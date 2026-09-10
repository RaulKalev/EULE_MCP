using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Village;
using Xunit;

namespace RevitMCP.Tests;

public class VillageEventTests
{
    private static readonly DateTimeOffset FixedTime = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private static VillageEventFactory Factory() =>
        new("session-1", clock: () => FixedTime);

    private static VillageProjectContext Context() => new()
    {
        ModelTitle = "1626_PP_EN",
        CentralPath = @"\\server\bim\1626\1626_PP_EN.rvt",
        LocalPath = @"C:\Users\someone\Documents\1626_PP_EN_someone.rvt",
        RevitVersion = "2026",
        IsWorkshared = true
    };

    // ─── Serialization ──────────────────────────────────────────────────────

    [Fact]
    public void Serialize_UsesSnakeCaseFieldsAndSchemaVersion()
    {
        var e = Factory().ToolStarted("revit_list_views", "Claude Code", Context());
        var json = VillageEventSerializer.Serialize(e);
        var obj = (JObject)VillageEventSerializer.ParseToken(json);

        Assert.Equal(VillageSchema.Version, obj.Value<int>("schema_version"));
        Assert.Equal("tool_started", obj.Value<string>("event_type"));
        Assert.Equal("revit_list_views", obj.Value<string>("tool_name"));
        Assert.Equal("views", obj.Value<string>("area"));
        Assert.Equal("inspect", obj.Value<string>("activity"));
        Assert.Equal("2026-09-10T12:00:00.000Z", obj.Value<string>("timestamp"));
        Assert.Equal("session-1", obj.Value<string>("session_id"));
        Assert.Equal(JTokenType.Null, obj["success"]!.Type);
        Assert.Equal(JTokenType.Null, obj["duration_ms"]!.Type);
        Assert.Equal(JTokenType.Null, obj["affected_count"]!.Type);
    }

    [Fact]
    public void Serialize_NeverContainsPathsArgumentsOrResults()
    {
        var ctx = Context();
        var data = new
        {
            returned = 3,
            elements = new[] { new { id = 123456, name = "Secret room name", parameters = new { Mark = "X1" } } },
            filePath = @"C:\Users\someone\secret.xlsx",
            message = "prompt-like text"
        };
        var e = Factory().ToolCompleted("revit_get_elements_info", "Codex", true, null, 42, data, ctx);
        var json = VillageEventSerializer.Serialize(e);

        Assert.DoesNotContain("server", json);
        Assert.DoesNotContain("someone", json);
        Assert.DoesNotContain("Secret", json);
        Assert.DoesNotContain("123456", json);
        Assert.DoesNotContain("secret.xlsx", json);
        Assert.DoesNotContain("prompt-like", json);
        Assert.DoesNotContain("arguments", json);
        Assert.DoesNotContain("\"data\"", json);
        Assert.Equal(3, e.AffectedCount);
    }

    [Fact]
    public void RoundTrip_PreservesEveryField()
    {
        var original = Factory().ToolCompleted("revit_place_tags", "Claude Code", true, null, 1234, new { placedCount = 24 }, Context());
        var json = VillageEventSerializer.Serialize(original);

        Assert.True(VillageEventSerializer.TryDeserialize(json, out var parsed, out var error), error);
        Assert.NotNull(parsed);
        Assert.Equal(original.EventId, parsed!.EventId);
        Assert.Equal(original.SessionId, parsed.SessionId);
        Assert.Equal(original.ModelId, parsed.ModelId);
        Assert.Equal(original.ProjectName, parsed.ProjectName);
        Assert.Equal(original.Timestamp, parsed.Timestamp);
        Assert.Equal(original.EventType, parsed.EventType);
        Assert.Equal(original.ToolName, parsed.ToolName);
        Assert.Equal(original.Activity, parsed.Activity);
        Assert.Equal(original.Area, parsed.Area);
        Assert.Equal(original.Success, parsed.Success);
        Assert.Equal(original.DurationMs, parsed.DurationMs);
        Assert.Equal(original.AffectedCount, parsed.AffectedCount);
        Assert.Equal(original.ClientName, parsed.ClientName);
        Assert.Equal(original.Status, parsed.Status);
        Assert.Equal(original.Sequence, parsed.Sequence);
    }

    [Theory]
    [InlineData(null, "empty")]
    [InlineData("", "empty")]
    [InlineData("   ", "empty")]
    [InlineData("not json", "Invalid JSON")]
    [InlineData("[1,2,3]", "not a JSON object")]
    [InlineData("{\"event_type\":\"tool_started\"}", "Missing schema_version")]
    [InlineData("{\"schema_version\":\"1\",\"event_type\":\"tool_started\"}", "Missing schema_version")]
    [InlineData("{\"schema_version\":2,\"event_type\":\"tool_started\"}", "Unsupported schema_version 2")]
    [InlineData("{\"schema_version\":1}", "Missing event_type")]
    [InlineData("{\"schema_version\":1,\"event_type\":42}", "Missing event_type")]
    public void TryDeserialize_RejectsMalformedInput(string? json, string expectedError)
    {
        Assert.False(VillageEventSerializer.TryDeserialize(json, out var ev, out var error));
        Assert.Null(ev);
        Assert.Contains(expectedError, error);
    }

    [Fact]
    public void TryDeserialize_RejectsOversizedInput()
    {
        var json = "{\"schema_version\":1,\"event_type\":\"tool_started\",\"pad\":\"" + new string('x', VillageSchema.MaxSerializedBytes) + "\"}";
        Assert.False(VillageEventSerializer.TryDeserialize(json, out _, out var error));
        Assert.Contains("exceeds", error);
    }

    [Fact]
    public void TryDeserialize_ToleratesUnknownFieldsAndVocabulary()
    {
        var json = "{\"schema_version\":1,\"event_type\":\"tool_started\",\"area\":\"moon_base\",\"activity\":\"dance\"," +
                   "\"tool_name\":42,\"success\":\"yes\",\"duration_ms\":12.9,\"affected_count\":\"many\"," +
                   "\"future_field\":{\"nested\":true},\"client_name\":\"" + new string('c', 500) + "\"}";

        Assert.True(VillageEventSerializer.TryDeserialize(json, out var ev, out var error), error);
        Assert.Equal(VillageAreas.Unknown, ev!.Area);
        Assert.Equal(VillageActivities.Unknown, ev.Activity);
        Assert.Null(ev.ToolName);
        Assert.Null(ev.Success);
        Assert.Equal(12, ev.DurationMs);
        Assert.Null(ev.AffectedCount);
        Assert.Equal(VillageSchema.MaxStringLength, ev.ClientName!.Length);
        Assert.False(string.IsNullOrEmpty(ev.EventId));
        Assert.False(string.IsNullOrEmpty(ev.Timestamp));
    }

    [Fact]
    public void TryDeserialize_KeepsUnknownEventTypeSoTheViewerCanIgnoreIt()
    {
        var json = "{\"schema_version\":1,\"event_type\":\"weather_changed\"}";
        Assert.True(VillageEventSerializer.TryDeserialize(json, out var ev, out _));
        Assert.Equal("weather_changed", ev!.EventType);
        Assert.False(VillageEventTypes.IsKnown(ev.EventType));
    }

    // ─── Vocabulary ─────────────────────────────────────────────────────────

    [Fact]
    public void Vocabularies_AreClosedAndNormalizeToUnknown()
    {
        Assert.Contains("unknown", VillageAreas.All);
        Assert.Contains("unknown", VillageActivities.All);
        Assert.Equal("unknown", VillageAreas.Normalize("Sheets!"));
        Assert.Equal("sheets", VillageAreas.Normalize(" SHEETS "));
        Assert.Equal("unknown", VillageActivities.Normalize(null));
        Assert.Equal("build_graph", VillageActivities.Normalize("build_graph"));
        Assert.True(VillageActivities.IsWrite("create"));
        Assert.True(VillageActivities.IsWrite("modify"));
        Assert.True(VillageActivities.IsWrite("delete"));
        Assert.False(VillageActivities.IsWrite("inspect"));
        Assert.False(VillageActivities.IsWrite("export"));
        Assert.Equal(9, VillageEventTypes.All.Length);
    }

    // ─── Factory ────────────────────────────────────────────────────────────

    [Fact]
    public void ToolCompleted_MapsSuccessDeferredAndFailure()
    {
        var f = Factory();

        var ok = f.ToolCompleted("revit_set_parameter", "Claude Code", true, null, 10, null, Context());
        Assert.Equal(VillageEventTypes.ToolCompleted, ok.EventType);
        Assert.Equal(VillageActivities.Modify, ok.Activity);
        Assert.True(ok.Success);
        Assert.Null(ok.Status);

        var deferred = f.ToolCompleted("revit_set_parameter", "Claude Code", false, "approval_required", 10, null, Context());
        Assert.Equal(VillageEventTypes.ToolDeferred, deferred.EventType);
        Assert.Equal(VillageActivities.Modify, deferred.Activity);
        Assert.False(deferred.Success);
        Assert.Equal("approval_required", deferred.Status);

        var failed = f.ToolCompleted("revit_set_parameter", "Claude Code", false, "transaction_failed", 10, new { modifiedCount = 5 }, Context());
        Assert.Equal(VillageEventTypes.ToolFailed, failed.EventType);
        Assert.Equal(VillageActivities.Error, failed.Activity);
        Assert.Equal(VillageAreas.Elements, failed.Area);
        Assert.Null(failed.AffectedCount);

        var timedOut = f.ToolCompleted("revit_graph_build", null, false, "request_timeout", 30000, null, Context());
        Assert.Equal(VillageEventTypes.ToolFailed, timedOut.EventType);
        Assert.Equal("request_timeout", timedOut.Status);
        Assert.Null(timedOut.ClientName);
    }

    [Fact]
    public void Factory_AssignsIncreasingSequenceAndStableSession()
    {
        var f = Factory();
        var a = f.ToolStarted("revit_list_sheets", "x", Context());
        var b = f.ToolCompleted("revit_list_sheets", "x", true, null, 1, null, Context());
        var c = f.SessionStarted(Context());

        Assert.Equal(1, a.Sequence);
        Assert.Equal(2, b.Sequence);
        Assert.Equal(3, c.Sequence);
        Assert.Equal("session-1", c.SessionId);
        Assert.NotEqual(a.EventId, b.EventId);
    }

    [Fact]
    public void Factory_SanitizesToolClientAndStatus()
    {
        var f = Factory();
        var e = f.ToolCompleted("Revit_List_Views; DROP TABLE", "Claude\u0000 Code\r\n", false, "Approval Required!", -5, null, null);

        Assert.Equal("unknown_tool", e.ToolName);
        Assert.Equal("Claude Code", e.ClientName);
        Assert.Equal("other", e.Status);
        Assert.Equal(0, e.DurationMs);
        Assert.Equal(VillageModelId.NoDocument, e.ModelId);
        Assert.Equal("No document", e.ProjectName);

        Assert.Equal("revit_list_views", VillageEventFactory.SanitizeToolName(" REVIT_list_views "));
        Assert.Equal("unknown_tool", VillageEventFactory.SanitizeToolName(new string('a', 101)));
        Assert.Null(VillageEventFactory.SanitizeClientName("\u0001\u0002"));
        Assert.Equal(60, VillageEventFactory.SanitizeClientName(new string('c', 80))!.Length);
    }

    [Fact]
    public void Factory_LifecycleEventsCarryProjectIdentity()
    {
        var f = Factory();
        var ctx = Context();

        var session = f.SessionStarted(ctx);
        var project = f.ProjectChanged(ctx);
        var graph = f.GraphRefreshed(ctx, true, 1200);
        var connected = f.BridgeConnected("Codex", ctx);
        var disconnected = f.BridgeDisconnected("Codex", ctx);

        Assert.Equal(VillageEventTypes.SessionStarted, session.EventType);
        Assert.Equal(VillageEventTypes.ProjectChanged, project.EventType);
        Assert.Equal(VillageEventTypes.GraphRefreshed, graph.EventType);
        Assert.Equal(VillageAreas.Graph, graph.Area);
        Assert.Equal(1200, graph.AffectedCount);
        Assert.True(graph.Success);
        Assert.Equal(VillageEventTypes.BridgeConnected, connected.EventType);
        Assert.Equal("Codex", connected.ClientName);
        Assert.Equal(VillageEventTypes.BridgeDisconnected, disconnected.EventType);
        Assert.All(new[] { session, project, graph, connected, disconnected }, e =>
        {
            Assert.Equal("1626_PP_EN", e.ProjectName);
            Assert.Equal(ctx.ComputeModelId(), e.ModelId);
        });
    }

    // ─── Model identity ─────────────────────────────────────────────────────

    [Fact]
    public void ModelId_IsStableCaseInsensitiveAndNotThePath()
    {
        var a = VillageModelId.Compute(@"\\Server\BIM\1626\1626_PP_EN.rvt", @"C:\local\1626_PP_EN_me.rvt", "1626_PP_EN");
        var b = VillageModelId.Compute(@"\\server\bim\1626\1626_pp_en.rvt", @"C:\other\local.rvt", "Other title");
        var c = VillageModelId.Compute(null, @"C:\local\Standalone.rvt", "Standalone");
        var d = VillageModelId.Compute(null, null, "Untitled");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(c, d);
        Assert.Equal(16, a.Length);
        Assert.Matches("^[0-9a-f]{16}$", a);
        Assert.DoesNotContain("server", a);
        Assert.Equal(VillageModelId.NoDocument, VillageModelId.Compute(null, "", "  "));
    }

    [Fact]
    public void ProjectContext_DerivesModelNameFromCentralPathFirst()
    {
        var ctx = Context();
        Assert.Equal("1626_PP_EN", ctx.ModelName);
        Assert.Equal("1626_PP_EN", ctx.DisplayName);
        Assert.True(ctx.HasDocument);

        var local = new VillageProjectContext { ModelTitle = "Title", LocalPath = @"C:\x\Model_v2.rvt" };
        Assert.Equal("Model_v2", local.ModelName);

        var cloud = new VillageProjectContext { ModelTitle = "Cloud", CentralPath = "BIM 360://Project/Folder/Cloud Model.rvt" };
        Assert.Equal("Cloud Model", cloud.ModelName);

        var untitled = new VillageProjectContext { ModelTitle = "Project1" };
        Assert.Equal("Project1", untitled.ModelName);
        Assert.False(VillageProjectContext.Empty.HasDocument);
        Assert.Equal("No document", VillageProjectContext.Empty.DisplayName);
    }

    // ─── Affected count extraction ──────────────────────────────────────────

    [Fact]
    public void AffectedCount_ReadsAllowListedTopLevelNumbersOnly()
    {
        Assert.Equal(24, VillageAffectedCount.TryExtract(new { placedCount = 24, returned = 100 }));
        Assert.Equal(100, VillageAffectedCount.TryExtract(new { returned = 100, totalMatched = 5000 }));
        Assert.Equal(7, VillageAffectedCount.TryExtract(JObject.Parse("{\"count\":7,\"items\":[1,2,3]}")));
        Assert.Equal(3, VillageAffectedCount.TryExtract(new Dictionary<string, object?> { ["totalIssues"] = 3L }));
        Assert.Equal(12, VillageAffectedCount.TryExtract(new { nodeCount = 12.7 }));
        Assert.Null(VillageAffectedCount.TryExtract(new { nested = new { placedCount = 24 } }));
        Assert.Null(VillageAffectedCount.TryExtract(new { count = "24" }));
        Assert.Null(VillageAffectedCount.TryExtract(new { summary = "done" }));
        Assert.Null(VillageAffectedCount.TryExtract(null));
        Assert.Null(VillageAffectedCount.TryExtract("string result"));
        Assert.Null(VillageAffectedCount.TryExtract(42));
        Assert.Null(VillageAffectedCount.TryExtract(new[] { 1, 2, 3 }));
        Assert.Null(VillageAffectedCount.TryExtract(JObject.Parse("{\"count\":\"seven\"}")));
    }

    private sealed class Throwing
    {
        public int Count => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void AffectedCount_NeverThrows()
    {
        Assert.Null(VillageAffectedCount.TryExtract(new Throwing()));
    }
}
