using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Village;
using Xunit;

namespace RevitMCP.Tests;

public class VillageAggregatorTests
{
    /// <summary>Deterministic harness: a controllable clock, a factory sharing it and a recording aggregator.</summary>
    private sealed class Harness
    {
        public DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        public readonly VillageOptions Options;
        public readonly VillageEventFactory Factory;
        public readonly VillageAggregator Aggregator;
        public readonly List<VillageStoryStep> Steps = new();
        public int StateChanges;
        public readonly VillageProjectContext Context = new()
        {
            ModelTitle = "1626_PP_EN",
            CentralPath = @"\\server\bim\1626\1626_PP_EN.rvt",
            RevitVersion = "2026",
            IsWorkshared = true
        };

        public Harness(Action<VillageOptions>? configure = null)
        {
            Options = new VillageOptions();
            configure?.Invoke(Options);
            Factory = new VillageEventFactory("session-1", clock: () => Now);
            Aggregator = new VillageAggregator(Options, () => Now);
            Aggregator.StepEmitted += Steps.Add;
            Aggregator.StateChanged += () => StateChanges++;
            Aggregator.SetProject(Context, "session-1");
        }

        public void Advance(int ms) => Now = Now.AddMilliseconds(ms);

        public void Start(string tool, string client = "Claude Code") =>
            Aggregator.Process(Factory.ToolStarted(tool, client, Context));

        public void Complete(string tool, object? data = null, string client = "Claude Code", int durationMs = 5) =>
            Aggregator.Process(Factory.ToolCompleted(tool, client, true, null, durationMs, data, Context));

        public void Fail(string tool, string status = "tool_execution_failed", string client = "Claude Code") =>
            Aggregator.Process(Factory.ToolCompleted(tool, client, false, status, 5, null, Context));

        public void Run(string tool, object? data = null, string client = "Claude Code")
        {
            Start(tool, client);
            Advance(5);
            Complete(tool, data, client);
        }

        public VillageAgent Agent(string client = "Claude Code") =>
            Aggregator.Agents[VillageAggregator.AgentId(client)];
    }

    // ─── The example from the specification ─────────────────────────────────

    [Fact]
    public void SpecExample_CollapsesIntoAReadableStory()
    {
        var h = new Harness();

        h.Run("revit_graph_query");
        h.Advance(20);
        for (var i = 0; i < 6; i++) { h.Run("revit_get_elements_info", new { returned = 10 }); h.Advance(10); }
        for (var i = 0; i < 24; i++) { h.Run("revit_retag", new { updatedCount = 1 }); h.Advance(10); }
        h.Run("revit_get_connection_status");
        h.Advance(Harness_Window(h) + 1);
        h.Aggregator.Tick();

        var work = h.Steps.Where(s => s.Kind == VillageStepKinds.Work).ToList();
        var moves = h.Steps.Where(s => s.Kind == VillageStepKinds.Move).Select(s => s.Area).ToList();

        Assert.Equal(new[] { "graph", "elements", "tags_annotations", "project" }, moves);
        Assert.Equal(4, work.Count);
        Assert.Equal("Searched the model graph", work[0].Label);
        Assert.Equal("Inspected model elements (6 tools)", work[1].Label);
        Assert.Equal(6, work[1].ToolCount);
        Assert.Equal(60, work[1].Affected);
        Assert.Equal("24 tags updated (24 tools)", work[2].Label);
        Assert.Equal(24, work[2].ToolCount);
        Assert.Equal(24, work[2].Affected);
        Assert.Equal("modify", work[2].Activity);
        Assert.Equal("sign_workshop", work[2].Building);
        Assert.Equal("Inspected the project", work[3].Label);

        // 32 tools produced 4 work steps + 4 moves + 1 connect: an animation per raw event is never required.
        Assert.Equal(9, h.Steps.Count);
        Assert.Equal(32, h.Agent().ToolCount);
        Assert.Equal(24, h.Aggregator.Areas["tags_annotations"].Writes);
        Assert.Equal(24, h.Aggregator.Areas["tags_annotations"].Affected);
        Assert.Equal(6, h.Aggregator.Areas["elements"].Reads);
    }

    private static int Harness_Window(Harness h) => h.Options.AggregationWindowMs;

    // ─── Merging and windows ────────────────────────────────────────────────

    [Fact]
    public void RapidSameAreaActivity_StaysInOneOpenStepAndKeepsTheAgentInPlace()
    {
        var h = new Harness();
        for (var i = 0; i < 50; i++) { h.Run("revit_list_sheets"); h.Advance(20); }

        Assert.Single(h.Steps.Where(s => s.Kind == VillageStepKinds.Move));
        Assert.Empty(h.Steps.Where(s => s.Kind == VillageStepKinds.Work)); // still open
        var snap = h.Aggregator.Snapshot();
        Assert.Single(snap.CurrentSteps);
        Assert.True(snap.CurrentSteps[0].Open);
        Assert.Equal(50, snap.CurrentSteps[0].ToolCount);
        Assert.Equal("Inspected sheets (50 tools)", snap.CurrentSteps[0].Label);
        Assert.Equal("archive", h.Agent().Building);
        Assert.Equal(VillageAgentStates.Success, h.Agent().State);
    }

    [Fact]
    public void Silence_ClosesTheStepOnTick()
    {
        var h = new Harness();
        h.Run("revit_list_views");
        Assert.False(h.Aggregator.Tick());
        h.Advance(h.Options.AggregationWindowMs + 1);
        Assert.True(h.Aggregator.Tick());

        var work = Assert.Single(h.Steps.Where(s => s.Kind == VillageStepKinds.Work));
        Assert.False(work.Open);
        Assert.Equal("Inspected views", work.Label);
        Assert.Equal("lookout", work.Building);
        Assert.Empty(h.Aggregator.Snapshot().CurrentSteps);
    }

    [Fact]
    public void ReadsAndWritesInTheSameArea_AreSeparateSteps()
    {
        var h = new Harness();
        h.Run("revit_list_sheets");
        h.Run("revit_find_unplaced_views");            // views → move
        h.Run("revit_rename_views", new { renamedCount = 3 });
        h.Run("revit_list_views");
        h.Advance(5000);
        h.Aggregator.Tick();

        var labels = h.Steps.Where(s => s.Kind == VillageStepKinds.Work).Select(s => s.Label).ToList();
        Assert.Equal(new[] { "Inspected sheets", "Searched views", "3 views updated", "Inspected views" }, labels);
    }

    [Fact]
    public void CatchUpMode_WidensTheWindowSoBacklogsMerge()
    {
        var h = new Harness(o => o.AggregationWindowMs = 1000);
        h.Aggregator.SetCatchUp(true);
        Assert.True(h.Aggregator.CatchUp);
        Assert.Equal("catch_up", h.Aggregator.Snapshot().Mode);

        h.Run("revit_list_sheets");
        h.Advance(3000); // beyond the live window, inside the catch-up window
        h.Run("revit_list_sheets");
        Assert.Empty(h.Steps.Where(s => s.Kind == VillageStepKinds.Work));
        Assert.Equal(2, h.Aggregator.Snapshot().CurrentSteps[0].ToolCount);

        h.Aggregator.SetCatchUp(false);
        Assert.Equal("live", h.Aggregator.Snapshot().Mode);
        h.Advance(1001);
        h.Aggregator.Tick();
        Assert.Single(h.Steps.Where(s => s.Kind == VillageStepKinds.Work));
        Assert.Equal(1, h.Aggregator.Stats.CatchUpEntries);
    }

    // ─── Errors and approvals ───────────────────────────────────────────────

    [Fact]
    public void Failures_ArePreservedAndPutTheAgentInErrorState()
    {
        var h = new Harness();
        h.Run("revit_list_sheets");
        h.Start("revit_set_parameter");
        h.Fail("revit_set_parameter", "transaction_failed");
        h.Fail("revit_set_parameter", "transaction_failed");
        h.Run("revit_list_sheets");
        h.Advance(5000);
        h.Aggregator.Tick();

        var kinds = h.Steps.Select(s => s.Kind).ToList();
        Assert.Contains(VillageStepKinds.Error, kinds);
        var error = h.Steps.Single(s => s.Kind == VillageStepKinds.Error);
        Assert.Equal(2, error.Failures);
        Assert.Equal("2 tools failed in model elements", error.Label);
        Assert.Equal(2, h.Aggregator.Areas["elements"].Failures);
        Assert.Equal(2, h.Aggregator.Snapshot().RecentFailures);
        Assert.Equal(2, h.Agent().FailureCount);

        var single = new Harness();
        single.Fail("revit_delete_views");
        Assert.Equal(VillageAgentStates.Error, single.Agent().State);
        single.Advance(5000);
        single.Aggregator.Tick();
        Assert.Equal("revit_delete_views failed in views", single.Steps.Single(s => s.Kind == VillageStepKinds.Error).Label);
    }

    [Fact]
    public void ApprovalRequired_IsDeferredNotAnError()
    {
        var h = new Harness();
        h.Start("revit_delete_views");
        h.Fail("revit_delete_views", "approval_required");
        h.Advance(5000);
        h.Aggregator.Tick();

        var step = Assert.Single(h.Steps.Where(s => s.Kind == VillageStepKinds.Deferred));
        Assert.Equal("Awaiting approval: revit_delete_views", step.Label);
        Assert.Equal(1, h.Aggregator.Areas["views"].Deferred);
        Assert.Equal(0, h.Aggregator.Areas["views"].Failures);
        Assert.Equal(0, h.Aggregator.Snapshot().RecentFailures);
        Assert.Equal(0, h.Agent().FailureCount);
    }

    [Fact]
    public void RecentFailures_ExpireAfterFiveMinutes()
    {
        var h = new Harness();
        h.Fail("revit_list_sheets");
        Assert.Equal(1, h.Aggregator.Snapshot().RecentFailures);
        h.Advance(VillageAggregator.RecentFailureWindowMs + 1);
        h.Aggregator.Tick();
        Assert.Equal(0, h.Aggregator.Snapshot().RecentFailures);
        Assert.Equal(1, h.Aggregator.Areas["sheets"].Failures); // counters keep the total
    }

    // ─── Agents ─────────────────────────────────────────────────────────────

    [Fact]
    public void Agents_ConnectMoveIdleAndDisconnect()
    {
        var h = new Harness(o => o.AgentIdleSeconds = 30);
        h.Run("revit_list_sheets");
        Assert.Equal("Claude Code connected", h.Steps[0].Label);
        Assert.Equal(VillageStepKinds.Bridge, h.Steps[0].Kind);

        var agent = h.Agent();
        Assert.Equal("claude-code", agent.Id);
        Assert.Equal(VillageAgentStates.Success, agent.State);
        Assert.Equal("sheets", agent.Area);

        h.Advance(VillageAggregator.IdleAfterMs + 1);
        h.Aggregator.Tick();
        Assert.Equal(VillageAgentStates.Idle, h.Agent().State);

        h.Advance(30_000);
        h.Aggregator.Tick();
        Assert.Equal(VillageAgentStates.Disconnected, h.Agent().State);
        Assert.Contains(h.Steps, s => s.Label == "Claude Code disconnected (idle)");

        h.Run("revit_list_views");
        Assert.Contains(h.Steps, s => s.Label == "Claude Code reconnected");
        Assert.Equal(VillageAgentStates.Success, h.Agent().State);
    }

    [Fact]
    public void Agents_AreKeyedByClientAndWorkIndependently()
    {
        var h = new Harness();
        h.Run("revit_list_sheets", client: "Claude Code");
        h.Run("revit_list_views", client: "Codex");
        h.Run("revit_list_sheets", client: "Claude Code");

        Assert.Equal(2, h.Aggregator.Agents.Count);
        Assert.Equal("sheets", h.Agent("Claude Code").Area);
        Assert.Equal("views", h.Agent("Codex").Area);
        Assert.Equal(2, h.Aggregator.Snapshot().CurrentSteps.Count);
        Assert.Equal(2, h.Steps.Count(s => s.Kind == VillageStepKinds.Move));

        h.Run("revit_list_sheets", client: null!);
        Assert.Equal("agent", h.Agent("Agent").Id);
    }

    [Fact]
    public void ToolStarted_MarksInProgressAndWorkingOrInspecting()
    {
        var h = new Harness();
        h.Start("revit_place_tags");
        Assert.Equal(VillageAgentStates.Working, h.Agent().State);
        Assert.Equal(1, h.Agent().InProgress);
        Assert.Equal("sign_workshop", h.Agent().Building);
        // An open step with only a start is shown live but never emitted as history when nothing finishes.
        Assert.Single(h.Aggregator.Snapshot().CurrentSteps);
        h.Advance(60_000);
        h.Aggregator.Tick();
        Assert.Empty(h.Steps.Where(s => s.Kind == VillageStepKinds.Work));
        Assert.Equal(1, h.Agent().InProgress);

        // A start that never completes is forgotten after the stuck timeout.
        h.Advance(VillageAggregator.StuckAfterMs);
        h.Aggregator.Tick();
        Assert.Equal(0, h.Agent().InProgress);
        Assert.Equal(VillageAgentStates.Idle, h.Agent().State);

        h.Start("revit_list_sheets");
        Assert.Equal(VillageAgentStates.Inspecting, h.Agent().State);
        h.Complete("revit_list_sheets");
        Assert.Equal(0, h.Agent().InProgress);
        Assert.Equal(VillageAgentStates.Success, h.Agent().State);
    }

    // ─── Unknown input ──────────────────────────────────────────────────────

    [Fact]
    public void UnknownToolsAndEvents_AreCountedNotFatal()
    {
        var h = new Harness();
        h.Run("frobnicate_widgets");
        h.Aggregator.Process(new VillageEvent { EventType = "weather_changed", Sequence = 999 });
        h.Aggregator.Process(new VillageEvent { EventType = VillageEventTypes.ToolCompleted, Area = "moon_base", Activity = "dance", ClientName = "Claude Code" });
        h.Aggregator.Process(null!);

        Assert.Equal("square", h.Agent().Building);
        Assert.Equal(VillageAreas.Unknown, h.Agent().Area);
        Assert.Equal(3, h.Aggregator.Stats.UnknownTools);
        Assert.Equal(1, h.Aggregator.Stats.UnknownEvents);
        Assert.Equal(999, h.Aggregator.Stats.LastSequence);

        h.Advance(5000);
        h.Aggregator.Tick();
        var work = h.Steps.Where(s => s.Kind == VillageStepKinds.Work).ToList();
        Assert.Equal("Worked in the village square (2 tools)", work[0].Label);
        var snapshot = h.Aggregator.Snapshot();
        Assert.DoesNotContain(snapshot.Areas, a => a.Area == "moon_base"); // foreign vocabulary folds into "unknown"
        Assert.Contains(snapshot.Areas, a => a.Area == VillageAreas.Unknown && a.Reads == 2);
    }

    // ─── Project switches and lifecycle events ──────────────────────────────

    [Fact]
    public void ProjectChange_ResetsCountersAndEmitsAStep()
    {
        var h = new Harness();
        h.Run("revit_list_sheets");
        h.Run("revit_place_tags", new { placedCount = 5 });
        Assert.Equal(5, h.Aggregator.Areas["tags_annotations"].Affected);
        h.Aggregator.Graph = JObject.Parse("{\"exists\":true}");

        var other = new VillageProjectContext { ModelTitle = "Other", LocalPath = @"C:\x\Other.rvt" };
        h.Aggregator.Process(h.Factory.ProjectChanged(other));

        Assert.Equal("Other", h.Aggregator.ProjectName);
        Assert.Equal(other.ComputeModelId(), h.Aggregator.ModelId);
        Assert.Contains(h.Steps, s => s.Kind == VillageStepKinds.Project && s.Label == "Switched to Other");
        Assert.Equal(0, h.Aggregator.Areas["tags_annotations"].Affected);
        Assert.Equal(0, h.Aggregator.Areas["sheets"].Reads);
        Assert.Equal(VillageAreas.Unknown, h.Agent().Area);
        Assert.Equal(VillageAgentStates.Idle, h.Agent().State);
        Assert.Null(h.Aggregator.Graph);
        Assert.Empty(h.Aggregator.Snapshot().CurrentSteps);
        // The story survives the switch (replay history).
        Assert.Contains(h.Steps, s => s.Label == "5 tags created");

        // The same project reported again is not a switch.
        var before = h.Steps.Count;
        h.Aggregator.Process(h.Factory.ProjectChanged(other));
        Assert.Equal(before, h.Steps.Count);
    }

    [Fact]
    public void LifecycleEvents_BecomeSteps()
    {
        var h = new Harness();
        h.Aggregator.Process(h.Factory.SessionStarted(h.Context));
        h.Aggregator.Process(h.Factory.GraphRefreshed(h.Context, true, 12345));
        h.Aggregator.Process(h.Factory.GraphRefreshed(h.Context, false, null));
        h.Aggregator.Process(h.Factory.BridgeConnected("Codex", h.Context));
        h.Aggregator.Process(h.Factory.BridgeDisconnected("Codex", h.Context));

        var labels = h.Steps.Select(s => s.Label).ToList();
        Assert.Equal(new[]
        {
            "Connector session started",
            "Graph snapshot refreshed (12,345 nodes)",
            "Graph snapshot unavailable",
            "Codex connected",
            "Codex connected",
            "Codex disconnected"
        }, labels);
        Assert.Equal(VillageAgentStates.Disconnected, h.Agent("Codex").State);
    }

    // ─── Bounds and snapshot shape ──────────────────────────────────────────

    [Fact]
    public void History_IsBoundedAndRecentStepsRespectTheLimit()
    {
        var h = new Harness(o => { o.HistoryLimit = 50; o.RecentActivityLimit = 20; });
        for (var i = 0; i < 100; i++)
        {
            h.Fail(i % 2 == 0 ? "revit_list_sheets" : "revit_list_views"); // alternating areas: every event is a move + error
            h.Advance(5000);
            h.Aggregator.Tick();
        }

        Assert.Equal(50, h.Aggregator.History().Count);
        var snap = h.Aggregator.Snapshot();
        Assert.Equal(20, snap.RecentSteps.Count);
        Assert.True(snap.RecentSteps.Last().Id > snap.RecentSteps.First().Id);
        Assert.Equal(h.Aggregator.History().Last().Id, snap.RecentSteps.Last().Id);
        Assert.True(h.Aggregator.Stats.StepsEmitted >= 200);
    }

    [Fact]
    public void Snapshot_SerializesSnakeCaseWithReadOnlyNoticeAndNoRawData()
    {
        var h = new Harness();
        h.Run("revit_place_tags", new { placedCount = 24, elements = new[] { 1, 2, 3 } });
        h.Aggregator.QueueStats = new VillageQueueStats { Enqueued = 2, Capacity = 2000 };
        var snap = h.Aggregator.Snapshot();
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(snap);
        var obj = (JObject)VillageEventSerializer.ParseToken(json);

        Assert.True(obj.Value<bool>("read_only"));
        Assert.Equal(VillageSchema.ReadOnlyNotice, obj.Value<string>("notice"));
        Assert.Equal("1626_PP_EN", obj.Value<string>("project_name"));
        Assert.Equal("live", obj.Value<string>("mode"));
        Assert.Equal("session-1", obj.Value<string>("session_id"));
        Assert.Equal(11, obj["buildings"]!.Count());
        Assert.Equal("claude-code", obj["agents"]![0]!.Value<string>("id"));
        Assert.Equal("sign_workshop", obj["agents"]![0]!.Value<string>("building"));
        Assert.Equal(24, obj["current_steps"]![0]!.Value<long>("affected"));
        Assert.Equal(2, obj["queue"]!.Value<long>("enqueued"));
        Assert.Equal(1.0, obj["viewer_options"]!.Value<double>("animation_speed"));
        Assert.Contains(obj["areas"]!, a => a.Value<string>("area") == "tags_annotations" && a.Value<long>("writes") == 1);
        Assert.DoesNotContain("server", json);
        Assert.DoesNotContain("\"elements\":[1,2,3]", json);
        Assert.DoesNotContain("\"graph\":{", json); // limited mode: null graph
    }

    [Fact]
    public void Snapshot_IsACopyThatLaterEventsDoNotMutate()
    {
        var h = new Harness();
        h.Run("revit_list_sheets");
        var snap = h.Aggregator.Snapshot();
        h.Run("revit_list_sheets");
        Assert.Equal(1, snap.CurrentSteps[0].ToolCount);
        Assert.Equal(1, snap.Agents[0].ToolCount);
    }

    [Fact]
    public void Labels_AreDeterministicTemplates()
    {
        Assert.Equal("3 sheets deleted", VillageAggregator.LabelFor(new VillageStoryStep { Kind = "work", Activity = "delete", Area = "sheets", Affected = 3, ToolCount = 1 }));
        Assert.Equal("Deleted sheets (2 tools)", VillageAggregator.LabelFor(new VillageStoryStep { Kind = "work", Activity = "delete", Area = "sheets", ToolCount = 2 }));
        Assert.Equal("Exported from electrical systems", VillageAggregator.LabelFor(new VillageStoryStep { Kind = "work", Activity = "export", Area = "electrical", ToolCount = 1 }));
        Assert.Equal("Graph rebuilt (1,200 nodes)", VillageAggregator.LabelFor(new VillageStoryStep { Kind = "work", Activity = "build_graph", Area = "graph", Affected = 1200, ToolCount = 1 }));
        Assert.Equal("Checked the fire alarm system", VillageAggregator.LabelFor(new VillageStoryStep { Kind = "work", Activity = "validate", Area = "fire_alarm", ToolCount = 1 }));
        Assert.Equal("Heads to the records office", VillageAggregator.LabelFor(new VillageStoryStep { Kind = "move", Area = "office" }));
        Assert.Equal("Awaiting approval: revit_delete (3 requests)", VillageAggregator.LabelFor(new VillageStoryStep { Kind = "deferred", Deferred = 3, Tools = { "revit_delete" } }));
        Assert.Equal("Custom", VillageAggregator.LabelFor(new VillageStoryStep { Kind = "bridge", Label = "Custom" }));
    }

    [Fact]
    public void Layout_MapsEveryAreaToABuildingOrTheSquare()
    {
        Assert.Equal(11, VillageLayout.Default.Count);
        Assert.Equal("town_hall", VillageLayout.BuildingFor("graph"));
        Assert.Equal("utility_district", VillageLayout.BuildingFor("fire_alarm"));
        Assert.Equal("square", VillageLayout.BuildingFor("unknown"));
        Assert.Equal("square", VillageLayout.BuildingFor(null));
        foreach (var area in VillageAreas.All.Where(a => a != VillageAreas.Unknown))
            Assert.NotEqual("square", VillageLayout.BuildingFor(area));
        Assert.All(VillageLayout.Default, b => Assert.False(string.IsNullOrEmpty(b.Description)));

        var clone = VillageLayout.Clone();
        clone[0].Size = 4;
        Assert.Equal(3, VillageLayout.Default[0].Size == 0 ? 3 : VillageLayout.Default[0].BaseSize);
    }
}
