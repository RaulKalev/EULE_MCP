using System.Collections.Concurrent;
using System.IO;

namespace RevitMCP.Addin.Memory.Hosting;

/// <summary>Where the open model's memory lives and who is asking. Built from the Revit document by the tools.</summary>
public sealed class MemoryContext
{
    /// <summary>Project number (or name) — the same key the model graph files projects under.</summary>
    public string? Project { get; set; }

    /// <summary>The project tier folder (<c>&lt;project root&gt;\.rktools\memory</c>); null when the model has no folder.</summary>
    public string? ProjectFolder { get; set; }

    /// <summary>The project root, which exists whenever the share is reachable.</summary>
    public string? ProjectRoot { get; set; }

    /// <summary>Why <see cref="ProjectFolder"/> is null.</summary>
    public string? ProjectProblem { get; set; }
    public string? Model { get; set; }
    public string? ModelVersion { get; set; }
    public string User { get; set; } = string.Empty;
    public string? Client { get; set; }
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

    public MemoryRecallContext ForRecall() => new()
    {
        Project = Project,
        Model = Model,
        ModelVersion = ModelVersion,
        User = User,
        Now = Now
    };
}

public sealed class MemoryRememberRequest
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string Scope { get; set; } = MemoryScopes.Project;
    public List<string> Tags { get; set; } = new();
    public string? Stage { get; set; }
    public string Source { get; set; } = MemorySources.Agent;

    /// <summary>Personal scope only: the note applies in every project, not just the open one.</summary>
    public bool AnyProject { get; set; }
}

public sealed class MemoryRememberResult
{
    public bool Success { get; set; }

    /// <summary>validation_failed | not_available | memory_full | null on success.</summary>
    public string? Status { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<string> Problems { get; set; } = new();
    public MemoryWriteKind Kind { get; set; }
    public MemoryEntry? Entry { get; set; }
    public MemoryEntry? Superseded { get; set; }
    public MemoryEntry? RetiredForCap { get; set; }
    public string? Path { get; set; }
    public bool Pending { get; set; }
}

public sealed class MemoryTierHits
{
    public string Scope { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;
    public bool FromMirror { get; set; }
    public int Entries { get; set; }
}

public sealed class MemoryReadResult
{
    public List<MemoryHit> Hits { get; set; } = new();
    public List<MemoryTierHits> Tiers { get; set; } = new();
    public List<string> Warnings { get; set; } = new();

    /// <summary>Entries matching before limit/offset (list only).</summary>
    public int Total { get; set; }
}

/// <summary>
/// Shared memory for this Revit session: resolves the personal and project tiers for the open model, keeps one
/// <see cref="MemoryCache"/> (and its watcher) per tier folder, and runs remember / recall / list with the
/// write-time rules of <see cref="MemoryPolicy"/>. The approval gate for project writes is the dispatcher's;
/// this class only does what an approved call asks. Pure — no Revit API, unit tested in RevitMCP.Tests.
/// </summary>
public sealed class MemoryService : IDisposable
{
    private readonly ConcurrentDictionary<string, MemoryCache> _caches = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _writeGate = new();
    private readonly Func<MemorySettings> _settings;
    private readonly bool _watch;

    public MemoryService(string personalFolder, string mirrorRoot, Func<MemorySettings>? settings = null, bool watch = true)
    {
        PersonalFolder = personalFolder;
        MirrorRoot = mirrorRoot;
        _settings = settings ?? (() => MemorySettings.Load());
        _watch = watch;
    }

    public static MemoryService? Current { get; private set; }

    /// <summary>The service used by the tools, with the standard folders. Called from App.OnStartup.</summary>
    public static MemoryService CreateDefault()
    {
        var service = new MemoryService(DefaultPersonalFolder(), DefaultMirrorRoot());
        Current = service;
        return service;
    }

    public static string DefaultPersonalFolder() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RKTools", "RevitMCP", "Memory");

    public static string DefaultMirrorRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RKTools", "RevitMCP", "MemoryMirror");

    public string PersonalFolder { get; }
    public string MirrorRoot { get; }
    public MemorySettings Settings => _settings();

    /// <summary>The project tier folder for a project root.</summary>
    public string ProjectFolderFor(string projectRoot) => Path.Combine(projectRoot, Settings.ProjectFolderName);

    // ── Remember ─────────────────────────────────────────────────────────────────────────────────────────

    public MemoryRememberResult Remember(MemoryContext context, MemoryRememberRequest request)
    {
        var settings = Settings;
        if (!settings.Enabled) return Refuse("not_available", "Shared memory is switched off (memory.enabled=false).");

        var scope = (request.Scope ?? string.Empty).Trim().ToLowerInvariant();
        if (scope.Length == 0) scope = MemoryScopes.Project;
        if (scope == MemoryScopes.Company)
            return Refuse("validation_failed", "The company tier is written by an administrator through proposals, which are not available yet. Use scope=project.");
        if (!MemoryScopes.IsKnown(scope))
            return Refuse("validation_failed", $"Unknown scope '{request.Scope}'. Use project or personal.");

        var source = (request.Source ?? string.Empty).Trim().ToLowerInvariant();
        if (source.Length == 0) source = MemorySources.Agent;
        if (source != MemorySources.Agent && source != MemorySources.Human)
            return Refuse("validation_failed", "source must be agent (your own note) or human (the user said it).");

        var project = scope == MemoryScopes.Personal && request.AnyProject ? null : Blank(context.Project);
        var topic = MemoryPolicy.NormalizeTopic(request.Key, context.Project);
        if (topic == null)
            return Refuse("validation_failed", "key is required: a topic path such as \"tags/detector-tag-type\".");

        var value = MemoryEntryParser.OneLine(request.Value);
        var reason = MemoryEntryParser.OneLine(request.Reason);
        var problems = MemoryPolicy.Validate(value, reason, settings.ValueMaxLength);
        if (problems.Count > 0)
            return new MemoryRememberResult { Status = "validation_failed", Message = "Not remembered: " + problems[0], Problems = problems };

        var cache = CacheFor(scope, context, out var unavailable);
        if (cache == null) return Refuse("not_available", unavailable!);

        var key = MemoryPolicy.FullKey(scope, project, topic);
        var tags = MemoryPolicy.NormalizeTags(request.Tags);
        var stage = Blank(request.Stage)?.ToUpperInvariant();
        var author = string.IsNullOrWhiteSpace(context.User) ? Environment.UserName : context.User;

        lock (_writeGate)
        {
            var snapshot = cache.Read();
            var sameKey = snapshot.Entries.Where(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase)).ToList();
            var decision = MemoryPolicy.Decide(sameKey, value, author);
            var result = new MemoryRememberResult { Success = true, Kind = decision.Kind };

            if (decision.Kind == MemoryWriteKind.Reaffirm)
            {
                // The file keeps its own counters; the tallies summed into the snapshot must not be written back.
                var entry = OnDisk(snapshot, decision.Existing!);
                entry.Counters.Remembered++;
                if (!entry.Authors.Contains(author, StringComparer.OrdinalIgnoreCase)) entry.Authors.Add(author);
                foreach (var t in tags) if (!entry.Tags.Contains(t)) entry.Tags.Add(t);
                entry.Reason = reason;
                entry.Updated = context.Now;
                if (string.Equals(entry.Model, context.Model, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(context.ModelVersion))
                    entry.ModelVersion = context.ModelVersion;
                var location = cache.Store.Write(entry);
                cache.Invalidate();
                result.Entry = entry;
                result.Path = location.Path;
                result.Pending = location.Pending;
                result.Message = $"'{key}' already said this; reaffirmed it (written {entry.Counters.Remembered + 1} times, by {entry.Authors.Count} author{(entry.Authors.Count == 1 ? "" : "s")}).";
                return result;
            }

            MemoryEntry? capVictim = null;
            if (decision.Kind == MemoryWriteKind.New)
            {
                var active = snapshot.Entries.Where(e => !e.IsRetired && SameProject(e.Project, project)).ToList();
                capVictim = MemoryPolicy.EntryToRetireForCap(active, settings.MaxEntriesPerProject, context.Now, out var full);
                if (full)
                    return Refuse("memory_full",
                        $"This project already holds {active.Count} active memory entries (memory.maxEntriesPerProject={settings.MaxEntriesPerProject}) and none is dormant. Supersede an existing key instead.");
            }

            var created = new MemoryEntry
            {
                Id = Guid.NewGuid().ToString(),
                Key = key,
                Scope = scope,
                Project = project,
                Stage = stage,
                Model = Blank(context.Model),
                ModelVersion = Blank(context.ModelVersion),
                Source = source,
                Author = author,
                Authors = new List<string> { author },
                Client = Blank(context.Client),
                Created = context.Now,
                Updated = context.Now,
                State = MemoryStates.Candidate,
                Supersedes = decision.Existing?.Id,
                Tags = tags,
                Value = value,
                Reason = reason
            };
            if (decision.FlipFlop) created.Flags.Add(MemoryPolicy.FlagFlipFlop);

            var written = cache.Store.Write(created);
            result.Entry = created;
            result.Path = written.Path;
            result.Pending = written.Pending;

            if (decision.Existing != null)
            {
                var old = OnDisk(snapshot, decision.Existing);
                old.State = MemoryStates.Retired;
                old.Updated = context.Now;
                cache.Store.Write(old);
                result.Superseded = old;
            }
            if (capVictim != null)
            {
                var victim = OnDisk(snapshot, capVictim);
                victim.State = MemoryStates.Retired;
                victim.Updated = context.Now;
                cache.Store.Write(victim);
                result.RetiredForCap = victim;
            }
            cache.Invalidate();

            result.Message = decision.Kind == MemoryWriteKind.Supersede
                ? $"Remembered '{key}'; it replaces the earlier value, which is kept as history." +
                  (decision.FlipFlop ? " The value went back and forth between people, so it is flagged as a disagreement for review." : string.Empty)
                : $"Remembered '{key}' in {scope} memory.";
            if (result.Pending) result.Message += " The project folder is unreachable: the entry waits in the local mirror and is copied when it returns.";
            return result;
        }
    }

    // ── Recall and list ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Ranked entries for a query across the personal and project tiers. Counts each returned entry as shown.</summary>
    public MemoryReadResult Recall(MemoryContext context, MemoryRecallOptions options, string? scope = null)
    {
        var read = ReadTiers(context, scope);
        if (read.Snapshots.Count == 0) return read.Result;

        var all = read.Snapshots.SelectMany(s => s.Snapshot.Entries);
        var hits = MemoryRecall.Rank(all, options, context.ForRecall());
        read.Result.Hits = hits;
        read.Result.Total = hits.Count;

        var user = string.IsNullOrWhiteSpace(context.User) ? Environment.UserName : context.User;
        foreach (var (cache, snapshot, _) in read.Snapshots)
        {
            var ids = new HashSet<string>(snapshot.Entries.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
            var shown = hits.Where(h => ids.Contains(h.Entry.Id)).Select(h => h.Entry.Id).ToList();
            if (shown.Count > 0 && !cache.Store.AddShown(user, shown))
                read.Result.Warnings.Add($"The usage tally in {snapshot.Folder} could not be updated.");
        }
        return read.Result;
    }

    /// <summary>Entries newest first, filtered by state and tags; does not count as shown.</summary>
    public MemoryReadResult List(MemoryContext context, string? scope, string? state, IEnumerable<string>? tags, int offset, int limit)
    {
        var read = ReadTiers(context, scope);
        var tagList = MemoryPolicy.NormalizeTags(tags);
        var wantedState = Blank(state)?.ToLowerInvariant();
        var recall = context.ForRecall();

        var matching = read.Snapshots
            .SelectMany(s => s.Snapshot.Entries)
            .Where(e => MemoryRecall.InProject(e, context.Project))
            .Where(e => StateMatches(e, wantedState))
            .Where(e => tagList.All(t => e.Tags.Contains(t)))
            .OrderByDescending(e => e.Updated)
            .ToList();

        read.Result.Total = matching.Count;
        read.Result.Hits = matching
            .Skip(Math.Max(0, offset))
            .Take(Math.Max(1, limit))
            .Select(e =>
            {
                var (stale, reason) = MemoryRecall.Staleness(e, recall);
                return new MemoryHit { Entry = e, Stale = stale, StaleReason = reason };
            })
            .ToList();
        return read.Result;
    }

    private sealed class TierRead
    {
        public MemoryReadResult Result { get; } = new();
        public List<(MemoryCache Cache, MemorySnapshot Snapshot, string Scope)> Snapshots { get; } = new();
    }

    private TierRead ReadTiers(MemoryContext context, string? scope)
    {
        var read = new TierRead();
        if (!Settings.Enabled)
        {
            read.Result.Warnings.Add("Shared memory is switched off (memory.enabled=false).");
            return read;
        }

        var wanted = Blank(scope)?.ToLowerInvariant() ?? "all";
        var scopes = wanted == "all" ? new[] { MemoryScopes.Project, MemoryScopes.Personal } : new[] { wanted };
        foreach (var s in scopes)
        {
            if (s == MemoryScopes.Company)
            {
                read.Result.Warnings.Add("The company tier is not available yet.");
                continue;
            }
            if (!MemoryScopes.IsKnown(s))
            {
                read.Result.Warnings.Add($"Unknown scope '{scope}'. Use all, project or personal.");
                continue;
            }
            var cache = CacheFor(s, context, out var unavailable);
            if (cache == null)
            {
                read.Result.Warnings.Add(unavailable!);
                continue;
            }
            var snapshot = cache.Read();
            foreach (var error in snapshot.Errors) read.Result.Warnings.Add($"Skipped an unreadable entry: {error}");
            if (snapshot.FromMirror) read.Result.Warnings.Add("The project folder is unreachable; read from the local mirror.");
            read.Snapshots.Add((cache, snapshot, s));
            read.Result.Tiers.Add(new MemoryTierHits { Scope = s, Folder = snapshot.Folder, FromMirror = snapshot.FromMirror, Entries = snapshot.Entries.Count });
        }
        return read;
    }

    // ── Tiers ────────────────────────────────────────────────────────────────────────────────────────────

    private MemoryCache? CacheFor(string scope, MemoryContext context, out string? unavailable)
    {
        unavailable = null;
        if (scope == MemoryScopes.Personal)
            return _caches.GetOrAdd(PersonalFolder, f => new MemoryCache(new MemoryStore(f), _watch));

        if (string.IsNullOrWhiteSpace(context.ProjectFolder))
        {
            unavailable = context.ProjectProblem ?? "Project memory needs a model saved in a project folder. Use scope=personal.";
            return null;
        }
        var folder = context.ProjectFolder!;
        var mirrorSegment = MemoryPolicy.Slug(context.Project ?? string.Empty);
        if (mirrorSegment.Length == 0) mirrorSegment = MemoryPolicy.Slug(Path.GetFileName(context.ProjectRoot ?? folder));
        if (mirrorSegment.Length == 0) mirrorSegment = "_unfiled";
        var mirror = Path.Combine(MirrorRoot, mirrorSegment);
        return _caches.GetOrAdd(folder, f => new MemoryCache(new MemoryStore(f, mirror, context.ProjectRoot), _watch));
    }

    /// <summary>The entry as stored in its file, without the tallies a snapshot sums in.</summary>
    private static MemoryEntry OnDisk(MemorySnapshot snapshot, MemoryEntry entry)
    {
        var copy = entry.Clone();
        if (snapshot.FileCounters.TryGetValue(entry.Id, out var c)) copy.Counters = c.Clone();
        return copy;
    }

    /// <summary>No state: everything but retired history. "all": everything. Otherwise that state only.</summary>
    private static bool StateMatches(MemoryEntry e, string? state) =>
        state == null ? !e.IsRetired : state == "all" || e.State == state;

    private static bool SameProject(string? a, string? b) =>
        string.Equals(MemoryPolicy.Slug(a ?? string.Empty), MemoryPolicy.Slug(b ?? string.Empty), StringComparison.Ordinal);

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s!.Trim();

    private static MemoryRememberResult Refuse(string status, string message) =>
        new() { Status = status, Message = message, Problems = new List<string> { message } };

    public void Dispose()
    {
        foreach (var cache in _caches.Values) cache.Dispose();
        _caches.Clear();
        if (ReferenceEquals(Current, this)) Current = null;
    }
}
