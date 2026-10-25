using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Plans;
using RevitMCP.Core.Models;
using Xunit;

namespace RevitMCP.Tests;

internal static class PlanFixtures
{
    public static readonly Dictionary<string, ToolPermission> Permissions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["revit_set_parameter"] = ToolPermission.RequiresApproval,
        ["revit_move_elements"] = ToolPermission.RequiresApproval,
        ["revit_place_tags"] = ToolPermission.RequiresApproval,
        ["revit_delete"] = ToolPermission.DestructiveRequiresManualApproval,
        ["revit_get_elements_info"] = ToolPermission.ReadOnly,
        ["revit_memory_remember"] = ToolPermission.RequiresApproval
    };

    public static PlanDefinition Parse(object envelope, string title = "Tag sockets", object? steps = null, string? planId = null)
    {
        var args = new Dictionary<string, object?>
        {
            ["title"] = title,
            ["steps"] = JToken.FromObject(steps ?? new[] { "Read sockets", "Tag them" }),
            ["envelope"] = JToken.FromObject(envelope)
        };
        if (planId != null) args["planId"] = planId;
        return PlanEnvelopeParser.Parse(args,
            name => Permissions.TryGetValue(name, out var p) ? p : null,
            name => name == "revit_memory_remember");
    }

    public static Plan Active(PlanEnvelope envelope) => new()
    {
        Id = PlanIds.NewId(),
        Title = "t",
        Envelope = envelope,
        State = PlanState.Active,
        CreatedAt = DateTimeOffset.Now,
        ActivatedAt = DateTimeOffset.Now,
        ExpiresAt = DateTimeOffset.Now.AddMinutes(envelope.ExpiresMinutes)
    };
}

public class PlanEnvelopeParserTests
{
    [Fact]
    public void DefaultsAndClamps()
    {
        var def = PlanFixtures.Parse(new { tools = new[] { "revit_set_parameter" }, expiresMinutes = 9999, maxCalls = 0 });

        Assert.True(def.IsValid, string.Join("; ", def.Errors));
        Assert.Equal(500, def.Envelope.MaxElementsPerCall);
        Assert.Equal(2000, def.Envelope.MaxElementsTotal);
        Assert.Equal(PlanEnvelope.MaxExpiresMinutes, def.Envelope.ExpiresMinutes);
        Assert.Equal(1, def.Envelope.MaxCalls);
        Assert.True(def.Envelope.AllowCreate);
        Assert.False(def.Envelope.Strict);
        Assert.Contains(def.Warnings, w => w.Contains("expiresMinutes"));
        Assert.True(PlanIds.IsValid(def.Id));
    }

    [Fact]
    public void DestructiveUnknownAndBackgroundToolsAreRefused()
    {
        var def = PlanFixtures.Parse(new { tools = new[] { "revit_delete", "revit_nope", "revit_memory_remember", "revit_set_parameter" } });

        Assert.False(def.IsValid);
        Assert.Contains(def.Errors, e => e.Contains("revit_delete") && e.Contains("destructive"));
        Assert.Contains(def.Errors, e => e.Contains("Unknown tool 'revit_nope'"));
        Assert.Contains(def.Errors, e => e.Contains("revit_memory_remember"));
    }

    [Fact]
    public void ReadOnlyToolIsLeftOutWithAWarning()
    {
        var def = PlanFixtures.Parse(new { tools = new[] { "revit_get_elements_info", "revit_move_elements" } });

        Assert.True(def.IsValid);
        Assert.Equal(new[] { "revit_move_elements" }, def.Envelope.Tools);
        Assert.Contains(def.Warnings, w => w.Contains("revit_get_elements_info"));
    }

    [Fact]
    public void ElementIdCapAndRequiredFields()
    {
        var tooMany = PlanFixtures.Parse(new { tools = new[] { "revit_set_parameter" }, elementIds = Enumerable.Range(1, PlanEnvelope.MaxElementIds + 1).ToArray() });
        Assert.Contains(tooMany.Errors, e => e.Contains("at most"));

        var noTitle = PlanFixtures.Parse(new { tools = new[] { "revit_set_parameter" } }, title: " ");
        Assert.Contains(noTitle.Errors, e => e.Contains("title"));

        var nothing = PlanFixtures.Parse(new { tools = new[] { "revit_set_parameter" }, allowCreate = false, allowModify = false });
        Assert.Contains(nothing.Errors, e => e.Contains("could do nothing"));
    }

    [Fact]
    public void ClientChosenPlanIdIsKeptWhenValid()
    {
        Assert.Equal("plan-0a1b2c", PlanFixtures.Parse(new { tools = new[] { "revit_set_parameter" } }, planId: "plan-0a1b2c").Id);
        Assert.Contains(PlanFixtures.Parse(new { tools = new[] { "revit_set_parameter" } }, planId: "x").Errors, e => e.Contains("planId"));
    }

    [Fact]
    public void ParsesScopeFields()
    {
        var def = PlanFixtures.Parse(new
        {
            tools = new[] { "revit_place_tags" },
            elementIds = new[] { 10, 11 },
            categories = new[] { "Electrical Fixtures" },
            viewIds = new[] { 900 },
            levelIds = new[] { 5 },
            worksets = new[] { "EL" },
            strict = true
        });

        Assert.Equal(new long[] { 10, 11 }, def.Envelope.ElementIds.OrderBy(i => i));
        Assert.Contains("electrical fixtures", def.Envelope.Categories);
        Assert.Contains(900L, def.Envelope.ViewIds);
        Assert.Contains(5L, def.Envelope.LevelIds);
        Assert.Contains("EL", def.Envelope.Worksets);
        Assert.True(def.Envelope.Strict);
    }
}

public class PlanArgumentScopeTests
{
    private static Dictionary<string, object?> Args(object anonymous)
        => JObject.FromObject(anonymous).Properties().ToDictionary(p => p.Name, p => (object?)p.Value);

    [Fact]
    public void FlatAndNestedIdsAreCollected()
    {
        var targets = PlanArgumentScope.Extract(Args(new
        {
            elementIds = new[] { 1, 2 },
            moves = new[] { new { elementId = 3, dx = 100.5 }, new { elementId = 4, dx = 0.0 } },
            rotations = new[] { new { id = 5, angle = 90 } },
            viewId = 900,
            typeId = 77,
            planId = "plan-000001",
            category = "Electrical Fixtures",
            limit = 50,
            useSelection = true
        }));

        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, targets.ElementIds.OrderBy(i => i));
        Assert.Equal(new long[] { 900 }, targets.ViewIds);
        Assert.Equal(new[] { "Electrical Fixtures" }, targets.Categories);
        Assert.Equal(50, targets.Limit);
        Assert.True(targets.UseSelection);
    }

    [Theory]
    // One row per argument shape the write tools use today.
    [InlineData("revit_set_parameter", "{\"elementIds\":[1,2],\"parameterName\":\"Mark\",\"value\":\"A\"}", "1,2")]
    [InlineData("revit_move_elements", "{\"moves\":[{\"elementId\":3,\"dx\":10}]}", "3")]
    [InlineData("revit_rotate_elements", "{\"rotations\":[{\"elementId\":4,\"angleDegrees\":90}]}", "4")]
    [InlineData("revit_set_elevation", "{\"elevations\":[{\"elementId\":5,\"elevationMm\":1200}]}", "5")]
    [InlineData("revit_set_parameters_bulk", "{\"changes\":[{\"elementId\":6,\"parameterName\":\"Mark\",\"value\":\"x\"}]}", "6")]
    [InlineData("revit_set_text_notes", "{\"textNoteIds\":[7],\"text\":\"x\"}", "7")]
    [InlineData("revit_place_tags", "{\"elementIds\":[8],\"viewId\":900,\"tagTypeId\":55}", "8")]
    [InlineData("revit_set_circuit_parameter", "{\"circuitIds\":[9],\"parameterName\":\"Load Name\",\"value\":\"x\"}", "9")]
    [InlineData("revit_panelschematics_set_circuit_rows", "{\"panel\":\"KK1\",\"rows\":[{\"circuitId\":10,\"values\":{\"A\":\"1\"}}]}", "10")]
    public void ToolArgumentShapes(string tool, string json, string expected)
    {
        var args = JObject.Parse(json).Properties().ToDictionary(p => p.Name, p => (object?)p.Value);
        var ids = PlanArgumentScope.Extract(args).ElementIds.OrderBy(i => i);
        Assert.True(string.Join(",", ids) == expected, $"{tool}: {string.Join(",", ids)}");
    }

    [Fact]
    public void CheckRefusesWhatIsOutsideTheEnvelope()
    {
        var plan = PlanFixtures.Active(new PlanEnvelope
        {
            Tools = { "revit_set_parameter", "revit_place_tags" },
            ElementIds = { 1, 2, 3 },
            Categories = { "Electrical Fixtures" },
            ViewIds = { 900 },
            MaxElementsPerCall = 2
        });
        string? Check(string tool, object args, params long[] selection)
            => PlanArgumentScope.Check(plan, tool, PlanArgumentScope.Extract(Args(args)), () => selection);

        Assert.Null(Check("revit_set_parameter", new { elementIds = new[] { 1, 2 } }));
        Assert.Contains("not one of the plan's tools", Check("revit_move_elements", new { elementIds = new[] { 1 } }));
        Assert.Contains("outside the plan's element list", Check("revit_set_parameter", new { elementIds = new[] { 1, 99 } }));
        Assert.Contains("outside the plan's views", Check("revit_place_tags", new { elementIds = new[] { 1 }, viewId = 901 }));
        Assert.Contains("outside the plan's categories", Check("revit_set_parameter", new { category = "Doors" }));
        Assert.Contains("per call", Check("revit_set_parameter", new { elementIds = new[] { 1, 2, 3 } }));
        Assert.Contains("limit 50", Check("revit_set_parameter", new { category = "Electrical Fixtures", limit = 50 }));
        Assert.Contains("element list", Check("revit_set_parameter", new { useSelection = true }, 1, 42));
    }

    [Fact]
    public void ViewScopeDefaultIsNotAViewId()
    {
        var plan = PlanFixtures.Active(new PlanEnvelope { Tools = { "revit_set_text_notes" }, ElementIds = { 36262 }, ViewIds = { 36243 } });
        string? Check(object args, long? activeView)
            => PlanArgumentScope.Check(plan, "revit_set_text_notes", PlanArgumentScope.Extract(Args(args)), () => Array.Empty<long>(), () => activeView);

        // Live test: the bridge's default viewId=-1 sent next to elementIds made the plan refuse the call.
        Assert.Null(Check(new { elementIds = new[] { 36262 }, viewId = -1 }, activeView: 2125680));
        Assert.Null(Check(new { elementIds = new[] { 36262 }, viewId = 0 }, activeView: null));
        Assert.Null(Check(new { viewId = -1, textFilter = "x" }, activeView: 36243));
        Assert.Contains("outside the plan's views", Check(new { viewId = -1, textFilter = "x" }, activeView: 2125680));
        Assert.Contains("active view is not known", Check(new { viewId = 0 }, activeView: null));
        Assert.Contains("outside the plan's views", Check(new { elementIds = new[] { 36262 }, viewId = 901 }, activeView: 36243));
    }

    [Fact]
    public void CreatingToolIsRefusedWhenCreatingIsNotAllowed()
    {
        var plan = PlanFixtures.Active(new PlanEnvelope { Tools = { "revit_place_tags" }, AllowCreate = false });
        Assert.Contains("creates elements", PlanArgumentScope.Check(plan, "revit_place_tags", new PlanTargets(), () => Array.Empty<long>()));
    }
}

public class PlanPostCheckTests
{
    private static readonly Dictionary<long, ElementFacts> Facts = new()
    {
        [1] = new() { BuiltInCategory = "OST_ElectricalFixtures", Category = "Electrical Fixtures", LevelId = 5 },
        [2] = new() { BuiltInCategory = "OST_ElectricalFixtures", Category = "Electrical Fixtures", LevelId = 6 },
        [3] = new() { BuiltInCategory = "OST_Doors", Category = "Doors" },
        [4] = new() { BuiltInCategory = "OST_ElectricalFixtures", Category = "Electrical Fixtures" },
        [10] = new() { BuiltInCategory = "OST_ElectricalFixtureTags", Category = "Electrical Fixture Tags", OwnerViewId = 900 },
        [11] = new() { BuiltInCategory = "OST_ElectricalCircuit", Category = "Electrical Circuits" },
        [20] = new() { BuiltInCategory = "OST_TextNotes", Category = "Text Notes", OwnerViewId = 901 }
    };

    private static PostCheckResult Run(Plan plan, long[]? added = null, long[]? modified = null, long[]? deleted = null)
    {
        var changes = new CallChanges();
        changes.Record(added ?? Array.Empty<long>(), modified ?? Array.Empty<long>(), deleted ?? Array.Empty<long>());
        return PlanPostCheck.Check(plan, changes, id => Facts.TryGetValue(id, out var f) ? f : null);
    }

    private static Plan PlanFor(Action<PlanEnvelope>? configure = null)
    {
        var env = new PlanEnvelope { Tools = { "revit_move_elements" }, Categories = { "Electrical Fixtures" } };
        configure?.Invoke(env);
        return PlanFixtures.Active(env);
    }

    [Fact]
    public void IncidentalCategoriesAreIgnoredUnlessStrict()
    {
        Assert.Null(Run(PlanFor(), modified: new[] { 1L, 10L, 11L }).Violation);
        Assert.NotNull(Run(PlanFor(e => e.Strict = true), modified: new[] { 1L, 11L }).Violation);
    }

    [Fact]
    public void DeletesAlwaysViolate()
    {
        var result = Run(PlanFor(e => e.Categories.Clear()), modified: new[] { 1L }, deleted: new[] { 10L });
        Assert.Contains("deleted", result.Violation);
        Assert.Equal(new[] { 10L }, result.OffendingIds);
    }

    [Fact]
    public void CategoryLevelViewAndIdScope()
    {
        Assert.Contains("Doors", Run(PlanFor(), modified: new[] { 3L }).Violation);
        Assert.Contains("level", Run(PlanFor(e => e.LevelIds.Add(5)), modified: new[] { 2L }).Violation);
        Assert.Contains("not in the element list", Run(PlanFor(e => e.ElementIds.Add(1)), modified: new[] { 2L }).Violation);
        Assert.Contains("view", Run(PlanFor(e => { e.Categories.Clear(); e.ViewIds.Add(900); }), added: new[] { 20L }).Violation);
        // The category matches by its OST_ name too.
        Assert.Null(Run(PlanFor(e => { e.Categories.Clear(); e.Categories.Add("OST_ElectricalFixtures"); }), modified: new[] { 1L }).Violation);
    }

    [Fact]
    public void CreateAndModifyFlags()
    {
        Assert.Contains("does not allow creating", Run(PlanFor(e => e.AllowCreate = false), added: new[] { 1L }).Violation);
        Assert.Contains("does not allow modifying", Run(PlanFor(e => e.AllowModify = false), modified: new[] { 1L }).Violation);
        // A new element is never on an id list written before it existed.
        Assert.Null(Run(PlanFor(e => e.ElementIds.Add(2)), added: new[] { 1L }).Violation);
    }

    [Fact]
    public void LimitsCountAddedIncidentalElementsButNotModifiedOnes()
    {
        var plan = PlanFor(e => { e.MaxElementsPerCall = 1; e.Categories.Clear(); });
        Assert.Null(Run(plan, modified: new[] { 1L, 11L }).Violation);
        Assert.Contains("per call", Run(plan, added: new[] { 10L }, modified: new[] { 1L }).Violation);
    }

    [Fact]
    public void TotalLimitAndNetting()
    {
        var plan = PlanFor(e => e.MaxElementsTotal = 2);
        plan.Touched.Add(1);
        plan.Touched.Add(2);

        Assert.Null(Run(plan, modified: new[] { 1L, 2L }).Violation);   // already counted
        Assert.Contains("in total", Run(plan, added: new[] { 4L }, modified: new[] { 1L }).Violation);

        var changes = new CallChanges();
        changes.Record(new[] { 7L }, Array.Empty<long>(), Array.Empty<long>());
        changes.Record(Array.Empty<long>(), new[] { 7L }, Array.Empty<long>());
        changes.Record(Array.Empty<long>(), Array.Empty<long>(), new[] { 7L });
        Assert.Empty(changes.Added);
        Assert.Empty(changes.Deleted);
    }
}

public class PlanToolClassificationTests
{
    private static IEnumerable<string> BridgeWriteTools()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitMCP.slnx"))) dir = dir.Parent;
        foreach (var file in Directory.GetFiles(Path.Combine(dir!.FullName, "RevitMCP.Bridge"), "RevitMcpTools*.cs"))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"McpServerTool\(Name = ""(?<name>[^""]+)""(?<rest>[^)]*)\)"))
                if (!m.Groups["rest"].Value.Contains("ReadOnly = true")) yield return m.Groups["name"].Value;
        }
    }

    [Fact]
    public void EveryWriteToolTheBridgeAdvertisesIsClassified()
    {
        var tools = BridgeWriteTools().Distinct().ToList();
        Assert.True(tools.Count > 50, $"parsed {tools.Count} write tools");
        var unknown = tools.Where(t => PlanToolClassification.Classify(t) == PlanToolKind.Unknown).ToList();
        Assert.True(unknown.Count == 0, "Classify these in PlanToolClassification: " + string.Join(", ", unknown));
    }

    [Theory]
    [InlineData("revit_place_tags", PlanToolKind.Creates)]
    [InlineData("revit_copy_elements", PlanToolKind.Creates)]
    [InlineData("revit_create_text_notes", PlanToolKind.Creates)]
    [InlineData("revit_set_parameter", PlanToolKind.Modifies)]
    [InlineData("revit_move_elements", PlanToolKind.Modifies)]
    [InlineData("revit_something_new", PlanToolKind.Unknown)]
    public void Classify(string tool, PlanToolKind kind) => Assert.Equal(kind, PlanToolClassification.Classify(tool));

    [Theory]
    [InlineData("OST_ElectricalFixtureTags", true)]
    [InlineData("OST_Dimensions", true)]
    [InlineData("OST_Wire", true)]
    [InlineData(null, true)]
    [InlineData("OST_ElectricalFixtures", false)]
    [InlineData("OST_TextNotes", false)]
    public void Incidental(string? category, bool expected) => Assert.Equal(expected, PlanToolClassification.IsIncidental(category));
}

public class PlanRegistryTests
{
    [Fact]
    public void ExhaustsAtTheCallLimitAndExpires()
    {
        var registry = new PlanRegistry();
        var plan = PlanFixtures.Active(new PlanEnvelope { Tools = { "revit_set_parameter" }, MaxCalls = 2 });
        registry.Add(plan);

        registry.RecordCall(plan, new PostCheckResult { Counted = { 1, 2 } });
        Assert.Null(registry.CheckUsable(plan, DateTimeOffset.Now));
        registry.RecordCall(plan, new PostCheckResult { Counted = { 2, 3 } });

        Assert.Equal(PlanState.Exhausted, plan.State);
        Assert.Equal(3, plan.Touched.Count);
        Assert.Contains("exhausted", registry.CheckUsable(plan, DateTimeOffset.Now));

        var later = PlanFixtures.Active(new PlanEnvelope { Tools = { "revit_set_parameter" }, ExpiresMinutes = 1 });
        registry.Add(later);
        Assert.Contains("expired", registry.CheckUsable(later, DateTimeOffset.Now.AddMinutes(2)));
        Assert.Equal(PlanState.Expired, later.State);
    }

    [Fact]
    public void RevokeCloseAndRevokeAll()
    {
        var registry = new PlanRegistry();
        var a = PlanFixtures.Active(new PlanEnvelope { Tools = { "x" } });
        var b = PlanFixtures.Active(new PlanEnvelope { Tools = { "x" } });
        registry.Add(a);
        registry.Add(b);
        var changes = 0;
        registry.Changed += () => changes++;

        Assert.True(registry.Finish(a, PlanState.Closed, "done", DateTimeOffset.Now));
        Assert.False(registry.Finish(a, PlanState.Revoked, "again", DateTimeOffset.Now));
        Assert.Equal(new[] { b.Id }, registry.RevokeAll("panic stop", DateTimeOffset.Now).Select(p => p.Id));
        Assert.Equal(PlanState.Revoked, b.State);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void ViolationsUseACall()
    {
        var registry = new PlanRegistry();
        var plan = PlanFixtures.Active(new PlanEnvelope { Tools = { "x" }, MaxCalls = 1 });
        registry.Add(plan);

        registry.RecordViolation(plan, new PlanViolation { Kind = "fallback", Reason = "outside" });
        Assert.Equal(PlanState.Active, plan.State);
        registry.RecordViolation(plan, new PlanViolation { Kind = "violated", Reason = "deleted" });
        Assert.Equal(PlanState.Exhausted, plan.State);
        Assert.Equal(2, plan.Violations.Count);
    }
}

public class PlanSummaryTests
{
    [Fact]
    public void SummaryNamesToolsScopeLimitsAndNoDelete()
    {
        var def = PlanFixtures.Parse(new
        {
            tools = new[] { "revit_place_tags", "revit_set_parameter" },
            elementIds = Enumerable.Range(1, 318).ToArray(),
            categories = new[] { "Electrical Fixtures" },
            maxCalls = 40
        }, title: "Tag all sockets on level 2", steps: Enumerable.Range(1, 14).Select(i => $"step {i}").ToArray());

        var text = PlanSummary.Build(def, "P125");

        Assert.StartsWith("PLAN: Tag all sockets on level 2 (14 steps) in 'P125'", text);
        Assert.Contains("revit_place_tags, revit_set_parameter", text);
        Assert.Contains("318 listed elements", text);
        Assert.Contains("Electrical Fixtures", text);
        Assert.Contains("≤ 40 calls", text);
        Assert.Contains("may not delete", text);
        Assert.Contains("14. step 14", text);
    }
}
