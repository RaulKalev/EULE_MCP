using System.Collections;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Village;

/// <summary>What the last graph tool response told the connector, kept for the freshness display.</summary>
public sealed class VillageGraphHint
{
    public string? DatabasePath { get; set; }
    public bool? Stale { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset ReportedAt { get; set; }
    public string Source { get; set; } = string.Empty;
}

/// <summary>
/// Ties the pieces together on one background consumer: hooks push sanitized events into the
/// bounded queue (never blocking), the consumer folds them through the aggregator, refreshes the
/// graph snapshot on a timer or after graph tools, and hands serialized messages to subscribers
/// (the SSE server). Nothing here can reach back into MCP or Revit. No Revit API dependency.
/// </summary>
public sealed class VillageStateHub : IDisposable
{
    /// <summary>State snapshots are pushed at most this often while events keep arriving.</summary>
    public const int StatePushIntervalMs = 250;
    /// <summary>Consumer wake-up interval when idle (drives Tick for idle/disconnect transitions).</summary>
    public const int IdleTickMs = 500;
    /// <summary>Most events folded per loop iteration before a state push is considered.</summary>
    public const int DrainBatch = 500;

    private static readonly string[] GraphTools =
    {
        "revit_graph_status", "revit_graph_build", "revit_graph_summary", "revit_graph_query"
    };

    private readonly VillageOptions _options;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<VillageProjectContext, VillageGraphHint?, VillageGraphSource>? _graphSourceProvider;
    private readonly Func<List<VillageInstanceLink>>? _instancesProvider;
    /// <summary>Keys of the warehouse models available (see VillageModelLibrary.ModelKey), or null.</summary>
    private readonly Func<ISet<string>?>? _warehouseModelKeys;
    private string _warehouseModelStamp = string.Empty;
    private readonly IVillageGraphReader? _graphReader;
    private readonly Action<string>? _log;
    private readonly int _catchUpEnterDepth;
    /// <summary>Same rules the graph reader's scorer uses; only the category lists matter here.</summary>
    private readonly VillageThemeConfig _themes;

    /// <summary>
    /// Revit category name → warehouse id, rebuilt on the consumer thread whenever the yard
    /// changes and read (lock-free) on the hook thread. Replaced wholesale, never mutated in
    /// place, so a hook always sees a complete map.
    /// </summary>
    private volatile Dictionary<string, string> _warehouseByCategory =
        new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cts;
    private Task? _consumer;
    private volatile VillageProjectContext _context = VillageProjectContext.Empty;
    private volatile string _contextModelId = VillageModelId.NoDocument;
    private volatile VillageGraphHint? _graphHint;
    private long _lastWriteTicks;
    private int _graphRefreshRequested;
    private volatile string _snapshotJson = "{}";
    private volatile string _instancesJson = "[]";
    private volatile string _contentsJson = "{}";
    private DateTimeOffset _lastStatePush = DateTimeOffset.MinValue;
    private DateTimeOffset _lastGraphRefresh = DateTimeOffset.MinValue;
    private bool _stateDirty;
    private long _hookFailures;

    public VillageStateHub(
        VillageOptions? options = null,
        VillageEventFactory? factory = null,
        VillageAggregator? aggregator = null,
        VillageEventQueue? queue = null,
        IVillageGraphReader? graphReader = null,
        Func<VillageProjectContext, VillageGraphHint?, VillageGraphSource>? graphSourceProvider = null,
        Func<List<VillageInstanceLink>>? instancesProvider = null,
        Func<DateTimeOffset>? clock = null,
        Action<string>? log = null,
        Func<ISet<string>?>? warehouseModelKeys = null,
        VillageFlyerBoard? flyers = null)
    {
        _options = options ?? VillageOptions.Default;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        var classifier = new VillageToolClassifier(_options.ToolAreas, _options.ToolActivities);
        Factory = factory ?? new VillageEventFactory(classifier: classifier, clock: _clock, warehouseResolver: ResolveWarehouse);
        Aggregator = aggregator ?? new VillageAggregator(_options, _clock);
        Queue = queue ?? new VillageEventQueue(_options.QueueSize, _options.MaxEventsPerSecond);
        _graphReader = graphReader;
        _graphSourceProvider = graphSourceProvider;
        _instancesProvider = instancesProvider;
        _warehouseModelKeys = warehouseModelKeys;
        Flyers = flyers;
        _log = log;
        _catchUpEnterDepth = Math.Max(50, _options.QueueSize / 10);
        _themes = VillageThemeConfig.FromJson(_options.ThemesJson);

        Aggregator.StepEmitted += OnStepEmitted;
        Aggregator.StateChanged += () => _stateDirty = true;
        Aggregator.SetProject(_context, Factory.SessionId);
        _snapshotJson = JsonConvert.SerializeObject(Aggregator.Snapshot());
    }

    /// <summary>
    /// The "has a warehouse model" test for the yard, or null to admit every category: when the
    /// option is off, or no warehouse models are available. The stamp changes whenever the set
    /// of models does.
    /// </summary>
    private Func<string, bool>? WarehouseModelFilter(out string stamp)
    {
        stamp = string.Empty;
        if (!_options.WarehouseModelsOnly || _warehouseModelKeys == null) return null;
        ISet<string>? keys;
        try { keys = _warehouseModelKeys(); }
        catch { keys = null; }
        if (keys == null || keys.Count == 0) return null;
        stamp = string.Join("|", keys.OrderBy(k => k, StringComparer.Ordinal));
        var set = keys;
        return category => set.Contains(VillageModelLibrary.ModelKey(category));
    }

    public VillageEventFactory Factory { get; }
    public VillageAggregator Aggregator { get; }
    public VillageEventQueue Queue { get; }

    /// <summary>The notice board read tool results are pinned to, or null when flyers are off.</summary>
    public VillageFlyerBoard? Flyers { get; }
    public string SessionId => Factory.SessionId;
    public bool IsRunning => _consumer != null && !_consumer.IsCompleted;
    public long HookFailures => Interlocked.Read(ref _hookFailures);

    /// <summary>Serialized messages for viewers: (event name, JSON). Raised on the consumer thread.</summary>
    public event Action<string, string>? MessagePublished;

    /// <summary>Latest serialized state snapshot; safe to read from any thread.</summary>
    public string SnapshotJson => _snapshotJson;

    /// <summary>Latest serialized instance list; safe to read from any thread.</summary>
    public string InstancesJson => _instancesJson;

    /// <summary>
    /// What each warehouse holds, keyed by warehouse id. Kept out of the state snapshot, which is
    /// pushed several times a second; the viewer fetches it when a warehouse is selected.
    /// </summary>
    public string ContentsJson => _contentsJson;

    public VillageProjectContext CurrentContext => _context;
    public VillageGraphHint? GraphHint => _graphHint;

    // ─── Lifecycle ──────────────────────────────────────────────────────────

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Publish(Factory.SessionStarted(_context));
        RequestGraphRefresh();
        _consumer = Task.Run(() => ConsumeLoopAsync(token), CancellationToken.None);
    }

    public void Stop()
    {
        var cts = _cts;
        if (cts == null) return;
        try { cts.Cancel(); } catch { }
        try { _consumer?.Wait(2000); } catch { }
        _cts = null;
        _consumer = null;
        try { Aggregator.Flush(); } catch { }
    }

    public void Dispose() => Stop();

    // ─── Hooks (any thread; O(1); never throw) ───────────────────────────────

    /// <summary>Queues a prebuilt event. Returns false when it was dropped.</summary>
    public bool Publish(VillageEvent e)
    {
        try { return Queue.TryEnqueue(e); }
        catch { Interlocked.Increment(ref _hookFailures); return false; }
    }

    /// <summary>
    /// Category name → warehouse id, or null when the yard has no warehouse for it. Called from
    /// the hook thread, so it only reads the immutable map published by the consumer thread.
    /// </summary>
    public string? ResolveWarehouse(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return null;
        return _warehouseByCategory.TryGetValue(category!.Trim(), out var id) ? id : null;
    }

    public void ToolStarted(string? toolName, string? clientName, VillageProjectContext? context, IReadOnlyDictionary<string, object?>? arguments = null)
    {
        try
        {
            var ctx = UpdateContext(context);
            Queue.TryEnqueue(Factory.ToolStarted(toolName, clientName, ctx, arguments));
        }
        catch { Interlocked.Increment(ref _hookFailures); }
    }

    public void ToolCompleted(string? toolName, string? clientName, bool success, string? status, long durationMs, object? resultData, VillageProjectContext? context, IReadOnlyDictionary<string, object?>? arguments = null)
    {
        try
        {
            var ctx = UpdateContext(context);
            var e = Factory.ToolCompleted(toolName, clientName, success, status, durationMs, resultData, ctx, arguments);
            Queue.TryEnqueue(e);

            if (success && VillageActivities.IsWrite(e.Activity))
            {
                // Re-evaluate freshness soon (the file is unchanged, so the reader answers from its cache).
                Interlocked.Exchange(ref _lastWriteTicks, _clock().UtcTicks);
                RequestGraphRefresh();
            }

            if (success && e.ToolName != null && Array.IndexOf(GraphTools, e.ToolName) >= 0)
            {
                _graphHint = ExtractGraphHint(e.ToolName, resultData, _clock());
                RequestGraphRefresh();
            }

            if (success && IsFlyerActivity(e.Activity)) PostFlyer(e.ToolName, clientName, ctx, resultData);
        }
        catch { Interlocked.Increment(ref _hookFailures); }
    }

    /// <summary>Reads, searches, analyses and checks can post a flyer; writes, exports and graph builds never do.</summary>
    public static bool IsFlyerActivity(string? activity) =>
        activity == VillageActivities.Inspect || activity == VillageActivities.Search ||
        activity == VillageActivities.Analyze || activity == VillageActivities.Validate;

    /// <summary>
    /// Pins the elements a read tool returned. The result is walked once, here, for ids and names
    /// (see <see cref="VillageFlyerExtractor"/>); it is not kept, and a result without elements
    /// posts nothing.
    /// </summary>
    private void PostFlyer(string? toolName, string? clientName, VillageProjectContext ctx, object? resultData)
    {
        var board = Flyers;
        if (board == null || !_options.FlyersEnabled || resultData == null) return;
        var found = VillageFlyerExtractor.Extract(resultData, _options.FlyerMaxItems);
        if (found.Items.Count > 0) board.Post(toolName, clientName, ctx, found);
    }

    /// <summary>Called by the host when the active document changes outside a tool call (optional).</summary>
    public void ProjectChanged(VillageProjectContext? context)
    {
        try { UpdateContext(context); }
        catch { Interlocked.Increment(ref _hookFailures); }
    }

    public void RequestGraphRefresh() => Interlocked.Exchange(ref _graphRefreshRequested, 1);

    private VillageProjectContext UpdateContext(VillageProjectContext? context)
    {
        if (context == null) return _context;
        var id = context.ComputeModelId();
        _context = context;
        if (!string.Equals(id, _contextModelId, StringComparison.Ordinal))
        {
            _contextModelId = id;
            Queue.TryEnqueue(Factory.ProjectChanged(context));
            _graphHint = null;
            Interlocked.Exchange(ref _lastWriteTicks, 0);
            RequestGraphRefresh();
        }
        return context;
    }

    /// <summary>Shallow read of the few graph-tool result fields the village needs; never retains the result.</summary>
    public static VillageGraphHint? ExtractGraphHint(string toolName, object? data, DateTimeOffset now)
    {
        if (data == null) return null;
        var hint = new VillageGraphHint { ReportedAt = now, Source = toolName };
        var path = Field(data, "databasePath") as string;
        if (!string.IsNullOrWhiteSpace(path)) hint.DatabasePath = path;

        var stale = Field(data, "stale");
        if (stale is bool b) hint.Stale = b;
        else if (stale is JValue jv && jv.Type == JTokenType.Boolean) hint.Stale = jv.Value<bool>();

        var reason = Field(data, "stale_reason") as string;
        if (reason == null && Field(data, "stale_reason") is JValue rv && rv.Type == JTokenType.String) reason = rv.Value<string>();
        if (!string.IsNullOrWhiteSpace(reason)) hint.Reason = VillageEventSerializer.Truncate(reason);

        if (toolName == "revit_graph_build" && hint.Stale == null) hint.Stale = false;
        return hint;
    }

    private static object? Field(object data, string name)
    {
        try
        {
            switch (data)
            {
                case JObject j:
                {
                    var t = j[name];
                    if (t == null) return null;
                    if (t.Type == JTokenType.String) return t.Value<string>();
                    if (t.Type == JTokenType.Boolean) return t.Value<bool>();
                    return t;
                }
                case IDictionary<string, object?> d:
                    return d.TryGetValue(name, out var v) ? v : null;
                case IDictionary plain:
                    return plain.Contains(name) ? plain[name] : null;
                case string:
                case ValueType:
                case IEnumerable:
                    return null;
            }
            var prop = data.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            return prop != null && prop.GetIndexParameters().Length == 0 ? prop.GetValue(data) : null;
        }
        catch
        {
            return null;
        }
    }

    // ─── Consumer ───────────────────────────────────────────────────────────

    private async Task ConsumeLoopAsync(CancellationToken ct)
    {
        var batch = new List<VillageEvent>(DrainBatch);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Queue.WaitForItemsAsync(IdleTickMs, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) break;
                RunOnce(batch);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log("consumer error: " + ex.GetType().Name + ": " + ex.Message);
                try { await Task.Delay(IdleTickMs, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>One consumer iteration. Public so tests can drive the hub deterministically without the background task.</summary>
    public void RunOnce(List<VillageEvent>? batch = null)
    {
        batch ??= new List<VillageEvent>(DrainBatch);
        var depth = Queue.Count;
        if (depth >= _catchUpEnterDepth) Aggregator.SetCatchUp(true);

        batch.Clear();
        Queue.DrainTo(batch, DrainBatch);
        foreach (var e in batch)
        {
            try { Aggregator.Process(e); }
            catch (Exception ex) { Log("aggregation error: " + ex.GetType().Name + ": " + ex.Message); }
        }

        if (Aggregator.CatchUp && Queue.Count == 0) Aggregator.SetCatchUp(false);

        if (Aggregator.Tick()) _stateDirty = true;
        Flyers?.Tick();
        MaybeRefreshGraph();

        var now = _clock();
        if (_stateDirty && (now - _lastStatePush).TotalMilliseconds >= StatePushIntervalMs)
            PushState(now);
    }

    /// <summary>Forces a state push (used after graph refreshes and by tests).</summary>
    public void PushState() => PushState(_clock());

    private void PushState(DateTimeOffset now)
    {
        Aggregator.QueueStats = Queue.Stats;
        var snapshot = Aggregator.Snapshot();
        var json = JsonConvert.SerializeObject(snapshot);
        _snapshotJson = json;
        _lastStatePush = now;
        _stateDirty = false;
        Raise("state", json);
    }

    private void OnStepEmitted(VillageStoryStep step)
    {
        try { Raise("step", JsonConvert.SerializeObject(step)); }
        catch (Exception ex) { Log("step publish error: " + ex.Message); }
    }

    private void Raise(string eventName, string json)
    {
        try { MessagePublished?.Invoke(eventName, json); }
        catch (Exception ex) { Log("subscriber error: " + ex.GetType().Name + ": " + ex.Message); }
    }

    // ─── Graph refresh ──────────────────────────────────────────────────────

    private void MaybeRefreshGraph()
    {
        var now = _clock();
        var requested = Interlocked.Exchange(ref _graphRefreshRequested, 0) == 1;
        var due = (now - _lastGraphRefresh).TotalSeconds >= _options.GraphRefreshSeconds;
        if (!requested && !due) return;
        _lastGraphRefresh = now;

        RefreshInstances();
        if (_graphReader == null || _graphSourceProvider == null) return;

        try
        {
            var context = _context;
            var hint = _graphHint;
            var source = _graphSourceProvider(context, hint);
            source.Hint = new VillageFreshnessHint
            {
                Stale = hint?.Stale,
                Reason = hint?.Reason ?? string.Empty,
                ReportedAt = hint?.ReportedAt,
                Source = hint?.Source ?? string.Empty,
                LastWriteAt = LastWriteAt()
            };

            var result = _graphReader.Read(source);
            TrackBaseline(result.Snapshot);
            var modelId = context.ComputeModelId();
            var theme = result.Theme;
            if (theme != null) theme.Identity = VillageThemeScorer.IdentityFor(modelId);

            var graphToken = JToken.FromObject(result.Snapshot);
            var themeToken = theme == null ? null : JToken.FromObject(theme);
            // The yard also depends on which warehouse models exist; a model folder that finishes
            // syncing after the first read has to rebuild it even though the graph did not change.
            var modelFilter = WarehouseModelFilter(out var modelStamp);
            var changed = result.Changed || !JToken.DeepEquals(Aggregator.Graph, graphToken) || modelStamp != _warehouseModelStamp;
            if (!changed) return;
            _warehouseModelStamp = modelStamp;

            Aggregator.Graph = graphToken;
            Aggregator.Theme = themeToken;
            Aggregator.Buildings = VillageLayoutSizer.Apply(VillageLayout.Default, result.Snapshot, theme);
            // One warehouse per category with elements; none at all when the graph is missing.
            var yard = VillageWarehouseYard.Plan(result.Snapshot.Categories, _themes, _options.MaxWarehouses, _options.WarehouseExcludeCategories, modelFilter);
            if (_baselineCategories != null)
                foreach (var warehouse in yard)
                    warehouse.Delta = warehouse.Count - (_baselineCategories.TryGetValue(warehouse.Category, out var before) ? before : 0);
            Aggregator.SetWarehouses(yard);
            var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var warehouse in Aggregator.Warehouses) lookup[warehouse.Category] = warehouse.Id;
            _warehouseByCategory = lookup;
            var contents = new Dictionary<string, VillageWarehouseContents>(StringComparer.Ordinal);
            foreach (var warehouse in Aggregator.Warehouses)
                if (result.Contents.TryGetValue(warehouse.Category, out var held)) contents[warehouse.Id] = held;
            foreach (var landmark in result.LandmarkContents) contents[landmark.Key] = landmark.Value;
            _contentsJson = JsonConvert.SerializeObject(contents);
            if (result.Changed)
                Aggregator.Process(Factory.GraphRefreshed(context, result.Snapshot.Exists && result.Snapshot.Error == null, result.Snapshot.Counts.Nodes));
            _stateDirty = true;
            PushState(now);
        }
        catch (Exception ex)
        {
            Log("graph refresh error: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    // ─── Change since the previous build ────────────────────────────────────

    private string? _trackedModel;
    private string? _trackedBuiltAt;
    private VillageGraphCounts? _trackedCounts;
    private Dictionary<string, long>? _trackedCategories;
    private string? _baselineBuiltAt;
    private VillageGraphCounts? _baselineCounts;
    private Dictionary<string, long>? _baselineCategories;

    /// <summary>
    /// Remembers the counts of each distinct graph build seen this session and stamps the snapshot
    /// with the change since the build before it. Idempotent for a snapshot that has not changed,
    /// so the cached result of an unchanged file keeps the same delta. Memory only.
    /// </summary>
    private void TrackBaseline(VillageGraphSnapshot snapshot)
    {
        if (!snapshot.Exists || snapshot.Error != null || string.IsNullOrEmpty(snapshot.BuiltAt)) return;

        // A different model is a different history: never diff across documents.
        if (!string.Equals(_trackedModel, snapshot.ModelName, StringComparison.Ordinal))
        {
            _trackedModel = snapshot.ModelName;
            _trackedBuiltAt = null; _trackedCounts = null; _trackedCategories = null;
            _baselineBuiltAt = null; _baselineCounts = null; _baselineCategories = null;
        }

        if (!string.Equals(_trackedBuiltAt, snapshot.BuiltAt, StringComparison.Ordinal))
        {
            if (_trackedCounts != null)
            {
                _baselineBuiltAt = _trackedBuiltAt;
                _baselineCounts = _trackedCounts;
                _baselineCategories = _trackedCategories;
            }
            _trackedBuiltAt = snapshot.BuiltAt;
            _trackedCounts = JsonConvert.DeserializeObject<VillageGraphCounts>(JsonConvert.SerializeObject(snapshot.Counts));
            _trackedCategories = snapshot.Categories.ToDictionary(c => c.Name, c => c.Count, StringComparer.OrdinalIgnoreCase);
        }

        if (_baselineCounts == null) return;
        var now = snapshot.Counts;
        var then = _baselineCounts;
        snapshot.PreviousBuiltAt = _baselineBuiltAt;
        snapshot.Delta = new VillageGraphCounts
        {
            Nodes = now.Nodes - then.Nodes, Edges = now.Edges - then.Edges, Elements = now.Elements - then.Elements,
            Types = now.Types - then.Types, Sheets = now.Sheets - then.Sheets, Views = now.Views - then.Views,
            Schedules = now.Schedules - then.Schedules, Levels = now.Levels - then.Levels, Spaces = now.Spaces - then.Spaces,
            Worksets = now.Worksets - then.Worksets, Panels = now.Panels - then.Panels, Circuits = now.Circuits - then.Circuits,
            Tags = now.Tags - then.Tags
        };
    }

    private void RefreshInstances()
    {
        if (_instancesProvider == null) return;
        try
        {
            var list = _instancesProvider() ?? new List<VillageInstanceLink>();
            Aggregator.Instances = list;
            _instancesJson = JsonConvert.SerializeObject(list);
        }
        catch (Exception ex)
        {
            Log("instance list error: " + ex.Message);
        }
    }

    private DateTimeOffset? LastWriteAt()
    {
        var ticks = Interlocked.Read(ref _lastWriteTicks);
        return ticks == 0 ? (DateTimeOffset?)null : new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private void Log(string message)
    {
        if (!_options.DiagnosticLogging) return;
        try { _log?.Invoke(message); } catch { }
    }
}
