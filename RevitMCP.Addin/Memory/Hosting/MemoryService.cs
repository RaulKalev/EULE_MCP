using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

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

    /// <summary>The project folder was guessed (no <c>.rktools</c> above the model): where memory went and how to pin it.</summary>
    public string? ProjectWarning { get; set; }
    public string? Model { get; set; }
    public string? ModelVersion { get; set; }
    public string User { get; set; } = string.Empty;
    public string? Client { get; set; }
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

    public MemoryRecallContext ForRecall(int staleAfterVersions = MemoryRecall.DefaultStaleAfterVersions) => new()
    {
        Project = Project,
        Model = Model,
        ModelVersion = ModelVersion,
        User = User,
        Now = Now,
        StaleAfterVersions = staleAfterVersions
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

    /// <summary>Ids of recalled entries this note relied on: each counts as used, and as worked when the note is approved.</summary>
    public List<string> BasedOn { get; set; } = new();

    /// <summary>
    /// A person approved this call in Revit (not Direct Edit). An approved project note is stored as confirmed, so it
    /// reaches everyone's hints at once; without approval it stays a candidate until it is confirmed.
    /// </summary>
    public bool Approved { get; set; }
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

    /// <summary>scope=company: the proposal file an administrator reviews.</summary>
    public string? ProposalPath { get; set; }

    /// <summary>basedOn ids that were found and credited.</summary>
    public int Credited { get; set; }
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

/// <summary>What a note is, independent of who asked for it: the agent, or the workflow observer.</summary>
internal sealed class MemoryNote
{
    public string Scope = MemoryScopes.Project;
    public string? Project;
    public string Key = string.Empty;
    public string Value = string.Empty;
    public string Reason = string.Empty;
    public List<string> Tags = new();
    public string? Stage;
    public string Source = MemorySources.Agent;
    public List<string> Authors = new();
    public MemoryMatch? Match;
    public List<string> Requires = new();
    public string? Connector;

    /// <summary>A fixed id, so two machines promoting the same pattern write the same file instead of two.</summary>
    public string? Id;

    /// <summary>Stored state: candidate, or confirmed when a person accepted the note on the review page.</summary>
    public string State = MemoryStates.Candidate;
}

/// <summary>
/// Shared memory for this Revit session: resolves the personal, project and company tiers for the open model, keeps
/// one <see cref="MemoryCache"/> (and its watcher) per tier folder, and runs remember / recall / list with the
/// write-time rules of <see cref="MemoryPolicy"/> and the lifecycle of <see cref="MemoryLifecycle"/>. Also hosts
/// the hint feedback and the workflow observer (the other parts of this class). The approval gate for project
/// writes is the dispatcher's; this class only does what an approved call asks. Pure — no Revit API.
/// </summary>
public sealed partial class MemoryService : IDisposable
{
    public const string ProposalExtension = ".memory.proposal.json";

    private readonly ConcurrentDictionary<string, MemoryCache> _caches = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _writeGate = new();
    private readonly Func<MemorySettings> _settings;
    private readonly Func<string, bool>? _toolExists;
    private readonly bool _watch;

    public MemoryService(string personalFolder, string mirrorRoot, Func<MemorySettings>? settings = null, bool watch = true,
        string? proposalsFolder = null, Func<string, bool>? toolExists = null)
    {
        PersonalFolder = personalFolder;
        MirrorRoot = mirrorRoot;
        ProposalsFolder = proposalsFolder ?? Path.Combine(Path.GetDirectoryName(personalFolder) ?? personalFolder, "MemoryProposals");
        _settings = settings ?? (() => MemorySettings.Load());
        _watch = watch;
        _toolExists = toolExists;
    }

    public static MemoryService? Current { get; private set; }

    /// <summary>The service used by the tools, with the standard folders. Called from App.OnStartup.</summary>
    public static MemoryService CreateDefault(Func<string, bool>? toolExists = null)
    {
        var service = new MemoryService(DefaultPersonalFolder(), DefaultMirrorRoot(), toolExists: toolExists);
        Current = service;
        return service;
    }

    public static string DefaultPersonalFolder() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RKTools", "RevitMCP", "Memory");

    public static string DefaultMirrorRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RKTools", "RevitMCP", "MemoryMirror");

    public string PersonalFolder { get; }
    public string MirrorRoot { get; }

    /// <summary>Company proposals land here, like skill proposals; an administrator applies them.</summary>
    public string ProposalsFolder { get; }
    public MemorySettings Settings => _settings();

    /// <summary>The connector version observed entries are bound to.</summary>
    public static string ConnectorVersion => typeof(MemoryService).Assembly.GetName().Version?.ToString() ?? "0";

    /// <summary>The project tier folder for a project root.</summary>
    public string ProjectFolderFor(string projectRoot) => Path.Combine(projectRoot, Settings.ProjectFolderName);

    // ── Remember ─────────────────────────────────────────────────────────────────────────────────────────

    public MemoryRememberResult Remember(MemoryContext context, MemoryRememberRequest request)
    {
        var settings = Settings;
        if (!settings.Enabled) return Refuse("not_available", "Shared memory is switched off (memory.enabled=false).");

        var scope = (request.Scope ?? string.Empty).Trim().ToLowerInvariant();
        if (scope.Length == 0) scope = MemoryScopes.Project;
        if (!MemoryScopes.IsKnown(scope))
            return Refuse("validation_failed", $"Unknown scope '{request.Scope}'. Use project, personal or company.");

        var source = (request.Source ?? string.Empty).Trim().ToLowerInvariant();
        if (source.Length == 0) source = MemorySources.Agent;
        if (source != MemorySources.Agent && source != MemorySources.Human)
            return Refuse("validation_failed", "source must be agent (your own note) or human (the user said it).");

        var topic = MemoryPolicy.NormalizeTopic(request.Key, context.Project);
        if (topic == null)
            return Refuse("validation_failed", "key is required: a topic path such as \"tags/detector-tag-type\".");

        var value = MemoryEntryParser.OneLine(request.Value);
        var reason = MemoryEntryParser.OneLine(request.Reason);
        var problems = MemoryPolicy.Validate(value, reason, settings.ValueMaxLength);
        if (problems.Count > 0)
            return new MemoryRememberResult { Status = "validation_failed", Message = "Not remembered: " + problems[0], Problems = problems };

        var author = UserOf(context);
        var project = scope == MemoryScopes.Company || (scope == MemoryScopes.Personal && request.AnyProject) ? null : Blank(context.Project);
        var note = new MemoryNote
        {
            Scope = scope,
            Project = project,
            Key = MemoryPolicy.FullKey(scope, project, topic),
            Value = value,
            Reason = reason,
            Tags = MemoryPolicy.NormalizeTags(request.Tags),
            Stage = Blank(request.Stage)?.ToUpperInvariant(),
            Source = source,
            Authors = new List<string> { author },
            // A person approved it at the Revit screen: that is the review the project tier asks for.
            State = scope == MemoryScopes.Project && request.Approved ? MemoryStates.Confirmed : MemoryStates.Candidate
        };

        var credited = Credit(context, request.BasedOn, scope == MemoryScopes.Project && request.Approved);
        if (scope == MemoryScopes.Company)
        {
            var proposal = Propose(context, note);
            proposal.Credited = credited;
            return proposal;
        }

        var cache = CacheFor(scope, context, out var unavailable);
        if (cache == null) return Refuse("not_available", unavailable!);
        var result = Write(cache, note, context);
        result.Credited = credited;
        if (result.Success && scope == MemoryScopes.Project && result.Entry != null)
        {
            var (state, _) = MemoryLifecycle.Effective(result.Entry, context.Now, settings, _toolExists);
            result.Message += state == MemoryStates.Confirmed
                ? " It is confirmed and shows in everyone's hints."
                : " It is a candidate: only you see it in hints until it is confirmed (approved in Revit, confirmed on revit_memory_page, or written by a second person).";
            if (context.ProjectWarning != null) result.Message += " " + context.ProjectWarning;
        }
        return result;
    }

    /// <summary>
    /// Writes a note into a tier with the merge rules: reaffirm the same value, supersede another, flag a flip-flop,
    /// respect the per-project cap. Shared by agent notes and the workflow observer.
    /// </summary>
    internal MemoryRememberResult Write(MemoryCache cache, MemoryNote note, MemoryContext context)
    {
        var settings = Settings;
        lock (_writeGate)
        {
            var snapshot = Read(cache, context.Now);
            var sameKey = snapshot.Entries.Where(e => string.Equals(e.Key, note.Key, StringComparison.OrdinalIgnoreCase)).ToList();
            var decision = MemoryPolicy.Decide(sameKey, note.Value, note.Authors.FirstOrDefault() ?? UserOf(context));
            var result = new MemoryRememberResult { Success = true, Kind = decision.Kind };

            if (decision.Kind == MemoryWriteKind.Reaffirm)
            {
                var entry = OnDisk(snapshot, decision.Existing!);
                entry.Counters.Remembered++;
                foreach (var a in note.Authors)
                    if (!entry.Authors.Contains(a, StringComparer.OrdinalIgnoreCase)) entry.Authors.Add(a);
                foreach (var t in note.Tags) if (!entry.Tags.Contains(t)) entry.Tags.Add(t);
                entry.Reason = note.Reason;
                entry.Updated = context.Now;
                // Approving the same value again confirms it, unless people disagree on it (the review page settles that).
                if (note.State == MemoryStates.Confirmed && entry.State == MemoryStates.Candidate && !entry.Flags.Contains(MemoryPolicy.FlagFlipFlop))
                    entry.State = MemoryStates.Confirmed;
                if (string.Equals(entry.Model, context.Model, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(context.ModelVersion))
                    entry.ModelVersion = context.ModelVersion;
                var location = cache.Store.Write(entry);
                cache.Invalidate();
                result.Entry = entry;
                result.Path = location.Path;
                result.Pending = location.Pending;
                result.Message = $"'{note.Key}' already said this; reaffirmed it (written {entry.Counters.Remembered + 1} times, by {entry.Authors.Count} author{(entry.Authors.Count == 1 ? "" : "s")}).";
                return result;
            }

            MemoryEntry? capVictim = null;
            if (decision.Kind == MemoryWriteKind.New)
            {
                var active = snapshot.Entries.Where(e => !e.IsRetired && SameProject(e.Project, note.Project)).ToList();
                capVictim = MemoryPolicy.EntryToRetireForCap(active, settings.MaxEntriesPerProject, context.Now, out var full);
                if (full)
                    return Refuse("memory_full",
                        $"This project already holds {active.Count} active memory entries (memory.maxEntriesPerProject={settings.MaxEntriesPerProject}) and none is dormant. Supersede an existing key instead.");
            }

            var created = NewEntry(note, context);
            // A fixed id must never overwrite a stored entry (history, or one a person retired).
            if (note.Id != null && snapshot.FileEntries.ContainsKey(note.Id)) created.Id = Guid.NewGuid().ToString();
            created.Supersedes = decision.Existing?.Id;
            if (decision.FlipFlop)
            {
                created.Flags.Add(MemoryPolicy.FlagFlipFlop);
                // One person's approval does not settle a disagreement between people.
                created.State = MemoryStates.Candidate;
            }

            var written = cache.Store.Write(created);
            result.Entry = created;
            result.Path = written.Path;
            result.Pending = written.Pending;

            // The new entry is written; a retire that fails now (a share hiccup) leaves the old entry active but older,
            // and recall and the next write of the key prefer the newest, so the note is not lost.
            string? retireProblem = null;
            if (decision.Existing != null)
            {
                var old = OnDisk(snapshot, decision.Existing);
                old.State = MemoryStates.Retired;
                old.Updated = context.Now;
                retireProblem = TryWrite(cache, old);
                if (retireProblem == null) result.Superseded = old;
            }
            if (capVictim != null)
            {
                var victim = OnDisk(snapshot, capVictim);
                victim.State = MemoryStates.Retired;
                victim.Updated = context.Now;
                retireProblem ??= TryWrite(cache, victim);
                result.RetiredForCap = victim;
            }
            cache.Invalidate();

            result.Message = decision.Kind == MemoryWriteKind.Supersede
                ? $"Remembered '{note.Key}'; it replaces the earlier value, which is kept as history." +
                  (decision.FlipFlop ? " The value went back and forth between people, so it is flagged as a disagreement for review." : string.Empty)
                : $"Remembered '{note.Key}' in {note.Scope} memory.";
            if (result.Pending) result.Message += " The project folder is unreachable: the entry waits in the local mirror and is copied when it returns.";
            if (retireProblem != null) result.Message += " The earlier entry could not be retired yet (" + retireProblem + "); the newer one wins.";
            return result;
        }
    }

    private static string? TryWrite(MemoryCache cache, MemoryEntry entry)
    {
        try
        {
            cache.Store.Write(entry);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    private static MemoryEntry NewEntry(MemoryNote note, MemoryContext context)
    {
        var author = note.Authors.FirstOrDefault() ?? UserOf(context);
        return new MemoryEntry
        {
            Id = note.Id ?? Guid.NewGuid().ToString(),
            Key = note.Key,
            Scope = note.Scope,
            Project = note.Project,
            Stage = note.Stage,
            Model = Blank(context.Model),
            ModelVersion = Blank(context.ModelVersion),
            Source = note.Source,
            Author = author,
            Authors = note.Authors.Count > 0 ? new List<string>(note.Authors) : new List<string> { author },
            Client = Blank(context.Client),
            Created = context.Now,
            Updated = context.Now,
            State = note.State,
            Tags = new List<string>(note.Tags),
            Requires = new List<string>(note.Requires),
            Match = note.Match,
            Connector = note.Connector,
            Value = note.Value,
            Reason = note.Reason
        };
    }

    /// <summary>
    /// scope=company: agents never write the company tier. The note becomes a proposal file in the user's proposals
    /// folder, holding the entry exactly as it would be stored, for an administrator to review and copy into
    /// <c>memory.companyFolder</c> — the same flow as skill proposals.
    /// </summary>
    private MemoryRememberResult Propose(MemoryContext context, MemoryNote note)
    {
        var entry = NewEntry(note, context);
        var settings = Settings;
        var proposal = new
        {
            kind = "memory.proposal",
            id = entry.Id,
            key = entry.Key,
            proposedAt = context.Now,
            proposedBy = entry.Author,
            client = entry.Client,
            fromProject = Blank(context.Project),
            fromModel = entry.Model,
            applyTo = settings.CompanyFolder,
            fileName = MemoryStore.FileName(entry.Id),
            howToApply = settings.CompanyFolder == null
                ? "Set memory.companyFolder in the company config, then save 'entry' there as 'fileName'."
                : $"Review, then save 'entry' as '{MemoryStore.FileName(entry.Id)}' in {settings.CompanyFolder}.",
            entry = MemoryEntryParser.Serialize(entry)
        };
        try
        {
            var path = MemoryStore.AtomicWrite(ProposalsFolder, entry.Id + ProposalExtension, JsonConvert.SerializeObject(proposal, Formatting.Indented));
            return new MemoryRememberResult
            {
                Success = true,
                Kind = MemoryWriteKind.New,
                Entry = entry,
                ProposalPath = path,
                Path = path,
                Message = $"Company memory is written by an administrator: proposed '{entry.Key}' in {path}."
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refuse("not_available", "The proposal could not be written: " + ex.Message);
        }
    }

    // ── Recall and list ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Ranked entries for a query across the tiers. Counts each returned entry as shown and watches what follows.</summary>
    public MemoryReadResult Recall(MemoryContext context, MemoryRecallOptions options, string? scope = null)
    {
        var read = ReadTiers(context, scope);
        if (read.Snapshots.Count == 0) return read.Result;

        var all = read.Snapshots.SelectMany(s => s.Snapshot.Entries);
        var hits = MemoryRecall.Rank(all, options, context.ForRecall(Settings.StaleAfterVersions));
        read.Result.Hits = hits;
        read.Result.Total = hits.Count;
        RecordShown(context, hits, read);
        return read.Result;
    }

    /// <summary>
    /// The memory block of revit_graph_route: confirmed entries (and the user's own candidates) matching the intent,
    /// at most <c>memory.hintLimit</c>. Null when hints are off.
    /// </summary>
    public MemoryReadResult? Hints(MemoryContext context, string query)
    {
        var settings = Settings;
        if (!settings.Enabled || !settings.Hints || string.IsNullOrWhiteSpace(query)) return null;
        return Recall(context, new MemoryRecallOptions
        {
            Query = query,
            Limit = settings.HintLimit,
            IncludeDormant = false,
            VisibleTo = UserOf(context)
        });
    }

    /// <summary>Entries newest first, filtered by state and tags; does not count as shown.</summary>
    public MemoryReadResult List(MemoryContext context, string? scope, string? state, IEnumerable<string>? tags, int offset, int limit)
    {
        var read = ReadTiers(context, scope);
        var tagList = MemoryPolicy.NormalizeTags(tags);
        var wantedState = Blank(state)?.ToLowerInvariant();
        var recall = context.ForRecall(Settings.StaleAfterVersions);

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
        var scopes = wanted == "all" ? new[] { MemoryScopes.Project, MemoryScopes.Company, MemoryScopes.Personal } : new[] { wanted };
        foreach (var s in scopes)
        {
            if (!MemoryScopes.IsKnown(s))
            {
                read.Result.Warnings.Add($"Unknown scope '{scope}'. Use all, project, company or personal.");
                continue;
            }
            var cache = CacheFor(s, context, out var unavailable);
            if (cache == null)
            {
                // No company folder configured is normal; only say so when the company tier was asked for.
                if (s != MemoryScopes.Company || wanted == MemoryScopes.Company) read.Result.Warnings.Add(unavailable!);
                continue;
            }
            var snapshot = Read(cache, context.Now);
            if (s == MemoryScopes.Project && context.ProjectWarning != null) read.Result.Warnings.Add(context.ProjectWarning);
            foreach (var error in snapshot.Errors) read.Result.Warnings.Add($"Skipped an unreadable entry: {error}");
            if (snapshot.FromMirror) read.Result.Warnings.Add($"The {s} memory folder is unreachable; read from the local mirror.");
            read.Snapshots.Add((cache, snapshot, s));
            read.Result.Tiers.Add(new MemoryTierHits { Scope = s, Folder = snapshot.Folder, FromMirror = snapshot.FromMirror, Entries = snapshot.Entries.Count });
        }
        return read;
    }

    /// <summary>A tier's entries with their usage tallies and effective lifecycle state.</summary>
    private MemorySnapshot Read(MemoryCache cache, DateTimeOffset now)
    {
        var snapshot = cache.Read();
        MemoryLifecycle.Apply(snapshot.Entries, now, Settings, _toolExists);
        return snapshot;
    }

    // ── Tiers ────────────────────────────────────────────────────────────────────────────────────────────

    internal MemoryCache? CacheFor(string scope, MemoryContext context, out string? unavailable)
    {
        unavailable = null;
        if (scope == MemoryScopes.Personal)
            return _caches.GetOrAdd(PersonalFolder, f => new MemoryCache(new MemoryStore(f), _watch));

        if (scope == MemoryScopes.Company)
        {
            var company = Settings.CompanyFolder;
            if (string.IsNullOrWhiteSpace(company))
            {
                unavailable = "No company memory is configured (memory.companyFolder in the company config).";
                return null;
            }
            return _caches.GetOrAdd(company!, f => new MemoryCache(new MemoryStore(f, Path.Combine(MirrorRoot, "_company")), _watch));
        }

        if (string.IsNullOrWhiteSpace(context.ProjectFolder))
        {
            unavailable = context.ProjectProblem ?? "Project memory needs a model saved in a project folder. Use scope=personal.";
            return null;
        }
        var folder = context.ProjectFolder!;
        var mirror = Path.Combine(MirrorRoot, MirrorSegment(context.Project, folder));
        return _caches.GetOrAdd(folder, f => new MemoryCache(new MemoryStore(f, mirror, context.ProjectRoot), _watch));
    }

    /// <summary>
    /// The mirror folder of a project tier: readable project key plus a hash of the full folder path, so two projects
    /// sharing a project number (Revit's default "Project Number") never share a mirror or replay into each other.
    /// </summary>
    internal static string MirrorSegment(string? project, string folder)
    {
        var slug = MemoryPolicy.Slug(project ?? string.Empty);
        if (slug.Length == 0) slug = "_unfiled";
        if (slug.Length > 40) slug = slug.Substring(0, 40);
        string full;
        try { full = Path.GetFullPath(folder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { full = folder; }
        return slug + "-" + DeterministicId(full.ToLowerInvariant()).Substring(0, 8);
    }

    /// <summary>The entry exactly as its file says: no tallies, stored state — what a rewrite starts from.</summary>
    private static MemoryEntry OnDisk(MemorySnapshot snapshot, MemoryEntry entry) =>
        snapshot.FileEntries.TryGetValue(entry.Id, out var stored) ? stored.Clone() : entry.Clone();

    /// <summary>A stable id from text: the same promoted pattern gets the same file on every machine.</summary>
    internal static string DeterministicId(string text)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes).ToString();
    }

    /// <summary>No state: everything but retired history. "all": everything. Otherwise that state only.</summary>
    private static bool StateMatches(MemoryEntry e, string? state) =>
        state == null ? !e.IsRetired : state == "all" || e.State == state;

    private static bool SameProject(string? a, string? b) =>
        string.Equals(MemoryPolicy.Slug(a ?? string.Empty), MemoryPolicy.Slug(b ?? string.Empty), StringComparison.Ordinal);

    internal static string UserOf(MemoryContext context) => string.IsNullOrWhiteSpace(context.User) ? Environment.UserName : context.User;

    internal static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s!.Trim();

    private static MemoryRememberResult Refuse(string status, string message) =>
        new() { Status = status, Message = message, Problems = new List<string> { message } };

    public void Dispose()
    {
        StopObserver();
        foreach (var cache in _caches.Values) cache.Dispose();
        _caches.Clear();
        if (ReferenceEquals(Current, this)) Current = null;
    }
}
