using System.Globalization;
using System.Security.Cryptography;
using Newtonsoft.Json;

namespace RevitMCP.Village;

/// <summary>
/// The notice board at the overlook: flyers posted by read tools, newest first. Flyers pinned
/// today stay until the local day changes; archived ones stay for
/// <see cref="VillageOptions.FlyerArchiveDays"/>; dismissed ones are deleted at once. Optionally
/// persisted as one small JSON file per model (ids and names only) so the board survives a Revit
/// restart. Thread-safe; every public member is guarded and never throws. No Revit API dependency.
/// </summary>
public sealed class VillageFlyerBoard
{
    public const int MaxArchived = 200;
    public const int SaveDelayMs = 500;
    public const long MaxStoreFileBytes = 8 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly List<VillageFlyer> _flyers = new();   // newest first
    private readonly HashSet<string> _dirtyModels = new(StringComparer.Ordinal);
    private readonly int _maxFlyers;
    private readonly int _archiveDays;
    private readonly string? _storeFolder;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeZoneInfo _zone;
    private readonly Action<string>? _log;
    private DateTime _day;
    private int _saveScheduled;

    public VillageFlyerBoard(
        int maxFlyers = 30,
        int archiveDays = 30,
        string? storeFolder = null,
        Func<DateTimeOffset>? clock = null,
        TimeZoneInfo? zone = null,
        Action<string>? log = null)
    {
        _maxFlyers = Math.Max(1, maxFlyers);
        _archiveDays = Math.Max(1, archiveDays);
        _storeFolder = string.IsNullOrWhiteSpace(storeFolder) ? null : storeFolder;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _zone = zone ?? TimeZoneInfo.Local;
        _log = log;
        _day = DayOf(_clock());
    }

    /// <summary>Raised after any change, outside the lock, on the thread that made it.</summary>
    public event Action? Changed;

    public int Count
    {
        get { lock (_gate) return _flyers.Count; }
    }

    // ─── Posting ────────────────────────────────────────────────────────────

    /// <summary>
    /// Pins a flyer for one read tool result. A repeat of the newest flyer — same tool, model and
    /// element ids, still pinned today — only moves its timestamp, so an agent paging or retrying
    /// does not fill the board. Returns the flyer on the board, or null when nothing was posted.
    /// </summary>
    public VillageFlyer? Post(string? toolName, string? clientName, VillageProjectContext? context, VillageFlyerExtraction extraction)
    {
        try
        {
            if (extraction == null || extraction.Items.Count == 0) return null;
            var ctx = context ?? VillageProjectContext.Empty;
            var now = _clock();
            var tool = VillageEventFactory.SanitizeToolName(toolName);
            var modelId = ctx.ComputeModelId();
            VillageFlyer posted;
            lock (_gate)
            {
                PruneLocked(now);
                var newest = _flyers.FirstOrDefault(f => f.State == VillageFlyerStates.New);
                if (newest != null && newest.Tool == tool && newest.ModelId == modelId && SameIds(newest.Items, extraction.Items))
                {
                    newest.UpdatedAt = now;
                    _flyers.Remove(newest);
                    _flyers.Insert(0, newest);
                    posted = newest;
                }
                else
                {
                    posted = new VillageFlyer
                    {
                        Id = NewId(),
                        Title = VillageFlyer.TitleFor(tool),
                        Tool = tool,
                        Client = VillageEventFactory.SanitizeClientName(clientName),
                        ModelId = modelId,
                        ProjectName = ctx.DisplayName,
                        CreatedAt = now,
                        UpdatedAt = now,
                        State = VillageFlyerStates.New,
                        Total = Math.Max(extraction.Total, extraction.Items.Count),
                        Truncated = extraction.Truncated,
                        Categories = VillageFlyer.CountCategories(extraction.Items),
                        Items = new List<VillageFlyerItem>(extraction.Items)
                    };
                    _flyers.Insert(0, posted);
                    EnforceCapsLocked();
                }
                _dirtyModels.Add(modelId);
            }
            AfterChange();
            return posted;
        }
        catch (Exception ex)
        {
            Log("post failed: " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    // ─── Reading ────────────────────────────────────────────────────────────

    /// <summary>Every flyer on the board without its items, newest first.</summary>
    public List<VillageFlyer> Summaries()
    {
        bool pruned;
        List<VillageFlyer> list;
        lock (_gate)
        {
            pruned = PruneLocked(_clock());
            list = _flyers.Select(f => f.Summary()).ToList();
        }
        if (pruned) AfterChange();
        return list;
    }

    /// <summary>One flyer with its items, or null.</summary>
    public VillageFlyer? Get(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_gate)
        {
            var f = _flyers.FirstOrDefault(x => x.Id == id);
            if (f == null) return null;
            var copy = f.Summary();
            copy.Items = f.Items == null ? new List<VillageFlyerItem>() : new List<VillageFlyerItem>(f.Items);
            return copy;
        }
    }

    // ─── User actions ───────────────────────────────────────────────────────

    public const string ActionArchive = "archive";
    public const string ActionUnarchive = "unarchive";
    public const string ActionDismiss = "dismiss";

    public static bool IsAction(string? action) =>
        action == ActionArchive || action == ActionUnarchive || action == ActionDismiss;

    /// <summary>Archive, unarchive (back to today's board) or dismiss one flyer. False when it does not exist.</summary>
    public bool Apply(string? id, string? action)
    {
        if (string.IsNullOrEmpty(id) || !IsAction(action)) return false;
        lock (_gate)
        {
            var f = _flyers.FirstOrDefault(x => x.Id == id);
            if (f == null) return false;
            var now = _clock();
            switch (action)
            {
                case ActionArchive:
                    f.State = VillageFlyerStates.Archived;
                    f.ArchivedAt = now;
                    break;
                case ActionUnarchive:
                    // Back on today's board: it is cleared with the rest of today's flyers.
                    f.State = VillageFlyerStates.New;
                    f.ArchivedAt = null;
                    f.UpdatedAt = now;
                    if (DayOf(f.CreatedAt) != DayOf(now)) f.CreatedAt = now;
                    break;
                case ActionDismiss:
                    _flyers.Remove(f);
                    break;
            }
            _dirtyModels.Add(f.ModelId);
            EnforceCapsLocked();
        }
        AfterChange();
        return true;
    }

    /// <summary>Clears yesterday's flyers once the local day has changed. Cheap enough to call on every tick.</summary>
    public void Tick()
    {
        bool pruned;
        lock (_gate)
        {
            var now = _clock();
            if (DayOf(now) == _day) return;
            pruned = PruneLocked(now);
        }
        if (pruned) AfterChange();
    }

    // ─── Retention ──────────────────────────────────────────────────────────

    private bool PruneLocked(DateTimeOffset now)
    {
        var today = DayOf(now);
        _day = today;
        var removed = _flyers.RemoveAll(f =>
        {
            var expired = f.State == VillageFlyerStates.Archived
                ? (now - (f.ArchivedAt ?? f.CreatedAt)).TotalDays >= _archiveDays
                : DayOf(f.CreatedAt) != today;
            if (expired) _dirtyModels.Add(f.ModelId);
            return expired;
        });
        return removed > 0;
    }

    private void EnforceCapsLocked()
    {
        Trim(VillageFlyerStates.New, _maxFlyers);
        Trim(VillageFlyerStates.Archived, MaxArchived);

        void Trim(string state, int cap)
        {
            var count = 0;
            for (var i = 0; i < _flyers.Count; i++)
            {
                if (_flyers[i].State != state) continue;
                if (++count <= cap) continue;
                _dirtyModels.Add(_flyers[i].ModelId);
                _flyers.RemoveAt(i--);
            }
        }
    }

    private DateTime DayOf(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, _zone).Date;

    private static bool SameIds(List<VillageFlyerItem>? a, List<VillageFlyerItem> b)
    {
        if (a == null || a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (a[i].Id != b[i].Id) return false;
        return true;
    }

    private static string NewId()
    {
        var bytes = new byte[8];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        return "f" + string.Concat(bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
    }

    /// <summary>Flyer ids are "f" + 16 hex digits; anything else is refused before a lookup.</summary>
    public static bool IsValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id!.Length > 40) return false;
        foreach (var c in id)
            if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9')) return false;
        return true;
    }

    private void AfterChange()
    {
        ScheduleSave();
        try { Changed?.Invoke(); }
        catch (Exception ex) { Log("subscriber error: " + ex.Message); }
    }

    // ─── Persistence ────────────────────────────────────────────────────────

    /// <summary>Loads every model file in the store folder, then drops what has expired.</summary>
    public void Load()
    {
        if (_storeFolder == null) return;
        try
        {
            if (!Directory.Exists(_storeFolder)) return;
            var loaded = new List<VillageFlyer>();
            foreach (var file in Directory.GetFiles(_storeFolder, "*.json").Take(500))
            {
                try
                {
                    if (new FileInfo(file).Length > MaxStoreFileBytes) continue;
                    var list = JsonConvert.DeserializeObject<List<VillageFlyer>>(File.ReadAllText(file));
                    if (list == null) continue;
                    loaded.AddRange(list.Where(f => f != null && IsValidId(f.Id) &&
                        (f.State == VillageFlyerStates.New || f.State == VillageFlyerStates.Archived)));
                }
                catch (Exception ex)
                {
                    Log("flyer file skipped (" + Path.GetFileName(file) + "): " + ex.Message);
                }
            }
            lock (_gate)
            {
                var known = new HashSet<string>(_flyers.Select(f => f.Id), StringComparer.Ordinal);
                _flyers.AddRange(loaded.Where(f => known.Add(f.Id)));
                _flyers.Sort((x, y) => y.UpdatedAt.CompareTo(x.UpdatedAt));
                PruneLocked(_clock());
                EnforceCapsLocked();
            }
        }
        catch (Exception ex)
        {
            Log("flyer load failed: " + ex.Message);
        }
    }

    /// <summary>Writes every changed model file now. The board otherwise saves shortly after each change.</summary>
    public void Flush()
    {
        if (_storeFolder == null) return;
        List<(string ModelId, List<VillageFlyer> Flyers)> work;
        lock (_gate)
        {
            if (_dirtyModels.Count == 0) return;
            work = _dirtyModels
                .Select(m => (m, _flyers.Where(f => f.ModelId == m).ToList()))
                .ToList();
            _dirtyModels.Clear();
            // Serialize under the lock: the flyers are mutable.
            for (var i = 0; i < work.Count; i++)
                work[i] = (work[i].ModelId, JsonConvert.DeserializeObject<List<VillageFlyer>>(JsonConvert.SerializeObject(work[i].Flyers))!);
        }

        try { Directory.CreateDirectory(_storeFolder); }
        catch (Exception ex) { Log("flyer folder unavailable: " + ex.Message); return; }

        foreach (var (modelId, flyers) in work)
        {
            var path = Path.Combine(_storeFolder, SafeFileName(modelId) + ".json");
            try
            {
                if (flyers.Count == 0)
                {
                    if (File.Exists(path)) File.Delete(path);
                    continue;
                }
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(flyers));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception ex)
            {
                Log("flyer save failed (" + modelId + "): " + ex.Message);
            }
        }
    }

    private void ScheduleSave()
    {
        if (_storeFolder == null) return;
        if (Interlocked.Exchange(ref _saveScheduled, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(SaveDelayMs).ConfigureAwait(false); }
            catch { }
            Interlocked.Exchange(ref _saveScheduled, 0);
            try { Flush(); }
            catch (Exception ex) { Log("flyer save failed: " + ex.Message); }
        });
    }

    private static string SafeFileName(string modelId)
    {
        var chars = modelId.Where(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-').Take(40).ToArray();
        return chars.Length == 0 ? "no-document" : new string(chars);
    }

    private void Log(string message)
    {
        try { _log?.Invoke("[village-flyers] " + message); } catch { }
    }
}
