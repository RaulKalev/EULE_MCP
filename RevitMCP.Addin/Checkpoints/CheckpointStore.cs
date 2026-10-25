using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace RevitMCP.Addin.Checkpoints;

/// <summary>
/// Checkpoints per model (#98): in memory while Revit runs, and on disk as
/// <c>&lt;folder&gt;\&lt;modelId&gt;\&lt;id&gt;.json</c> (meta, warnings, batches, identities) plus
/// <c>&lt;id&gt;.snapshot.json</c> (written once). Batches are saved debounced on a background thread, never
/// on the Revit API thread. A checkpoint loaded from disk has <see cref="Checkpoint.TrackingComplete"/> false:
/// changes made while the connector was not running were not seen. Pure — no Revit API, unit tested.
/// </summary>
public sealed class CheckpointStore : IDisposable
{
    public const int MaxPerModel = 20;
    public const int MaxRecordedIds = 500_000;
    public const int MaxIdentities = 50_000;
    public const int MaxTitleLength = 120;
    public static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(2);

    public const string RestartNote =
        "Revit or the connector was restarted since this checkpoint was created: changes made while it was not running are not listed; snapshot comparison is still exact.";
    public const string CapNote =
        "The change recording cap was reached: later changes are not listed; snapshot comparison is still exact.";

    private readonly string _folder;
    private readonly TimeSpan _saveDelay;
    private readonly Dictionary<string, List<Checkpoint>> _byModel = new(StringComparer.Ordinal);
    private readonly HashSet<Checkpoint> _dirty = new();
    private readonly List<Task> _writes = new();
    private readonly Timer _timer;
    private bool _disposed;

    /// <summary>Guards every checkpoint's mutable state; the change tracker records under it too.</summary>
    public object Gate { get; } = new();

    public CheckpointStore(string folder) : this(folder, SaveDelay) { }

    public CheckpointStore(string folder, TimeSpan saveDelay)
    {
        _folder = folder;
        _saveDelay = saveDelay;
        _timer = new Timer(_ => SaveDirty(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string Folder => _folder;

    /// <summary>Checkpoints of a model, oldest first; loads them from disk the first time.</summary>
    public List<Checkpoint> ForModel(string modelId)
    {
        lock (Gate) return Load(modelId).ToList();
    }

    /// <summary>True when the model has at least one checkpoint (loads it from disk the first time).</summary>
    public bool HasCheckpoints(string modelId)
    {
        lock (Gate) return Load(modelId).Count > 0;
    }

    public Checkpoint? Find(string modelId, string id)
    {
        if (!CheckpointIds.IsValid(id)) return null;
        lock (Gate) return Load(modelId).FirstOrDefault(c => c.Id == id);
    }

    /// <summary>The model a checkpoint id belongs to, looking in memory and on disk; null when unknown.</summary>
    public string? FindModelOf(string id)
    {
        if (!CheckpointIds.IsValid(id)) return null;
        lock (Gate)
        {
            foreach (var pair in _byModel)
                if (pair.Value.Any(c => c.Id == id)) return pair.Key;
            if (!Directory.Exists(_folder)) return null;
            foreach (var dir in Directory.GetDirectories(_folder))
            {
                var modelId = Path.GetFileName(dir);
                if (CheckpointIds.IsValidModelId(modelId) && File.Exists(MetaPath(modelId, id))) return modelId;
            }
            return null;
        }
    }

    /// <summary>
    /// Adds a checkpoint and saves it. Returns the checkpoints evicted to stay at <see cref="MaxPerModel"/>
    /// (oldest first). Throws <see cref="ArgumentException"/> for an invalid id or model id.
    /// </summary>
    public List<Checkpoint> Add(Checkpoint checkpoint)
    {
        if (!CheckpointIds.IsValid(checkpoint.Id)) throw new ArgumentException("Invalid checkpoint id.");
        if (!CheckpointIds.IsValidModelId(checkpoint.ModelId)) throw new ArgumentException("Invalid model id.");
        checkpoint.Title = NormalizeTitle(checkpoint.Title);

        var evicted = new List<Checkpoint>();
        string meta, snapshot;
        lock (Gate)
        {
            var list = Load(checkpoint.ModelId);
            list.Add(checkpoint);
            while (list.Count > MaxPerModel)
            {
                var oldest = list.OrderBy(c => c.CreatedAt).First();
                list.Remove(oldest);
                _dirty.Remove(oldest);
                evicted.Add(oldest);
            }
            meta = JsonConvert.SerializeObject(checkpoint, Formatting.None);
            snapshot = JsonConvert.SerializeObject(checkpoint.Snapshot, Formatting.None);
        }

        var model = checkpoint.ModelId;
        var id = checkpoint.Id;
        QueueWrite(() =>
        {
            Directory.CreateDirectory(ModelFolder(model));
            WriteAtomic(SnapshotPath(model, id), snapshot);
            WriteAtomic(MetaPath(model, id), meta);
            foreach (var old in evicted) DeleteFiles(old.ModelId, old.Id);
        });
        return evicted;
    }

    public bool Remove(string modelId, string id)
    {
        if (!CheckpointIds.IsValid(id)) return false;
        bool found;
        lock (Gate)
        {
            var list = Load(modelId);
            var cp = list.FirstOrDefault(c => c.Id == id);
            found = cp != null;
            if (cp != null)
            {
                list.Remove(cp);
                _dirty.Remove(cp);
            }
        }
        if (found) QueueWrite(() => DeleteFiles(modelId, id));
        return found;
    }

    /// <summary>
    /// Appends a batch to every checkpoint of the model and caches the identities given (up to
    /// <see cref="MaxIdentities"/> per checkpoint). Call under no lock; cheap enough for DocumentChanged.
    /// </summary>
    public void Record(string modelId, ChangeBatch batch, IReadOnlyDictionary<long, ElementIdentity>? identities)
    {
        if (batch.IdCount == 0) return;
        lock (Gate)
        {
            var list = Load(modelId);
            if (list.Count == 0) return;
            foreach (var cp in list)
            {
                if (cp.RecordedIdCount + batch.IdCount > MaxRecordedIds)
                {
                    if (cp.TrackingComplete || cp.TrackingNote != CapNote)
                    {
                        cp.TrackingComplete = false;
                        cp.TrackingNote = CapNote;
                        _dirty.Add(cp);
                    }
                    continue;
                }
                cp.Batches.Add(batch);
                if (identities != null)
                {
                    foreach (var pair in identities)
                    {
                        if (cp.Identities.Count >= MaxIdentities) break;
                        if (!cp.Identities.ContainsKey(pair.Key) && !cp.Snapshot.ContainsKey(pair.Key))
                            cp.Identities[pair.Key] = pair.Value;
                    }
                }
                _dirty.Add(cp);
            }
        }
        ScheduleSave();
    }

    /// <summary>
    /// The batch count of every checkpoint of the model, so the batches of a call that was rolled back whole (a
    /// plan violation, #99) can be dropped again with <see cref="ResetBatches"/>.
    /// </summary>
    public Dictionary<string, int> MarkBatches(string modelId)
    {
        lock (Gate) return Load(modelId).ToDictionary(cp => cp.Id, cp => cp.Batches.Count, StringComparer.Ordinal);
    }

    /// <summary>Drops batches recorded after <paramref name="mark"/>: the model is back where it was then.</summary>
    public void ResetBatches(string modelId, IReadOnlyDictionary<string, int> mark)
    {
        var changed = false;
        lock (Gate)
        {
            foreach (var cp in Load(modelId))
            {
                if (!mark.TryGetValue(cp.Id, out var count) || cp.Batches.Count <= count) continue;
                cp.Batches.RemoveRange(count, cp.Batches.Count - count);
                _dirty.Add(cp);
                changed = true;
            }
        }
        if (changed) ScheduleSave();
    }

    /// <summary>Ids that no checkpoint of the model has an identity for yet (so the tracker reads only those).</summary>
    public List<long> MissingIdentities(string modelId, IEnumerable<long> ids)
    {
        lock (Gate)
        {
            var list = Load(modelId);
            if (list.Count == 0) return new List<long>();
            return ids.Where(id => list.Any(cp => cp.Identities.Count < MaxIdentities &&
                                                  !cp.Identities.ContainsKey(id) &&
                                                  !cp.Snapshot.ContainsKey(id)))
                      .Distinct().ToList();
        }
    }

    /// <summary>Marks a checkpoint changed outside <see cref="Record"/> (e.g. a plan id) so it is saved.</summary>
    public void MarkChanged(Checkpoint checkpoint)
    {
        lock (Gate) _dirty.Add(checkpoint);
        ScheduleSave();
    }

    /// <summary>Writes everything pending now and waits for it. For shutdown and tests.</summary>
    public void Flush()
    {
        SaveDirty();
        Task[] pending;
        lock (_writes) pending = _writes.ToArray();
        try { Task.WaitAll(pending); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Flush();
        _timer.Dispose();
    }

    public static string NormalizeTitle(string? title)
    {
        var t = (title ?? string.Empty).Trim();
        if (t.Length == 0) t = "Checkpoint";
        return t.Length > MaxTitleLength ? t.Substring(0, MaxTitleLength) : t;
    }

    private List<Checkpoint> Load(string modelId)
    {
        if (_byModel.TryGetValue(modelId, out var list)) return list;
        list = new List<Checkpoint>();
        _byModel[modelId] = list;
        if (!CheckpointIds.IsValidModelId(modelId)) return list;

        var folder = ModelFolder(modelId);
        if (!Directory.Exists(folder)) return list;
        foreach (var file in Directory.GetFiles(folder, CheckpointIds.Prefix + "*.json"))
        {
            if (file.EndsWith(".snapshot.json", StringComparison.OrdinalIgnoreCase)) continue;
            var cp = ReadCheckpoint(modelId, file);
            if (cp != null) list.Add(cp);
        }
        list.Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
        return list;
    }

    private Checkpoint? ReadCheckpoint(string modelId, string file)
    {
        try
        {
            var cp = JsonConvert.DeserializeObject<Checkpoint>(File.ReadAllText(file, Encoding.UTF8));
            if (cp == null || !CheckpointIds.IsValid(cp.Id) || cp.Id != Path.GetFileNameWithoutExtension(file) || cp.ModelId != modelId)
                return null;
            var snapshotFile = SnapshotPath(modelId, cp.Id);
            if (File.Exists(snapshotFile))
                cp.Snapshot = JsonConvert.DeserializeObject<Dictionary<long, ElementSnapshot>>(File.ReadAllText(snapshotFile, Encoding.UTF8)) ?? new();
            cp.Batches ??= new();
            cp.Identities ??= new();
            cp.Warnings ??= new();
            cp.TrackingComplete = false;
            cp.TrackingNote ??= RestartNote;
            return cp;
        }
        catch
        {
            return null;
        }
    }

    private void ScheduleSave()
    {
        if (_disposed) return;
        try { _timer.Change(_saveDelay, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
    }

    private void SaveDirty()
    {
        List<(string Model, string Id, string Json)> work;
        lock (Gate)
        {
            if (_dirty.Count == 0) return;
            work = _dirty
                .Where(cp => _byModel.TryGetValue(cp.ModelId, out var list) && list.Contains(cp))
                .Select(cp => (cp.ModelId, cp.Id, JsonConvert.SerializeObject(cp, Formatting.None)))
                .ToList();
            _dirty.Clear();
        }
        QueueWrite(() =>
        {
            foreach (var w in work)
            {
                Directory.CreateDirectory(ModelFolder(w.Model));
                WriteAtomic(MetaPath(w.Model, w.Id), w.Json);
            }
        });
    }

    /// <summary>Writes run one after another on the thread pool, in the order queued.</summary>
    private void QueueWrite(Action write)
    {
        lock (_writes)
        {
            _writes.RemoveAll(t => t.IsCompleted);
            var previous = _writes.Count > 0 ? _writes[_writes.Count - 1] : Task.CompletedTask;
            _writes.Add(previous.ContinueWith(_ =>
            {
                try { write(); } catch { /* a failed save must never reach Revit */ }
            }, TaskScheduler.Default));
        }
    }

    private void DeleteFiles(string modelId, string id)
    {
        try { File.Delete(MetaPath(modelId, id)); } catch { }
        try { File.Delete(SnapshotPath(modelId, id)); } catch { }
    }

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }

    private string ModelFolder(string modelId) => Path.Combine(_folder, modelId);
    private string MetaPath(string modelId, string id) => Path.Combine(ModelFolder(modelId), id + ".json");
    private string SnapshotPath(string modelId, string id) => Path.Combine(ModelFolder(modelId), id + ".snapshot.json");
}
