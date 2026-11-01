using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Approval;
using RevitMCP.Addin.FilledRegions;
using RevitMCP.Addin.Plans;
using RevitMCP.Bridge;
using RevitMCP.Core.Models;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#135: reading specs and rules, rule expansion with its type map, skipped sources, and the tool surface.</summary>
public class FilledRegionSpecTests
{
    private static Dictionary<string, object?> Args(string? regions = null, string? rules = null)
    {
        var args = new Dictionary<string, object?>();
        if (regions != null) args["regions"] = JToken.Parse(regions);
        if (rules != null) args["rules"] = JToken.Parse(rules);
        return args;
    }

    private static RegionSpec One(string json)
    {
        var request = FilledRegionSpecParser.ParseCreate(Args("[" + json + "]"), out var error);
        Assert.True(request != null, error);
        return request!.Regions.Single();
    }

    private static RegionRule Rule(string json)
    {
        var request = FilledRegionSpecParser.ParseCreate(Args(rules: "[" + json + "]"), out var error);
        Assert.True(request != null, error);
        return request!.Rules.Single();
    }

    // ── Direct specs ─────────────────────────────────────────────────────────

    [Fact]
    public void ARequestNeedsRegionsOrRules()
    {
        Assert.Null(FilledRegionSpecParser.ParseCreate(Args(), out var error));
        Assert.Contains("regions", error);
    }

    [Fact]
    public void ArgumentsMayArriveAsJsonStrings()
    {
        var args = new Dictionary<string, object?> { ["regions"] = """[{"view":1,"type":2,"rectangle":{"from":{"x":0,"y":0},"to":{"x":1,"y":1}}}]""" };
        var request = FilledRegionSpecParser.ParseCreate(args, out var error);
        Assert.True(request != null, error);
        Assert.Equal(RegionShapeKind.Rectangle, request!.Regions[0].Shape.Kind);
        Assert.Null(FilledRegionSpecParser.ParseCreate(new Dictionary<string, object?> { ["regions"] = "not json" }, out error));
        Assert.Contains("not a JSON array", error);
    }

    [Fact]
    public void TooManyRegionsAreRefused()
    {
        var many = "[" + string.Join(",", Enumerable.Repeat("{}", FilledRegionSpecParser.MaxRegions + 1)) + "]";
        Assert.Null(FilledRegionSpecParser.ParseCreate(Args(many), out var error));
        Assert.Contains("limit", error);
    }

    [Fact]
    public void LoopsWithAHoleAreRead()
    {
        var spec = One("""
            {"view":"Plan 1","type":"Blue","lineStyle":"Thin Lines","parameters":{"Comments":"ok","Mark":5},
             "loops":[[{"x":0,"y":0,"z":3000},{"x":1000,"y":0},{"x":1000,"y":1000},{"x":0,"y":0}],
                      [{"x":200,"y":100},{"x":300,"y":100},{"x":300,"y":200},{"x":200,"y":100}]],
             "paddingMm":25}
            """);
        Assert.Empty(spec.Errors);
        Assert.Equal("Plan 1", spec.View);
        Assert.Equal("Thin Lines", spec.LineStyle);
        Assert.Equal("ok", spec.Parameters["comments"]);
        Assert.Equal("5", spec.Parameters["Mark"]);
        Assert.Equal(RegionShapeKind.Loops, spec.Shape.Kind);
        Assert.Equal(2, spec.Shape.Loops.Count);
        Assert.Equal(3000, spec.Shape.Loops[0][0].Z);
        Assert.Null(spec.Shape.Loops[0][1].Z);
        Assert.Equal(25, spec.Shape.PaddingMm);
    }

    [Fact]
    public void AFlatPointListIsOneLoop()
    {
        var spec = One("""{"view":1,"type":2,"loops":[{"x":0,"y":0},{"x":1,"y":0},{"x":1,"y":1},{"x":0,"y":0}]}""");
        Assert.Empty(spec.Errors);
        Assert.Equal(4, Assert.Single(spec.Shape.Loops).Count);
    }

    [Fact]
    public void ArcMidAndViewSpaceAreRead()
    {
        var spec = One("""
            {"view":1,"type":2,"space":"view","closeLoops":true,
             "loops":[[{"u":-5,"v":0,"arcMid":{"u":0,"v":-5}},{"u":5,"v":0,"arcMid":{"u":0,"v":5}}]]}
            """);
        Assert.Empty(spec.Errors);
        Assert.True(spec.Shape.ViewSpace);
        Assert.True(spec.Shape.CloseLoops);
        Assert.Equal(-5, spec.Shape.Loops[0][0].ArcMid!.V);
    }

    [Theory]
    [InlineData("""{"type":2,"rectangle":{"from":{"x":0,"y":0},"to":{"x":1,"y":1}}}""", "view")]
    [InlineData("""{"view":1,"rectangle":{"from":{"x":0,"y":0},"to":{"x":1,"y":1}}}""", "type")]
    [InlineData("""{"view":1,"type":2}""", "one shape")]
    [InlineData("""{"view":1,"type":2,"room":5,"rectangle":{"center":{"x":0,"y":0},"widthMm":1,"heightMm":1}}""", "one shape only")]
    [InlineData("""{"view":1,"type":2,"space":"sheet","room":5}""", "space must be")]
    [InlineData("""{"view":1,"type":2,"rectangle":{"center":{"x":0,"y":0},"widthMm":-1,"heightMm":1}}""", "positive")]
    [InlineData("""{"view":1,"type":2,"loops":[[{"z":1}]]}""", "x and y")]
    [InlineData("""{"view":1,"type":2,"space":"view","loops":[[{"x":1,"y":1}]]}""", "{u, v}")]
    [InlineData("""{"view":1,"type":2,"room":{"level":"1"}}""", "'room' must be")]
    [InlineData("""{"view":1,"type":2,"room":5,"boundaryLocation":"middle"}""", "boundaryLocation")]
    [InlineData("""{"view":1,"type":2,"detailLines":[]}""", "detailLines")]
    [InlineData("""{"view":1,"type":2,"element":{"linkInstanceId":3}}""", "'element'")]
    [InlineData("""{"view":1,"type":2,"room":5,"parameters":[1]}""", "parameters")]
    public void BadSpecsSayWhatIsWrong(string json, string expected)
    {
        var spec = One(json);
        Assert.Contains(spec.Errors, e => e.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ARoomIsReadFromAnIdOrAnObject_WithItsLink()
    {
        Assert.Equal(412233, One("""{"view":1,"type":2,"room":412233}""").Shape.RoomId);

        var linked = One("""{"view":1,"type":2,"room":{"id":7,"linkInstanceId":99},"boundaryLocation":"center"}""").Shape;
        Assert.Equal(RegionShapeKind.Room, linked.Kind);
        Assert.Equal(7, linked.RoomId);
        Assert.Equal(99, linked.LinkInstanceId);
        Assert.Equal("center", linked.BoundaryLocation);

        var byNumber = One("""{"view":1,"type":2,"room":{"number":"101","level":"1. korrus"}}""").Shape;
        Assert.Equal("101", byNumber.RoomNumber);
        Assert.Equal("1. korrus", byNumber.RoomLevel);
    }

    [Fact]
    public void DetailLinesAndElementsAreRead()
    {
        Assert.Equal(new long[] { 1, 2, 3 }, One("""{"view":1,"type":2,"detailLines":[1,2,3]}""").Shape.DetailLineIds);
        var element = One("""{"view":1,"type":2,"element":{"id":8,"linkInstanceId":9}}""").Shape;
        Assert.Equal(RegionShapeKind.ElementBox, element.Kind);
        Assert.Equal(8, element.ElementId);
        Assert.Equal(9, element.LinkInstanceId);
    }

    // ── Rules ────────────────────────────────────────────────────────────────

    private const string DepartmentRule = """
        {"view":"Plan 1",
         "source":{"kind":"rooms","level":"1. korrus","linkInstanceId":99,
                   "filters":[{"parameterName":"Name","operator":"contains","value":"Office"}]},
         "shape":{"kind":"roomBoundary","paddingMm":-50},
         "typeMap":{"parameter":"Department","map":{"Sales":"Blue","Admin ":"Red","Empty":"Blue"}},
         "lineStyle":"Thin Lines","parameters":{"Comments":"rule"}}
        """;

    [Fact]
    public void ARuleIsRead()
    {
        var rule = Rule(DepartmentRule);
        Assert.Empty(rule.Errors);
        Assert.Equal(RuleSourceKind.Rooms, rule.SourceKind);
        Assert.Equal("1. korrus", rule.Level);
        Assert.Equal(99, rule.LinkInstanceId);
        Assert.Equal(RegionShapeKind.Room, rule.ShapeKind);
        Assert.Equal(-50, rule.PaddingMm);
        Assert.Equal("contains", Assert.Single(rule.Filters).Operator);
        Assert.Equal("Department", rule.TypeMap!.Parameter);
        Assert.Equal(3, rule.TypeMap.Count);
    }

    [Fact]
    public void AnElementRuleDefaultsToTheirBoundingBoxes()
    {
        var rule = Rule("""{"view":1,"type":"Blue","source":{"kind":"elements","categories":["Mechanical Equipment"],"inView":"Plan 1"}}""");
        Assert.Empty(rule.Errors);
        Assert.Equal(RegionShapeKind.ElementBox, rule.ShapeKind);
        Assert.Equal("Plan 1", rule.InView);
        Assert.Equal("Mechanical Equipment", Assert.Single(rule.Categories));
    }

    [Theory]
    [InlineData("""{"type":"T","source":{"kind":"rooms"}}""", "view")]
    [InlineData("""{"view":1,"type":"T"}""", "'source' is required")]
    [InlineData("""{"view":1,"type":"T","source":{"kind":"walls"}}""", "source.kind")]
    [InlineData("""{"view":1,"source":{"kind":"rooms"}}""", "Give 'type'")]
    [InlineData("""{"view":1,"type":"T","typeMap":{"parameter":"P","map":{"a":"b"}},"source":{"kind":"rooms"}}""", "not both")]
    [InlineData("""{"view":1,"typeMap":{"map":{"a":"b"}},"source":{"kind":"rooms"}}""", "typeMap.parameter")]
    [InlineData("""{"view":1,"typeMap":{"parameter":"P","map":{}},"source":{"kind":"rooms"}}""", "typeMap.map")]
    [InlineData("""{"view":1,"typeMap":{"parameter":"P","map":{"a":"X","A ":"Y"}},"source":{"kind":"rooms"}}""", "twice")]
    [InlineData("""{"view":1,"type":"T","source":{"kind":"elements"}}""", "every element")]
    [InlineData("""{"view":1,"type":"T","source":{"kind":"rooms"},"shape":{"kind":"circle"}}""", "shape.kind")]
    [InlineData("""{"view":1,"type":"T","source":{"kind":"rooms","filters":[{"value":"x"}]}}""", "parameterName")]
    public void BadRulesSayWhatIsWrong(string json, string expected) =>
        Assert.Contains(Rule(json).Errors, e => e.Contains(expected, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void AFixedTypeGoesToEverySource_AsOrdinarySpecsOfTheSameShape()
    {
        var rule = Rule("""{"view":"Plan 1","type":"Blue","source":{"kind":"rooms","linkInstanceId":99},"shape":{"paddingMm":10,"boundaryLocation":"center"},"parameters":{"Comments":"x"}}""");
        var expansion = RuleExpander.Expand(rule, new[]
        {
            new RuleSourceItem { Id = 1, Label = "Room 101" },
            new RuleSourceItem { Id = 2, Label = "Room 102" }
        }, firstIndex: 5);

        Assert.Empty(expansion.Skipped);
        Assert.Equal(new[] { 5, 6 }, expansion.Regions.Select(r => r.Index));
        var first = expansion.Regions[0];
        Assert.Equal("Plan 1", first.View);
        Assert.Equal("Blue", first.Type);
        Assert.Equal(0, first.RuleIndex);
        Assert.Equal(1, first.SourceId);
        Assert.Equal("Room 101", first.SourceLabel);
        Assert.Equal(RegionShapeKind.Room, first.Shape.Kind);
        Assert.Equal(1, first.Shape.RoomId);
        Assert.Equal(99, first.Shape.LinkInstanceId);
        Assert.Equal(10, first.Shape.PaddingMm);
        Assert.Equal("center", first.Shape.BoundaryLocation);
        Assert.Equal("x", first.Parameters["Comments"]);
        Assert.Empty(first.Errors);
    }

    [Fact]
    public void ABoundingBoxRuleTargetsTheElement()
    {
        var rule = Rule("""{"view":1,"type":"Blue","source":{"kind":"elements","categories":["Doors"]}}""");
        var region = RuleExpander.Expand(rule, new[] { new RuleSourceItem { Id = 44, Label = "Door" } }).Regions.Single();
        Assert.Equal(RegionShapeKind.ElementBox, region.Shape.Kind);
        Assert.Equal(44, region.Shape.ElementId);
        Assert.Equal(0, region.Shape.RoomId);
    }

    [Fact]
    public void TheMapPicksTheType_IgnoringCaseAndOuterSpaces()
    {
        var expansion = RuleExpander.Expand(Rule(DepartmentRule), new[]
        {
            new RuleSourceItem { Id = 1, Label = "A", ParameterValue = "sales" },
            new RuleSourceItem { Id = 2, Label = "B", ParameterValue = " ADMIN " }
        });
        Assert.Equal(new[] { "Blue", "Red" }, expansion.Regions.Select(r => r.Type));
        Assert.Empty(expansion.Skipped);
    }

    [Fact]
    public void SourcesWithoutAMappedTypeAreListedAndSkipped_NeverGuessed()
    {
        var expansion = RuleExpander.Expand(Rule(DepartmentRule), new[]
        {
            new RuleSourceItem { Id = 1, Label = "A", ParameterValue = "Sales" },
            new RuleSourceItem { Id = 2, Label = "B", ParameterValue = "Kitchen" },
            new RuleSourceItem { Id = 3, Label = "C", ParameterValue = "kitchen " },
            new RuleSourceItem { Id = 4, Label = "D", ParameterValue = "   " },
            new RuleSourceItem { Id = 5, Label = "E", ParameterValue = null },
            new RuleSourceItem { Id = 6, Label = "F", ParameterFound = false },
            new RuleSourceItem { Id = 7, Label = "G", ParameterValue = "Sal" }
        });

        Assert.Equal(1, Assert.Single(expansion.Regions).SourceId);
        Assert.Equal(6, expansion.Skipped.Count);
        Assert.Equal(RegionCodes.ValueNotMapped, expansion.Skipped.Single(s => s.Id == 2).Code);
        Assert.Equal("Kitchen", expansion.Skipped.Single(s => s.Id == 2).Value);
        Assert.Equal(RegionCodes.ValueEmpty, expansion.Skipped.Single(s => s.Id == 4).Code);
        Assert.Equal(RegionCodes.ValueEmpty, expansion.Skipped.Single(s => s.Id == 5).Code);
        Assert.Equal(RegionCodes.ParameterNotFound, expansion.Skipped.Single(s => s.Id == 6).Code);
        Assert.Equal(RegionCodes.ValueNotMapped, expansion.Skipped.Single(s => s.Id == 7).Code);
        Assert.Contains("Department", expansion.Skipped.Single(s => s.Id == 6).Reason);

        Assert.Equal(2, expansion.UnmappedValues["KITCHEN"]);
        Assert.Equal(1, expansion.UnmappedValues["Sal"]);
    }

    [Theory]
    [InlineData(RegionCodes.LoopSelfIntersecting, true)]
    [InlineData(RegionCodes.BoundaryNotInViewPlane, true)]
    [InlineData(RegionCodes.RoomNotBounded, true)]
    [InlineData(RegionCodes.NoBoundingBox, true)]
    [InlineData(RegionCodes.TypeNotFound, false)]
    [InlineData(RegionCodes.ViewNotSupported, false)]
    [InlineData(RegionCodes.InvalidSpec, false)]
    public void ASourceProblemSkipsTheSource_AndAnythingElseIsInvalid(string code, bool skipsSource) =>
        Assert.Equal(skipsSource, RegionCodes.IsSourceProblem(code));

    // ── Edits and types ──────────────────────────────────────────────────────

    private static RegionEditSpec Edit(string json)
    {
        var request = FilledRegionEditParser.ParseEdits(new Dictionary<string, object?> { ["edits"] = JToken.Parse("[" + json + "]") }, out var error);
        Assert.True(request != null, error);
        return request!.Edits.Single();
    }

    [Fact]
    public void AnEditOfTypeOrParametersDoesNotReshape()
    {
        var edit = Edit("""{"id":5,"type":"Red","parameters":{"Comments":"x"}}""");
        Assert.Empty(edit.Errors);
        Assert.False(edit.Reshapes);
        Assert.Equal("Red", edit.Type);
    }

    [Fact]
    public void ABoundaryMeansAReshape_AndTakesEveryShapeKind()
    {
        var edit = Edit("""{"id":5,"boundary":{"rectangle":{"from":{"x":0,"y":0},"to":{"x":1,"y":1}},"paddingMm":5}}""");
        Assert.Empty(edit.Errors);
        Assert.True(edit.Reshapes);
        Assert.Equal(RegionShapeKind.Rectangle, edit.Boundary!.Kind);
        Assert.Equal(5, edit.Boundary.PaddingMm);

        Assert.Equal(RegionShapeKind.Room, Edit("""{"id":5,"boundary":{"room":{"id":1,"linkInstanceId":2}}}""").Boundary!.Kind);
        Assert.Equal(RegionShapeKind.DetailLines, Edit("""{"id":5,"boundary":{"detailLines":[1,2,3]}}""").Boundary!.Kind);
    }

    [Theory]
    [InlineData("""{"type":"Red"}""", "'id'")]
    [InlineData("""{"id":5}""", "Nothing to change")]
    [InlineData("""{"id":5,"boundary":{}}""", "one shape")]
    [InlineData("""{"id":5,"boundary":7}""", "must be an object")]
    public void BadEditsSayWhatIsWrong(string json, string expected) =>
        Assert.Contains(Edit(json).Errors, e => e.Contains(expected, StringComparison.OrdinalIgnoreCase));

    private static RegionTypeSpec TypeSpec(string json)
    {
        var request = FilledRegionEditParser.ParseTypes(new Dictionary<string, object?> { ["types"] = JToken.Parse("[" + json + "]") }, out var error);
        Assert.True(request != null, error);
        return request!.Types.Single();
    }

    [Fact]
    public void ATypeToCreateAndATypeToEditAreTold()
    {
        var created = TypeSpec("""{"duplicateFrom":"Blue","name":"Green","foregroundPattern":"solid","foregroundColor":"#00FF00","backgroundPattern":"none","backgroundColor":"10,20,30","lineWeight":3,"isMasking":true}""");
        Assert.Empty(created.Errors);
        Assert.True(created.IsCreate);
        Assert.Equal("solid", created.ForegroundPattern);
        Assert.Equal("none", created.BackgroundPattern);
        Assert.Equal("#00FF00", created.ForegroundColor!.Value.ToHex());
        Assert.Equal("#0A141E", created.BackgroundColor!.Value.ToHex());
        Assert.Equal(3, created.LineWeight);
        Assert.True(created.IsMasking);

        var edited = TypeSpec("""{"type":"Blue","foregroundColor":{"r":1,"g":2,"b":3}}""");
        Assert.Empty(edited.Errors);
        Assert.False(edited.IsCreate);
        Assert.True(edited.ChangesAppearance);
        Assert.Equal("#010203", edited.ForegroundColor!.Value.ToHex());
    }

    [Fact]
    public void AnEmptyPatternMeansNone_AndAnAbsentPatternMeansUnchanged()
    {
        Assert.Null(TypeSpec("""{"type":"Blue","lineWeight":1}""").ForegroundPattern);
        Assert.Equal(string.Empty, TypeSpec("""{"type":"Blue","foregroundPattern":""}""").ForegroundPattern);
    }

    [Theory]
    [InlineData("""{"lineWeight":1}""", "Give 'type'")]
    [InlineData("""{"type":"A","duplicateFrom":"B","name":"C"}""", "Give 'type'")]
    [InlineData("""{"duplicateFrom":"B"}""", "'name' is required")]
    [InlineData("""{"type":"A"}""", "Nothing to change")]
    [InlineData("""{"type":"A","lineWeight":17}""", "lineWeight")]
    [InlineData("""{"type":"A","lineWeight":1.5}""", "lineWeight")]
    [InlineData("""{"type":"A","foregroundColor":"red"}""", "not a colour")]
    [InlineData("""{"type":"A","foregroundColor":{"r":300,"g":0,"b":0}}""", "0-255")]
    [InlineData("""{"type":"A","isMasking":"yes"}""", "isMasking")]
    [InlineData("""{"duplicateFrom":"B","name":"C:D"}""", "refuses")]
    public void BadTypeSpecsSayWhatIsWrong(string json, string expected) =>
        Assert.Contains(TypeSpec(json).Errors, e => e.Contains(expected, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void EditAndTypeRequestsAreBounded()
    {
        Assert.Null(FilledRegionEditParser.ParseEdits(new Dictionary<string, object?>(), out _));
        Assert.Null(FilledRegionEditParser.ParseTypes(new Dictionary<string, object?>(), out _));
        var tooMany = JToken.Parse("[" + string.Join(",", Enumerable.Repeat("{}", FilledRegionEditParser.MaxTypes + 1)) + "]");
        Assert.Null(FilledRegionEditParser.ParseTypes(new Dictionary<string, object?> { ["types"] = tooMany }, out var error));
        Assert.Contains("limit", error);
    }

    // ── Summaries ────────────────────────────────────────────────────────────

    private static McpToolRequest Request(string tool, string key, string json) => new()
    {
        ToolName = tool,
        Arguments = new Dictionary<string, object?> { [key] = JToken.Parse(json) }
    };

    [Fact]
    public void TheApprovalSaysWhatARequestDoes()
    {
        var create = FilledRegionApprovalSummaries.TryBuild(Request("revit_create_filled_regions", "rules", "[" + DepartmentRule + "]"));
        Assert.Contains("linked rooms of link 99", create);
        Assert.Contains("mapped from 'Department'", create);

        var direct = FilledRegionApprovalSummaries.TryBuild(Request("revit_create_filled_regions", "regions", """[{"view":1,"type":2,"room":5},{"view":1,"type":2,"detailLines":[1,2,3]}]"""));
        Assert.Contains("2 filled region(s)", direct);
        Assert.Contains("1 view(s)", direct);

        var edit = FilledRegionApprovalSummaries.TryBuild(Request("revit_edit_filled_regions", "edits", """[{"id":5,"type":"Red"},{"id":6,"boundary":{"room":5}}]"""));
        Assert.Contains("ids kept", edit);
        Assert.Contains("may be lost", edit);

        var types = FilledRegionApprovalSummaries.TryBuild(Request("revit_set_filled_region_types", "types", """[{"duplicateFrom":"A","name":"B"},{"type":"C","lineWeight":2}]"""));
        Assert.StartsWith("Create", types);
        Assert.Contains("every region of those types changes", types);

        Assert.Null(FilledRegionApprovalSummaries.TryBuild(Request("revit_create_views", "views", "[]")));
        Assert.Contains("invalid", FilledRegionApprovalSummaries.TryBuild(new McpToolRequest { ToolName = "revit_edit_filled_regions", Arguments = new Dictionary<string, object?>() }));
    }

    // ── Tool surface ─────────────────────────────────────────────────────────

    public static readonly string[] Tools =
    {
        "revit_list_filled_regions", "revit_list_filled_region_types", "revit_preview_create_filled_regions", "revit_create_filled_regions",
        "revit_preview_edit_filled_regions", "revit_edit_filled_regions", "revit_preview_set_filled_region_types", "revit_set_filled_region_types"
    };

    [Fact]
    public void EveryToolIsInTheTagsGroup()
    {
        foreach (var tool in Tools) Assert.Equal("tags", BridgeToolGroups.GroupOf(tool));
        Assert.Contains("filled regions", BridgeToolGroups.Descriptions["tags"]);
    }

    [Theory]
    [InlineData("revit_list_filled_regions", "ReadOnly")]
    [InlineData("revit_list_filled_region_types", "ReadOnly")]
    [InlineData("revit_preview_create_filled_regions", "ReadOnly")]
    [InlineData("revit_create_filled_regions", "RequiresApproval")]
    [InlineData("revit_preview_edit_filled_regions", "ReadOnly")]
    [InlineData("revit_edit_filled_regions", "RequiresApproval")]
    [InlineData("revit_preview_set_filled_region_types", "ReadOnly")]
    [InlineData("revit_set_filled_region_types", "RequiresApproval")]
    public void PermissionsMatchOnBothSides(string tool, string permission)
    {
        Assert.Equal(permission, ToolSourceScan.Addin.Value[tool].Permission);
        var bridge = Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == tool);
        Assert.Equal(permission == "ReadOnly", bridge.ReadOnly);
        Assert.Contains(tool, bridge.Targets);
    }

    [Fact]
    public void EveryToolTakesTheDocumentArgument()
    {
        foreach (var tool in Tools)
        {
            Assert.Contains("\"document\"", Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == tool).Source);
        }
    }

    [Theory]
    [InlineData("revit_create_filled_regions")]
    [InlineData("revit_edit_filled_regions")]         // a reshape makes a replacement
    [InlineData("revit_set_filled_region_types")]     // a spec can duplicate a type
    public void PlansCountTheWriteToolsAsCreating(string tool) =>
        Assert.Equal(PlanToolKind.Creates, PlanToolClassification.Classify(tool));
}
