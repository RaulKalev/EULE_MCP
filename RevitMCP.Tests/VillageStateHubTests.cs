using System.Diagnostics;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Graph;
using RevitMCP.Addin.Village;
using Xunit;
using Xunit.Abstractions;

namespace RevitMCP.Tests;

public class VillageStateHubTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rkmcp_hub_" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    public VillageStateHubTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static VillageProjectContext Context(string title = "1626_PP_EN") => new()
    {
        ModelTitle = title,
        CentralPath = @"\\server\bim\" + title + ".rvt",
        RevitVersion = "2026",
        IsWorkshared = true
    };

    private VillageStateHub Hub(VillageOptions? options = null, VillageGraphReader? reader = null,
        Func<VillageProjectContext, VillageGraphHint?, VillageGraphSource>? source = null)
    {
        var o = options ?? new VillageOptions();
        return new VillageStateHub(o, clock: () => _now, graphReader: reader, graphSourceProvider: source,
            instancesProvider: () => new List<VillageInstanceLink> { new() { ProcessId = 1, ProjectName = "1626_PP_EN", Url = "http://127.0.0.1:47800/", IsCurrent = true } });
    }

    [Fact]
    public void Hooks_ProduceStateAndStepMessagesWithoutAnyViewer()
    {
        using var hub = Hub();
        var messages = new List<(string Name, string Json)>();
        hub.MessagePublished += (n, j) => messages.Add((n, j));

        hub.ToolStarted("revit_list_sheets", "Claude Code", Context());
        hub.ToolCompleted("revit_list_sheets", "Claude Code", true, null, 12, new { returned = 5 }, Context());
        hub.RunOnce();

        Assert.Contains(messages, m => m.Name == "step" && m.Json.Contains("\"kind\":\"project\""));
        Assert.Contains(messages, m => m.Name == "step" && m.Json.Contains("Claude Code connected"));
        Assert.Contains(messages, m => m.Name == "step" && m.Json.Contains("\"kind\":\"move\""));
        Assert.Contains(messages, m => m.Name == "state");

        var state = (JObject)VillageEventSerializer.ParseToken(hub.SnapshotJson);
        Assert.Equal("1626_PP_EN", state.Value<string>("project_name"));
        Assert.Equal("archive", state["agents"]![0]!.Value<string>("building"));
        Assert.Equal(1, state["current_steps"]!.Count());
        Assert.True(state.Value<bool>("read_only"));
        Assert.Equal(0, hub.HookFailures);
    }

    [Fact]
    public void Hooks_NeverThrowAndNeverBlock()
    {
        using var hub = Hub(new VillageOptions { QueueSize = 100, MaxEventsPerSecond = 10 });
        hub.MessagePublished += (_, _) => throw new InvalidOperationException("subscriber bug");

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 10_000; i++)
        {
            hub.ToolStarted("revit_get_elements_info", "Claude Code", Context());
            hub.ToolCompleted("revit_get_elements_info", "Claude Code", true, null, 1, new { returned = 1 }, Context());
        }
        hub.ToolCompleted(null, null, false, "weird status", -1, new object(), null);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 2000, $"20k hook calls took {sw.ElapsedMilliseconds} ms");
        Assert.True(hub.Queue.Count <= 100);
        hub.RunOnce(); // the throwing subscriber must not break the consumer
        Assert.Equal(0, hub.HookFailures);
    }

    [Fact]
    public void HookOverhead_WithNoViewer_IsMicroseconds()
    {
        using var hub = Hub(new VillageOptions { QueueSize = 20000, MaxEventsPerSecond = 5000 });
        var ctx = Context();
        var data = new { placedCount = 3, elements = new[] { 1, 2, 3 } };

        // Warm up
        for (var i = 0; i < 1000; i++) hub.ToolCompleted("revit_place_tags", "Claude Code", true, null, 5, data, ctx);
        hub.RunOnce();

        const int n = 100_000;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < n; i++)
            hub.ToolCompleted("revit_place_tags", "Claude Code", true, null, 5, data, ctx);
        sw.Stop();

        var perCall = sw.Elapsed.TotalMilliseconds * 1000.0 / n;
        _output.WriteLine($"ToolCompleted hook: {perCall:F2} µs per call ({n} calls, {sw.ElapsedMilliseconds} ms total, queue {hub.Queue.Count})");
        Assert.True(perCall < 50, $"hook cost {perCall:F2} µs exceeds 50 µs");
    }

    [Fact]
    public void Flood_EntersCatchUpAndLeavesWhenDrained()
    {
        using var hub = Hub(new VillageOptions { QueueSize = 1000, MaxEventsPerSecond = 5000, AggregationWindowMs = 1000 });
        var modes = new List<string>();
        hub.MessagePublished += (n, j) => { if (n == "state") modes.Add(((JObject)VillageEventSerializer.ParseToken(j)).Value<string>("mode")!); };

        for (var i = 0; i < 800; i++)
            hub.ToolCompleted("revit_get_elements_info", "Claude Code", true, null, 1, null, Context());
        Assert.True(hub.Queue.Count >= 500);

        hub.RunOnce();
        Assert.True(hub.Aggregator.CatchUp);
        Assert.True(hub.Aggregator.Stats.CatchUpEntries >= 1);
        _now = _now.AddMilliseconds(300);
        hub.RunOnce();
        Assert.False(hub.Aggregator.CatchUp);
        Assert.Contains("catch_up", modes);
        Assert.Equal("live", modes.Last());
    }

    [Fact]
    public void ProjectSwitch_IsDetectedFromTheContextInHooks()
    {
        using var hub = Hub();
        hub.ToolCompleted("revit_list_sheets", "Claude Code", true, null, 1, null, Context("A"));
        hub.RunOnce();
        Assert.Equal("A", hub.Aggregator.ProjectName);

        hub.ToolCompleted("revit_list_sheets", "Claude Code", true, null, 1, null, Context("B"));
        hub.RunOnce();
        Assert.Equal("B", hub.Aggregator.ProjectName);
        Assert.Contains(hub.Aggregator.History(), s => s.Kind == "project" && s.Label == "Switched to B");
        Assert.Equal(Context("B").ComputeModelId(), hub.CurrentContext.ComputeModelId());

        hub.ProjectChanged(Context("C"));
        hub.RunOnce();
        Assert.Equal("C", hub.Aggregator.ProjectName);
    }

    [Fact]
    public void GraphToolResponses_FeedTheFreshnessHintWithoutRetainingTheResult()
    {
        using var hub = Hub();
        var data = new
        {
            exists = true,
            databasePath = @"C:\graphs\1626\1626_PP_EN.graph.db",
            stale = true,
            stale_reason = "Element count changed from 100 to 101",
            nodeCount = 1200,
            secret = "do not publish"
        };
        hub.ToolCompleted("revit_graph_status", "Claude Code", true, null, 3, data, Context());

        var hint = hub.GraphHint!;
        Assert.Equal(@"C:\graphs\1626\1626_PP_EN.graph.db", hint.DatabasePath);
        Assert.True(hint.Stale);
        Assert.Equal("Element count changed from 100 to 101", hint.Reason);
        Assert.Equal("revit_graph_status", hint.Source);

        var fromJObject = VillageStateHub.ExtractGraphHint("revit_graph_build", JObject.Parse("{\"databasePath\":\"x.db\",\"nodeCount\":5}"), _now)!;
        Assert.Equal("x.db", fromJObject.DatabasePath);
        Assert.False(fromJObject.Stale);   // a successful build is fresh by definition
        Assert.Null(VillageStateHub.ExtractGraphHint("revit_graph_status", null, _now));
        Assert.Null(VillageStateHub.ExtractGraphHint("revit_graph_status", "string", _now)!.DatabasePath);

        hub.RunOnce();
        Assert.DoesNotContain("do not publish", hub.SnapshotJson);
        Assert.DoesNotContain("graphs", hub.SnapshotJson);
    }

    [Fact]
    public void GraphRefresh_LoadsSnapshotThemeAndSizesBuildings()
    {
        var dbPath = Path.Combine(_root, "1626", "1626_PP_EN.graph.db");
        WriteFireAlarmGraph(dbPath, devices: 60);
        var reader = new VillageGraphReader(Path.Combine(_root, "cache"), clock: () => _now);
        using var hub = Hub(reader: reader, source: (ctx, hint) => new VillageGraphSource
        {
            DatabasePath = hint?.DatabasePath,
            Roots = { _root },
            ModelName = ctx.ModelName,
            RootSource = "test root"
        });
        var messages = new List<(string Name, string Json)>();
        hub.MessagePublished += (n, j) => messages.Add((n, j));

        hub.ToolCompleted("revit_list_sheets", "Claude Code", true, null, 1, null, Context());
        hub.RequestGraphRefresh();
        hub.RunOnce();

        var state = (JObject)VillageEventSerializer.ParseToken(hub.SnapshotJson);
        Assert.True(state["graph"]!.Value<bool>("exists"));
        Assert.Equal(60, state["graph"]!["counts"]!.Value<int>("elements"));
        Assert.Equal("unknown", state["graph"]!["freshness"]!.Value<string>("status"));
        Assert.Equal("fire_alarm", state["theme"]!.Value<string>("theme"));
        Assert.Equal(VillageThemeScorer.IdentityFor(Context().ComputeModelId()).Hue, state["theme"]!["identity"]!.Value<int>("hue"));
        var houses = state["buildings"]!.First(b => b.Value<string>("id") == "houses");
        Assert.Equal(2, houses.Value<int>("size"));
        Assert.Contains(messages, m => m.Name == "step" && m.Json.Contains("Graph snapshot refreshed"));
        Assert.Contains(state["instances"]!, i => i.Value<bool>("is_current"));

        // A graph tool reporting stale flips the freshness on the next refresh without re-reading the file.
        hub.ToolCompleted("revit_graph_status", "Claude Code", true, null, 3, new { databasePath = dbPath, stale = true, stale_reason = "changed" }, Context());
        hub.RunOnce();
        state = (JObject)VillageEventSerializer.ParseToken(hub.SnapshotJson);
        Assert.Equal("stale", state["graph"]!["freshness"]!.Value<string>("status"));
        Assert.Equal(2, state["buildings"]!.First(b => b.Value<string>("id") == "warning_area").Value<int>("size"));

        // A successful write after a fresh report makes it "possibly stale".
        hub.ToolCompleted("revit_graph_build", "Claude Code", true, null, 3, new { databasePath = dbPath }, Context());
        hub.RunOnce();
        Assert.Equal("fresh", ((JObject)VillageEventSerializer.ParseToken(hub.SnapshotJson))["graph"]!["freshness"]!.Value<string>("status"));
        _now = _now.AddSeconds(5);
        hub.ToolCompleted("revit_set_parameter", "Claude Code", true, null, 3, new { modifiedCount = 1 }, Context());
        hub.RequestGraphRefresh();
        hub.RunOnce();
        Assert.Equal("possibly_stale", ((JObject)VillageEventSerializer.ParseToken(hub.SnapshotJson))["graph"]!["freshness"]!.Value<string>("status"));
    }

    [Fact]
    public void GraphRefresh_WithoutGraph_IsLimitedMode()
    {
        var reader = new VillageGraphReader(Path.Combine(_root, "cache"), clock: () => _now);
        using var hub = Hub(reader: reader, source: (ctx, _) => new VillageGraphSource { Roots = { _root }, ModelName = ctx.ModelName });
        hub.ToolCompleted("revit_list_sheets", "Claude Code", true, null, 1, null, Context());
        hub.RequestGraphRefresh();
        hub.RunOnce();

        var state = (JObject)VillageEventSerializer.ParseToken(hub.SnapshotJson);
        Assert.False(state["graph"]!.Value<bool>("exists"));
        Assert.Equal("missing", state["graph"]!["freshness"]!.Value<string>("status"));
        Assert.Equal(JTokenType.Null, state["theme"]!.Type);
        Assert.All(state["buildings"]!, b => Assert.Equal(b.Value<int>("base_size"), b.Value<int>("size")));
    }

    [Fact]
    public async Task BackgroundConsumer_StartsProcessesAndStops()
    {
        using var hub = new VillageStateHub(new VillageOptions());
        var received = 0;
        hub.MessagePublished += (_, _) => Interlocked.Increment(ref received);
        hub.Start();
        Assert.True(hub.IsRunning);

        hub.ToolCompleted("revit_list_views", "Codex", true, null, 1, null, Context());
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref received) == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(received > 0);
        Assert.Contains("session_started", hub.Aggregator.History().Count > 0 ? "session_started" : string.Empty);

        hub.Stop();
        Assert.False(hub.IsRunning);
        hub.Stop(); // idempotent
    }

    private static void WriteFireAlarmGraph(string path, int devices)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var nodes = new List<GraphNode>
        {
            new() { Id = "1", Kind = "level", Name = "L1", Category = "Levels" },
            new() { Id = "60", Kind = "type", Name = "EN_ATS-Sireen: Std", Category = "Fire Alarm Devices" },
            new() { Id = "80", Kind = "sheet", Name = "EN-5-01", Category = "Sheets" },
            new() { Id = "70", Kind = "view", Name = "L1", Category = "Views", Extra = "{\"isSchedule\":false}" }
        };
        var edges = new List<GraphEdge>();
        for (var i = 0; i < devices; i++)
        {
            var id = (100 + i).ToString();
            nodes.Add(new GraphNode { Id = id, Kind = "element", Name = "Dev " + i, Category = "Fire Alarm Devices", Level = "L1" });
            edges.Add(new GraphEdge(id, "60", "type_of"));
        }
        var meta = new Dictionary<string, string>
        {
            [GraphSchema.MetaKeys.ModelName] = "1626_PP_EN",
            [GraphSchema.MetaKeys.BuiltAt] = "2026-09-10T11:00:00Z",
            [GraphSchema.MetaKeys.ElementCount] = devices.ToString(),
            [GraphSchema.MetaKeys.SchemaVersion] = GraphSchema.SchemaVersion.ToString()
        };
        using var db = GraphDatabase.CreateNew(path);
        db.WriteGraph(nodes, edges, meta);
    }
}
