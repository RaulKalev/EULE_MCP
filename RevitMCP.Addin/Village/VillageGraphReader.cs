using System.Diagnostics;
using System.IO;
using RevitMCP.Addin.Graph;
using RevitMCP.Village;

namespace RevitMCP.Addin.Village;

/// <summary>
/// Reads a model graph strictly for visualization, honouring the shared-folder rules:
/// the published file is never opened directly (it is copied into a village-owned cache through
/// <see cref="GraphStore.OpenForRead"/>, which tolerates atomic replacement and never holds the
/// shared file), the copy is opened read-only, connections are closed immediately, and results
/// are cached until the file's size or timestamp changes. Never throws; failures are reported in
/// <see cref="VillageGraphSnapshot.Error"/>. No Revit API dependency.
/// </summary>
public sealed class VillageGraphReader : IVillageGraphReader
{
    /// <summary>Category rows requested from the summary (the graph caps at 200).</summary>
    public const int CategoryTopN = 200;
    /// <summary>Most type nodes examined for keyword evidence.</summary>
    public const int MaxTypes = 2000;
    /// <summary>Most view nodes examined when counting schedules.</summary>
    public const int MaxViews = 5000;
    /// <summary>Instances counted per matched type (neighbour query limit).</summary>
    public const int MaxInstancesPerType = 500;
    private const int PageSize = 500;

    private readonly GraphStore _store;
    private readonly VillageThemeScorer _scorer;
    private readonly Func<DateTimeOffset> _clock;

    private string? _lastPath;
    private long _lastLength = -1;
    private DateTime _lastWriteUtc;
    private VillageGraphReadResult? _last;

    public VillageGraphReader(string cacheRoot, VillageThemeScorer? scorer = null, Func<DateTimeOffset>? clock = null)
    {
        _store = new GraphStore(cacheRoot);
        _scorer = scorer ?? new VillageThemeScorer();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string CacheRoot => _store.LocalRoot;

    /// <summary>Finds the graph file for the source, or null. Explicit path first, then a one-level search under each root.</summary>
    public static string? Locate(VillageGraphSource source)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(source.DatabasePath) && File.Exists(source.DatabasePath))
                return Path.GetFullPath(source.DatabasePath!);

            var model = GraphPathResolver.SanitizeSegment(source.ModelName, "_untitled");
            var fileName = model + GraphSchema.FileExtension;
            string? best = null;
            var bestWrite = DateTime.MinValue;

            foreach (var root in source.Roots)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                string full;
                try { full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(root.Trim())); }
                catch { continue; }
                if (!Directory.Exists(full)) continue;

                foreach (var dir in EnumerateCandidateDirectories(full))
                {
                    var candidate = Path.Combine(dir, fileName);
                    if (!File.Exists(candidate)) continue;
                    var write = File.GetLastWriteTimeUtc(candidate);
                    if (best == null || write > bestWrite)
                    {
                        best = candidate;
                        bestWrite = write;
                    }
                }
            }
            return best;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateCandidateDirectories(string root)
    {
        yield return root;
        string[] children;
        try { children = Directory.GetDirectories(root); }
        catch { yield break; }
        Array.Sort(children, StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var child in children)
        {
            var name = Path.GetFileName(child);
            if (name.Equals("tmp", StringComparison.OrdinalIgnoreCase) || name.Equals("cache", StringComparison.OrdinalIgnoreCase))
                continue;
            yield return child;
            if (++count >= 500) yield break;
        }
    }

    /// <summary>Reads (or returns the cached) snapshot. Never throws.</summary>
    public VillageGraphReadResult Read(VillageGraphSource source)
    {
        var now = _clock();
        var path = Locate(source);

        if (path == null)
        {
            var missing = new VillageGraphReadResult
            {
                Snapshot = VillageGraphSnapshot.Missing(now, source.Hint),
                Changed = _last == null || _last.Snapshot.Exists
            };
            missing.Snapshot.RootSource = source.RootSource;
            missing.Snapshot.ModelName = source.ModelName;
            Remember(null, -1, DateTime.MinValue, missing);
            return missing;
        }

        long length;
        DateTime writeUtc;
        try
        {
            var info = new FileInfo(path);
            length = info.Length;
            writeUtc = info.LastWriteTimeUtc;
        }
        catch (Exception ex)
        {
            return Failure(source, path, now, "Graph file could not be inspected: " + ex.GetType().Name);
        }

        if (_last != null && _last.Snapshot.Exists && string.Equals(_lastPath, path, StringComparison.OrdinalIgnoreCase) &&
            _lastLength == length && _lastWriteUtc == writeUtc)
        {
            // Unchanged file: only the freshness (hint may have moved) is recomputed.
            _last.Snapshot.Freshness = VillageGraphFreshness.Evaluate(true, _last.Snapshot.BuiltAt, source.Hint, now);
            _last.Changed = false;
            return _last;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var handle = _store.OpenForRead(path);
            var snapshot = new VillageGraphSnapshot
            {
                Exists = true,
                FileName = Path.GetFileName(path),
                RootSource = source.RootSource,
                UsedCache = handle.UsedLocalCache,
                ReadAt = VillageEventSerializer.FormatTimestamp(now)
            };
            VillageThemeResult theme;

            using (var db = GraphDatabase.OpenReadOnly(handle.ReadPath))
            {
                var meta = db.ReadMeta();
                snapshot.ModelName = meta.ModelName;
                snapshot.SchemaVersion = meta.SchemaVersion;
                snapshot.BuiltAt = meta.BuiltAt;
                snapshot.IsWorkshared = ParseBool(meta.Get(GraphSchema.MetaKeys.IsWorkshared));
                snapshot.Counts.Nodes = meta.NodeCount ?? 0;
                snapshot.Counts.Edges = meta.EdgeCount ?? 0;
                snapshot.Counts.Elements = meta.ElementCount ?? 0;

                var summary = db.Summarize(CategoryTopN, sampleSize: 0);
                var kinds = summary.NodesByKind.ToDictionary(k => k.Key, k => k.Count, StringComparer.Ordinal);
                var rels = summary.EdgesByRel.ToDictionary(k => k.Key, k => k.Count, StringComparer.Ordinal);
                snapshot.Counts.Types = Get(kinds, GraphSchema.Kinds.Type);
                snapshot.Counts.Sheets = Get(kinds, GraphSchema.Kinds.Sheet);
                snapshot.Counts.Views = Get(kinds, GraphSchema.Kinds.View);
                snapshot.Counts.Levels = Get(kinds, GraphSchema.Kinds.Level);
                snapshot.Counts.Spaces = Get(kinds, GraphSchema.Kinds.Space);
                snapshot.Counts.Worksets = Get(kinds, GraphSchema.Kinds.Workset);
                snapshot.Counts.Panels = Get(kinds, GraphSchema.Kinds.Panel);
                snapshot.Counts.Circuits = Get(kinds, GraphSchema.Kinds.Circuit);
                snapshot.Counts.Tags = Get(rels, GraphSchema.Rels.TaggedIn);
                if (snapshot.Counts.Elements == 0) snapshot.Counts.Elements = Get(kinds, GraphSchema.Kinds.Element);
                if (snapshot.Counts.Nodes == 0) snapshot.Counts.Nodes = kinds.Values.Sum();

                snapshot.Health.CircuitsWithoutPanel = summary.CircuitsWithoutPanelCount;
                snapshot.Health.CircuitsWithoutElements = summary.CircuitsWithoutElementsCount;
                snapshot.Health.ElementsWithoutLocation = summary.ElementsWithoutLocationCount;

                snapshot.Categories = summary.ElementsByCategory
                    .Where(c => !string.IsNullOrEmpty(c.Key))
                    .Take(25)
                    .Select(c => new VillageThemeEvidence { Name = c.Key, Count = c.Count })
                    .ToList();
                snapshot.Levels = summary.ElementsByLevel
                    .Where(c => !string.IsNullOrEmpty(c.Key))
                    .Take(25)
                    .Select(c => new VillageThemeEvidence { Name = c.Key, Count = c.Count })
                    .ToList();

                snapshot.Counts.Schedules = CountSchedules(db);

                var input = new VillageThemeInput
                {
                    ModelId = source.ModelName, // replaced by the hub with the real model id; kept deterministic here
                    ElementsByCategory = summary.ElementsByCategory
                        .Where(c => !string.IsNullOrEmpty(c.Key))
                        .Select(c => new KeyValuePair<string, long>(c.Key, c.Count))
                        .ToList(),
                    Types = SampleTypes(db)
                };
                theme = _scorer.Score(input);
            }

            sw.Stop();
            snapshot.ReadMs = sw.ElapsedMilliseconds;
            snapshot.Freshness = VillageGraphFreshness.Evaluate(true, snapshot.BuiltAt, source.Hint, now);

            var result = new VillageGraphReadResult { Snapshot = snapshot, Theme = theme, Changed = true, DatabasePath = path };
            Remember(path, length, writeUtc, result);
            return result;
        }
        catch (Exception ex)
        {
            return Failure(source, path, now, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private VillageGraphReadResult Failure(VillageGraphSource source, string path, DateTimeOffset now, string error)
    {
        var snapshot = new VillageGraphSnapshot
        {
            Exists = true,
            FileName = SafeFileName(path),
            RootSource = source.RootSource,
            ModelName = source.ModelName,
            Error = error,
            ReadAt = VillageEventSerializer.FormatTimestamp(now),
            Freshness = new VillageGraphFreshness
            {
                Status = VillageGraphFreshnessStatus.Unknown,
                Reason = "The graph file could not be read: " + error,
                Source = "graph file",
                CheckedAt = VillageEventSerializer.FormatTimestamp(now)
            }
        };
        var result = new VillageGraphReadResult { Snapshot = snapshot, Changed = true, DatabasePath = path };
        Remember(null, -1, DateTime.MinValue, result);
        return result;
    }

    private void Remember(string? path, long length, DateTime writeUtc, VillageGraphReadResult result)
    {
        _lastPath = path;
        _lastLength = length;
        _lastWriteUtc = writeUtc;
        _last = result;
    }

    private static long CountSchedules(GraphDatabase db)
    {
        long schedules = 0;
        var examined = 0;
        for (var page = 0; examined < MaxViews; page++)
        {
            var result = db.Find(GraphSchema.Kinds.View, null, null, null, null, page, PageSize);
            foreach (var node in result.Items)
            {
                examined++;
                if (node.Extra != null && node.Extra.IndexOf("\"isSchedule\":true", StringComparison.OrdinalIgnoreCase) >= 0)
                    schedules++;
                else if (node.Extra != null && node.Extra.IndexOf("\"isSchedule\":\"true\"", StringComparison.OrdinalIgnoreCase) >= 0)
                    schedules++;
            }
            if (!result.HasMore || result.Items.Count == 0) break;
        }
        return schedules;
    }

    /// <summary>Type names with instance counts. Bounded by <see cref="MaxTypes"/> and <see cref="MaxInstancesPerType"/>.</summary>
    private static List<VillageTypeSample> SampleTypes(GraphDatabase db)
    {
        var samples = new List<VillageTypeSample>();
        var examined = 0;
        for (var page = 0; examined < MaxTypes; page++)
        {
            var result = db.Find(GraphSchema.Kinds.Type, null, null, null, null, page, PageSize);
            foreach (var node in result.Items)
            {
                examined++;
                if (examined > MaxTypes) break;
                var instances = db.Neighbors(node.Id, GraphSchema.Rels.TypeOf, "in", MaxInstancesPerType).Count;
                if (instances > 0)
                    samples.Add(new VillageTypeSample { Name = node.Name, InstanceCount = instances });
            }
            if (!result.HasMore || result.Items.Count == 0) break;
        }
        return samples;
    }

    private static long Get(Dictionary<string, long> map, string key) =>
        map.TryGetValue(key, out var v) ? v : 0;

    private static bool? ParseBool(string? value) =>
        bool.TryParse(value, out var b) ? b : (bool?)null;

    private static string? SafeFileName(string path)
    {
        try { return Path.GetFileName(path); }
        catch { return null; }
    }
}
