using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Documentation.Views;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#104: fan-out per level, naming, ifNameExists, kind-specific validation and the approval summary.</summary>
public class ViewCreatePlannerTests
{
    private static readonly List<LevelInfo> Levels = new()
    {
        new LevelInfo(311, "2. korrus", 3600),
        new LevelInfo(310, "1. korrus", 0),
        new LevelInfo(312, "3. korrus", 7200)
    };

    private static ViewCreateRequest Request(string viewsJson, NameConflictMode mode = NameConflictMode.Suffix)
    {
        var request = ViewCreateSpecParser.Parse(new Dictionary<string, object?> { ["views"] = JToken.Parse(viewsJson) }, out var error);
        Assert.True(request != null, error);
        request!.IfNameExists = mode;
        return request;
    }

    private static List<PlannedView> Plan(string viewsJson, NameConflictMode mode = NameConflictMode.Suffix, params string[] existing)
    {
        var planned = ViewCreatePlanner.Plan(Request(viewsJson, mode), Levels, existing, out var error);
        Assert.Null(error);
        return planned;
    }

    [Fact]
    public void AllLevels_FanOutInElevationOrder_WithLevelAndNInNameAndSheet()
    {
        var planned = Plan("""[{"kind":"floorPlan","name":"EN {level}","levels":"all","placeOnSheet":{"sheet":"EN-10{n}"}}]""");
        Assert.Equal(3, planned.Count);
        Assert.All(planned, p => Assert.True(p.IsReady));
        Assert.Equal(new[] { "EN 1. korrus", "EN 2. korrus", "EN 3. korrus" }, planned.Select(p => p.Name));
        Assert.Equal(new long[] { 310, 311, 312 }, planned.Select(p => p.Level!.Id));
        Assert.Equal(new[] { "EN-101", "EN-102", "EN-103" }, planned.Select(p => p.Sheet));
        Assert.Equal(new[] { 1, 2, 3 }, planned.Select(p => p.FanOutIndex));
    }

    [Fact]
    public void LevelsByNameOrId_AndOneSheetPerLevel()
    {
        var planned = Plan("""[{"kind":"ceilingPlan","name":"EN lagi {level}","levels":["312","1. KORRUS"],"placeOnSheet":{"sheets":["A","B"]}}]""");
        Assert.Equal(new[] { "EN lagi 3. korrus", "EN lagi 1. korrus" }, planned.Select(p => p.Name));
        Assert.Equal(new[] { "A", "B" }, planned.Select(p => p.Sheet));
    }

    [Fact]
    public void FanOutWithoutPlaceholder_AppendsTheLevelName()
    {
        var planned = Plan("""[{"kind":"floorPlan","name":"EN plaan","levels":["1. korrus","2. korrus"]}]""");
        Assert.Equal(new[] { "EN plaan 1. korrus", "EN plaan 2. korrus" }, planned.Select(p => p.Name));
        Assert.Contains(planned[0].Notes, n => n.Contains("appended"));
    }

    [Fact]
    public void MissingLevel_IsInvalidWithItsCode_AndTheOthersStayReady()
    {
        var planned = Plan("""[{"kind":"floorPlan","name":"EN {level}","levels":["1. korrus","9. korrus"]}]""");
        Assert.True(planned[0].IsReady);
        Assert.Equal(ViewCreateStatus.Invalid, planned[1].Status);
        Assert.Equal(ViewCreateCodes.LevelNotFound, planned[1].Code);
        Assert.Contains("9. korrus", planned[1].Reason);
        Assert.Contains("1. korrus", planned[1].Reason);   // the available levels are listed
    }

    [Fact]
    public void PlanWithoutLevel_IsInvalid()
    {
        var planned = Plan("""[{"kind":"floorPlan","name":"X"},{"kind":"floorPlan","level":"1. korrus","levels":["2. korrus"]}]""");
        Assert.Equal(ViewCreateCodes.InvalidSpec, planned[0].Code);
        Assert.Contains("needs 'level'", planned[0].Reason);
        Assert.Contains("not both", planned[1].Reason);
    }

    [Fact]
    public void ExistingNames_GetASuffix_ByDefault_AlsoWithinTheRequest()
    {
        var planned = Plan("""[{"kind":"drafting","name":"Detail"},{"kind":"drafting","name":"Detail"},{"kind":"drafting","name":"detail 1"}]""",
            NameConflictMode.Suffix, "Detail");
        Assert.Equal("Detail 1", planned[0].Name);
        Assert.Equal("Detail 2", planned[1].Name);
        Assert.Equal("detail 1 1", planned[2].Name);   // names compare case-insensitively
        Assert.Contains(planned[0].Notes, n => n.Contains("already exists"));
    }

    [Fact]
    public void IfNameExists_SkipAndFail()
    {
        var skipped = Plan("""[{"kind":"drafting","name":"Detail"},{"kind":"drafting","name":"New"}]""", NameConflictMode.Skip, "Detail");
        Assert.Equal(ViewCreateStatus.Skipped, skipped[0].Status);
        Assert.Equal(ViewCreateCodes.ViewNameExists, skipped[0].Code);
        Assert.True(skipped[1].IsReady);

        var failed = Plan("""[{"kind":"drafting","name":"Detail"}]""", NameConflictMode.Fail, "Detail");
        Assert.Equal(ViewCreateStatus.Invalid, failed[0].Status);
        Assert.Equal(ViewCreateCodes.ViewNameExists, failed[0].Code);
    }

    [Fact]
    public void NoName_LeavesRevitsDefault()
    {
        var planned = Plan("""[{"kind":"threeD"}]""");
        Assert.True(planned[0].IsReady);
        Assert.Null(planned[0].Name);
    }

    [Fact]
    public void ForbiddenCharactersAndUnknownPlaceholders_AreInvalid()
    {
        var planned = Plan("""[{"kind":"drafting","name":"A: B"},{"kind":"drafting","name":"D {level}"},{"kind":"drafting","name":"D {x}"}]""");
        Assert.Contains("not allow", planned[0].Reason);
        Assert.Contains("only plans have a level", planned[1].Reason);
        Assert.Contains("not allow", planned[2].Reason);
    }

    [Fact]
    public void Section_GetsItsBox_WithTheLevelRangeByDefault()
    {
        var planned = Plan("""[{"kind":"section","name":"S1","from":{"x":0,"y":0,"z":3700},"to":{"x":8000,"y":0,"z":3700}}]""");
        var box = planned[0].Section;
        Assert.NotNull(box);
        Assert.Equal(3600, box!.BottomMm);
        Assert.Equal(7200, box.TopMm);
        Assert.Equal(SectionBoxMath.DefaultDepthMm, box.DepthMm);
        Assert.Equal(1, box.LookDirection.Y, 6);
    }

    [Fact]
    public void KindSpecificValidation()
    {
        var planned = Plan("""
            [{"kind":"section","from":{"x":0,"y":0}},
             {"kind":"section","from":{"x":0,"y":0},"to":{"x":0,"y":0.2,"z":3000}},
             {"kind":"callout","from":{"x":0,"y":0},"to":{"x":1000,"y":1000}},
             {"kind":"callout","parentView":"P"},
             {"kind":"callout","parentView":"P","from":{"x":0,"y":0},"to":{"x":1,"y":1},"aroundElements":[3]},
             {"kind":"legend","name":"L"},
             {"kind":"callout","parentView":"P","referenceView":"R","from":{"x":0,"y":0},"to":{"x":1,"y":1},"placeOnSheet":"S1"},
             {"kind":"callout","parentView":"P","aroundRoom":5,"paddingMm":200}]
            """);
        Assert.Contains("'from' and 'to'", planned[0].Reason);
        Assert.Contains("vertical", planned[1].Reason);
        Assert.Contains("parentView", planned[2].Reason);
        Assert.Contains("corners", planned[3].Reason);
        Assert.Contains("not both", planned[4].Reason);
        Assert.Equal(ViewCreateCodes.LegendSourceRequired, planned[5].Code);
        Assert.Contains("cannot create a legend", planned[5].Reason);
        Assert.Contains("cannot be placed on a sheet", planned[6].Reason);
        Assert.True(planned[7].IsReady);
    }

    [Fact]
    public void ReferenceCallout_HasNoName()
    {
        var planned = Plan("""[{"kind":"callout","name":"C","parentView":"P","referenceView":"R","from":{"x":0,"y":0},"to":{"x":1,"y":1}}]""");
        Assert.True(planned[0].IsReady);
        Assert.True(planned[0].IsReferenceCallout);
        Assert.Null(planned[0].Name);
    }

    [Fact]
    public void ParseErrors_BecomeInvalidSpecs()
    {
        var planned = Plan("""[{"kind":"bogus"},{"kind":"drafting"}]""");
        Assert.Equal(ViewCreateCodes.InvalidSpec, planned[0].Code);
        Assert.True(planned[1].IsReady);
    }

    [Fact]
    public void FanOutBeyondTheLimit_IsRefused()
    {
        var many = Enumerable.Range(0, 120).Select(i => new LevelInfo(i + 1, $"L{i}", i * 3000)).ToList();
        var request = Request("""[{"kind":"floorPlan","name":"P {level}","levels":"all"}]""");
        ViewCreatePlanner.Plan(request, many, Array.Empty<string>(), out var error);
        Assert.Contains("limit", error);
    }

    [Fact]
    public void PlanOnlyFields_OnOtherKinds_AreNoted()
    {
        var planned = Plan("""[{"kind":"drafting","level":"1. korrus","cropTo":{"room":3},"viewRange":{"cutOffsetMm":1}}]""");
        Assert.True(planned[0].IsReady);
        Assert.Equal(3, planned[0].Notes.Count);
    }

    [Theory]
    [InlineData("FloorPlan", "Detail", true)]
    [InlineData("FloorPlan", "FloorPlan", true)]
    [InlineData("FloorPlan", "CeilingPlan", false)]
    [InlineData("CeilingPlan", "Detail", false)]
    [InlineData("CeilingPlan", "CeilingPlan", true)]
    [InlineData("Section", "Detail", true)]
    [InlineData("Section", "Section", true)]
    [InlineData("Section", "FloorPlan", false)]
    [InlineData("StructuralPlan", "StructuralPlan", true)]
    [InlineData("Drafting", "Detail", false)]
    [InlineData("ThreeDimensional", "Detail", false)]
    public void CalloutCompatibility(string parent, string type, bool compatible) =>
        Assert.Equal(compatible, CalloutRules.IsCompatible(parent, type));

    [Fact]
    public void CalloutDefaultType_IsDetail_ExceptInCeilingPlans()
    {
        Assert.Equal("Detail", CalloutRules.DefaultTypeFamily("FloorPlan"));
        Assert.Equal("CeilingPlan", CalloutRules.DefaultTypeFamily("CeilingPlan"));
    }

    [Fact]
    public void Summaries_CountPerKind_LevelsTemplatesAndSheets()
    {
        var request = Request("""
            [{"kind":"floorPlan","name":"EN {level}","levels":["1. korrus","2. korrus","3. korrus"],"template":"EN_Plaan","placeOnSheet":{"sheets":["A","B","C"]}},
             {"kind":"section","from":{"x":0,"y":0},"to":{"x":5000,"y":0}},
             {"kind":"threeD"}]
            """);
        var fromRequest = ViewCreatePlanner.Summarize(request);
        Assert.StartsWith("Create 5 view(s): 3 floor plans (levels 1. korrus, 2. korrus, 3. korrus, template EN_Plaan), 1 section and 1 3D view and place 3 on sheets.", fromRequest);

        var planned = ViewCreatePlanner.Plan(request, Levels, Array.Empty<string>(), out _);
        var fromPlan = ViewCreatePlanner.SummarizePlanned(planned, request);
        Assert.StartsWith("Create 5 view(s): 3 floor plans (levels 1. korrus, 2. korrus, 3. korrus, template EN_Plaan), 1 section and 1 3D view and place 3 on sheets.", fromPlan);

        var all = ViewCreatePlanner.Summarize(Request("""[{"kind":"floorPlan","levels":"all","template":"EN_Plaan"}]"""));
        Assert.Contains("floor plans for all levels (template EN_Plaan)", all);
    }

    [Fact]
    public void UniqueName_FollowsTheViewManagerRule()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A", "A 1" };
        Assert.Equal("A 2", ViewCreatePlanner.UniqueName("A", taken));
        Assert.Equal("B", ViewCreatePlanner.UniqueName("B", taken));
    }
}
