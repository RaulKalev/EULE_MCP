using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace RevitMCP.Addin.Memory.Hosting;

/// <summary>
/// The workflow observer's host. Completed calls are queued by <see cref="MemoryHooks"/> and processed on a background
/// task, one at a time: hint feedback first, then <see cref="WorkflowObserver"/>. A pattern over the support threshold
/// becomes a <b>personal</b> candidate (source <c>observed</c>); the user's patterns are also listed in
/// <c>.observed\&lt;user&gt;.json</c> in the project memory folder, and a pattern listed by two or more users is
/// promoted to the <b>project</b> tier under a fixed id, so every machine that notices writes the same file.
/// The counts themselves stay local in <c>%AppData%\…\Memory\.observer\&lt;project&gt;.json</c>.
/// </summary>
public sealed partial class MemoryService
{
    public const string ObserverFolderName = ".observer";
    public const string ObservedFolderName = ".observed";
    private const int MaxQueued = 1000;

    private readonly ConcurrentQueue<ObservedCall> _queue = new();
    private readonly object _observerGate = new();
    private readonly WorkflowObserver _observer = new();
    private readonly Dictionary<string, ObserverState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirtyStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string? Root, string? Problem, DateTimeOffset Found)> _roots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _explicitRoots = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan RootCacheTime = TimeSpan.FromMinutes(10);
    private int _draining;
    private volatile bool _stopped;

    /// <summary>Queues a completed call. O(1), never blocks, never throws.</summary>
    public void Observe(ObservedCall call)
    {
        try
        {
            if (_stopped || _queue.Count >= MaxQueued) return;
            _queue.Enqueue(call);
            if (Interlocked.CompareExchange(ref _draining, 1, 0) == 0) Task.Run(Drain);
        }
        catch
        {
            // Learning must never affect a tool call.
        }
    }

    private void Drain()
    {
        while (true)
        {
            while (!_stopped && _queue.TryDequeue(out var call))
            {
                try { ProcessObserved(call); }
                catch { }
            }
            try { FlushObserver(); }
            catch { }
            Interlocked.Exchange(ref _draining, 0);
            if (_stopped || _queue.IsEmpty || Interlocked.CompareExchange(ref _draining, 1, 0) != 0) return;
        }
    }

    /// <summary>Processes one call synchronously (the background task's body; called directly by tests).</summary>
    public List<ObservedPattern> ProcessObserved(ObservedCall call)
    {
        var settings = Settings;
        if (!settings.Enabled) return new List<ObservedPattern>();
        Feedback(call);
        if (!settings.ObserverEnabled || string.IsNullOrWhiteSpace(call.Project)) return new List<ObservedPattern>();

        List<ObservedPattern> patterns;
        var stateKey = MemoryPolicy.Slug(call.Project!);
        lock (_observerGate)
        {
            var state = StateFor(stateKey);
            patterns = _observer.Observe(state, call, settings.ObserverMinSupport, settings.ObserverMinSessions);
            _dirtyStates.Add(stateKey);
        }
        if (patterns.Count == 0) return patterns;

        var context = ObservedContext(call);
        var kept = new List<ObservedPattern>();
        var personal = CacheFor(MemoryScopes.Personal, context, out _)!;
        foreach (var pattern in patterns)
        {
            if (MemoryPolicy.Validate(pattern.Value, pattern.Reason, settings.ValueMaxLength).Count > 0) continue;
            Write(personal, ObservedNote(MemoryScopes.Personal, call.Project, pattern, new List<string> { context.User }), context);
            kept.Add(pattern);
        }
        if (kept.Count > 0 && context.ProjectFolder != null) PublishAndPromote(context, kept);
        return kept;
    }

    private static MemoryNote ObservedNote(string scope, string? project, ObservedPattern pattern, List<string> authors)
    {
        var topic = MemoryPolicy.NormalizeTopic(pattern.Topic) ?? "observed";
        return new MemoryNote
        {
            Scope = scope,
            Project = project,
            Key = MemoryPolicy.FullKey(scope, project, topic),
            Value = pattern.Value,
            Reason = pattern.Reason,
            Tags = MemoryPolicy.NormalizeTags(pattern.Tags),
            Source = MemorySources.Observed,
            Authors = authors,
            Match = pattern.Match,
            Requires = new List<string>(pattern.Requires),
            Connector = ConnectorVersion
        };
    }

    private MemoryContext ObservedContext(ObservedCall call)
    {
        var context = new MemoryContext
        {
            Project = call.Project,
            User = string.IsNullOrWhiteSpace(call.User) ? Environment.UserName : call.User,
            Client = Blank(call.Client),
            Now = call.Time
        };
        if (!string.IsNullOrWhiteSpace(call.ModelPath))
        {
            var (root, problem) = ProjectRootOf(call.ModelPath!);
            context.ProjectRoot = root;
            context.ProjectFolder = root == null ? null : ProjectFolderFor(root);
            context.ProjectProblem = problem;
        }
        return context;
    }

    /// <summary>A tool call passed projectRoot for this model: the observer files the model's patterns there too.</summary>
    public void NoteProjectRoot(string modelPath, string root)
    {
        if (!string.IsNullOrWhiteSpace(modelPath) && !string.IsNullOrWhiteSpace(root)) _explicitRoots[modelPath] = root;
    }

    /// <summary>
    /// The project root of a model: one a tool was given explicitly, else the lookup above the model file, kept for a
    /// few minutes only — a share that was briefly away must not pin the wrong folder for the whole session.
    /// </summary>
    private (string? Root, string? Problem) ProjectRootOf(string modelPath)
    {
        if (_explicitRoots.TryGetValue(modelPath, out var given)) return (given, null);
        if (_roots.TryGetValue(modelPath, out var cached) && DateTimeOffset.UtcNow - cached.Found < RootCacheTime)
            return (cached.Root, cached.Problem);
        var (root, problem, _) = MemoryProjectRoot.Find(modelPath, null, Settings.ProjectRootSkipFolders);
        _roots[modelPath] = (root, problem, DateTimeOffset.UtcNow);
        return (root, problem);
    }

    /// <summary>
    /// Lists the user's patterns in the shared <c>.observed</c> folder and promotes those that another user's list
    /// holds with the same value. Never throws.
    /// </summary>
    private void PublishAndPromote(MemoryContext context, List<ObservedPattern> patterns)
    {
        try
        {
            var shared = Path.Combine(context.ProjectFolder!, ObservedFolderName);
            var user = MemoryPolicy.Slug(context.User);
            if (user.Length == 0) user = "_unknown";
            var mine = ReadObserved(Path.Combine(shared, user + ".json"));
            foreach (var p in patterns) mine[p.Topic] = p;
            MemoryStore.AtomicWrite(shared, user + ".json", JsonConvert.SerializeObject(mine, Formatting.Indented));

            var everyone = new Dictionary<string, Dictionary<string, ObservedPattern>>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.GetFiles(shared, "*.json"))
                everyone[Path.GetFileNameWithoutExtension(file)] = ReadObserved(file);

            var project = CacheFor(MemoryScopes.Project, context, out _);
            if (project == null) return;
            foreach (var p in patterns)
            {
                var users = everyone
                    .Where(u => u.Value.TryGetValue(p.Topic, out var theirs) && MemoryPolicy.SameValue(theirs.Value, p.Value))
                    .Select(u => u.Key)
                    .OrderBy(u => u, StringComparer.Ordinal)
                    .ToList();
                if (users.Count < 2) continue;

                var note = ObservedNote(MemoryScopes.Project, context.Project, p, users);
                note.Reason = $"Observed for {users.Count} people: {p.Reason}";
                note.Id = DeterministicId(note.Key + "|" + MemoryRecall.Fold(p.Value));

                var sameValue = Read(project, context.Now).Entries
                    .Where(e => string.Equals(e.Key, note.Key, StringComparison.OrdinalIgnoreCase) && MemoryPolicy.SameValue(e.Value, p.Value))
                    .ToList();
                // A person retired this value: usage alone must not bring it back.
                if (sameValue.Any(e => e.IsRetired && e.Flags.Contains(FlagRetiredByReview))) continue;
                var existing = sameValue.FirstOrDefault(e => !e.IsRetired);
                if (existing != null && users.All(u => existing.Authors.Contains(u, StringComparer.OrdinalIgnoreCase))) continue;
                Write(project, note, context);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // The share may be away; the personal candidate is already written and the next sighting tries again.
        }
    }

    private static Dictionary<string, ObservedPattern> ReadObserved(string path)
    {
        try
        {
            if (!File.Exists(path)) return new Dictionary<string, ObservedPattern>(StringComparer.OrdinalIgnoreCase);
            var parsed = JsonConvert.DeserializeObject<Dictionary<string, ObservedPattern>>(File.ReadAllText(path, Encoding.UTF8));
            return parsed == null
                ? new Dictionary<string, ObservedPattern>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, ObservedPattern>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<string, ObservedPattern>(StringComparer.OrdinalIgnoreCase);
        }
    }

    // ── Observer state on disk ───────────────────────────────────────────────────────────────────────────

    private string StatePath(string stateKey) => Path.Combine(PersonalFolder, ObserverFolderName, stateKey + ".json");

    private ObserverState StateFor(string stateKey)
    {
        if (_states.TryGetValue(stateKey, out var state)) return state;
        state = new ObserverState();
        try
        {
            var path = StatePath(stateKey);
            if (File.Exists(path))
                state = JsonConvert.DeserializeObject<ObserverState>(File.ReadAllText(path, Encoding.UTF8)) ?? new ObserverState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            state = new ObserverState();
        }
        _states[stateKey] = state;
        return state;
    }

    /// <summary>Writes the observer counts that changed. Never throws.</summary>
    public void FlushObserver()
    {
        lock (_observerGate)
        {
            foreach (var key in _dirtyStates.ToList())
            {
                try
                {
                    MemoryStore.AtomicWrite(Path.Combine(PersonalFolder, ObserverFolderName), key + ".json", JsonConvert.SerializeObject(_states[key]));
                    _dirtyStates.Remove(key);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                }
            }
        }
    }

    private void StopObserver()
    {
        _stopped = true;
        try { FlushObserver(); }
        catch { }
    }
}
