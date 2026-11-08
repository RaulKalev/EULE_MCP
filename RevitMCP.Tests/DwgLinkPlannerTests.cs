using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Plans;
using RevitMCP.Addin.Services;
using RevitMCP.Bridge;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>revit_link_dwg: current view only in plans, auto-detected scale, view resolution, already-linked files.</summary>
public class DwgLinkPlannerTests
{
    private const string Plan1 = @"C:\Proj\AR_1korrus.dwg";
    private const string Plan2 = @"C:\Proj\AR_2korrus.dwg";

    private static readonly List<DwgLinkViewInfo> Views = new()
    {
        new() { Id = 10, Name = "1. korrus", Kind = "plan", ViewType = "FloorPlan" },
        new() { Id = 11, Name = "2. korrus", Kind = "plan", ViewType = "FloorPlan" },
        new() { Id = 12, Name = "Lõige A", Kind = "section", ViewType = "Section" },
        new() { Id = 13, Name = "Skeem", Kind = "drafting", ViewType = "DraftingView" },
        new() { Id = 14, Name = "{3D}", Kind = "3d", ViewType = "ThreeD" },
        new() { Id = 15, Name = "A-101", Kind = "other", ViewType = "DrawingSheet" },
        new() { Id = 16, Name = "EN plaan", Kind = "plan", ViewType = "FloorPlan", IsTemplate = true },
        new() { Id = 17, Name = "Topelt", Kind = "plan", ViewType = "FloorPlan" },
        new() { Id = 18, Name = "Topelt", Kind = "plan", ViewType = "CeilingPlan" }
    };

    private static DwgLinkContext Context(params DwgExistingLink[] existing) => new()
    {
        ActiveViewId = 10,
        Views = Views,
        Existing = existing,
        FileExists = p => p.StartsWith(@"C:\Proj\", StringComparison.OrdinalIgnoreCase),
        ValidateRead = p => p.StartsWith(@"C:\Secret\", StringComparison.OrdinalIgnoreCase) ? "Not permitted." : null
    };

    private static List<DwgLinkPlanItem> PlanFor(string json, string ifExists = "skip", params DwgExistingLink[] existing)
    {
        var args = JObject.Parse(json).Properties().ToDictionary(p => p.Name, p => (object?)p.Value);
        var errors = new List<string>();
        var requests = DwgLinkPlanner.Parse(args, errors);
        Assert.Empty(errors);
        return DwgLinkPlanner.Plan(requests, Context(existing), ifExists);
    }

    [Fact]
    public void FloorPlanLinksAreCurrentViewOnlyWithAutoScale()
    {
        var item = Assert.Single(PlanFor($"{{\"links\":[{{\"path\":{JsonPath(Plan1)},\"viewId\":11}}]}}"));
        Assert.Equal("willLink", item.Status);
        Assert.True(item.ThisViewOnly);
        Assert.Equal("auto", item.Request.Unit);
        Assert.Equal("origin", item.Request.Placement);
        Assert.Equal(11, item.View!.Id);
    }

    [Fact]
    public void DefaultsToTheActiveViewAndAcceptsPathsWithAViewName()
    {
        var plan = PlanFor($"{{\"paths\":[{JsonPath(Plan1)},{JsonPath(Plan2)}],\"view\":\"lõige a\"}}");
        Assert.All(plan, p => Assert.Equal(12, p.View!.Id));
        Assert.All(plan, p => Assert.True(p.ThisViewOnly));

        var active = Assert.Single(PlanFor($"{{\"links\":[{JsonPath(Plan1)}]}}"));
        Assert.Equal(10, active.View!.Id);
    }

    [Fact]
    public void PerFileValuesOverrideTopLevelOnes()
    {
        var plan = PlanFor($"{{\"unit\":\"mm\",\"viewId\":10,\"links\":[{{\"path\":{JsonPath(Plan1)}}},{{\"path\":{JsonPath(Plan2)},\"viewId\":11,\"unit\":\"auto\",\"placement\":\"shared\",\"pin\":true}}]}}");
        Assert.Equal("mm", plan[0].Request.Unit);
        Assert.Equal(10, plan[0].View!.Id);
        Assert.Equal("auto", plan[1].Request.Unit);
        Assert.Equal("shared", plan[1].Request.Placement);
        Assert.True(plan[1].Request.Pin);
        Assert.Equal(11, plan[1].View!.Id);
    }

    [Fact]
    public void ThisViewOnlyFalseIsAllowedInPlansButNotHonouredInDrafting()
    {
        var plan = PlanFor($"{{\"thisViewOnly\":false,\"links\":[{{\"path\":{JsonPath(Plan1)},\"viewId\":10}},{{\"path\":{JsonPath(Plan2)},\"viewId\":13}}]}}");
        Assert.False(plan[0].ThisViewOnly);
        Assert.Contains(plan[0].Notes, n => n.Contains("every view"));
        Assert.True(plan[1].ThisViewOnly);
        Assert.Contains(plan[1].Notes, n => n.Contains("drafting"));
    }

    [Fact]
    public void ThreeDViewsTakeModelWideLinksOnly()
    {
        var model = Assert.Single(PlanFor($"{{\"links\":[{{\"path\":{JsonPath(Plan1)},\"viewId\":14}}]}}"));
        Assert.Equal("willLink", model.Status);
        Assert.False(model.ThisViewOnly);

        var refused = Assert.Single(PlanFor($"{{\"links\":[{{\"path\":{JsonPath(Plan1)},\"viewId\":14,\"thisViewOnly\":true}}]}}"));
        Assert.Equal("invalid", refused.Status);
        Assert.Contains("3D view", refused.Reason);
    }

    [Theory]
    [InlineData(15, "cannot hold a DWG link")]
    [InlineData(16, "view template")]
    [InlineData(99, "was not found")]
    public void RefusesViewsThatCannotTakeALink(long viewId, string expected)
    {
        var item = Assert.Single(PlanFor($"{{\"links\":[{{\"path\":{JsonPath(Plan1)},\"viewId\":{viewId}}}]}}"));
        Assert.Equal("invalid", item.Status);
        Assert.Contains(expected, item.Reason);
    }

    [Fact]
    public void AmbiguousViewNameAsksForTheId()
    {
        var item = Assert.Single(PlanFor($"{{\"links\":[{{\"path\":{JsonPath(Plan1)},\"view\":\"Topelt\"}}]}}"));
        Assert.Equal("invalid", item.Status);
        Assert.Contains("pass viewId", item.Reason);
    }

    [Theory]
    [InlineData(@"C:\Proj\missing.dxf", "not a .dwg")]
    [InlineData(@"C:\Other\missing.dwg", "does not exist")]
    [InlineData(@"C:\Secret\a.dwg", "Not permitted")]
    [InlineData(@"relative.dwg", "absolute")]
    public void ChecksTheFile(string path, string expected)
    {
        var item = Assert.Single(PlanFor($"{{\"links\":[{{\"path\":{JsonPath(path)},\"viewId\":10}}]}}"));
        Assert.Equal("invalid", item.Status);
        Assert.Contains(expected, item.Reason);
    }

    [Fact]
    public void SkipsAFileAlreadyLinkedIntoThatViewUnlessLinkAgain()
    {
        var existing = new DwgExistingLink { Path = Plan1.ToUpperInvariant(), OwnerViewId = 10, InstanceId = 555 };
        var json = $"{{\"links\":[{{\"path\":{JsonPath(Plan1)},\"viewId\":10}},{{\"path\":{JsonPath(Plan1)},\"viewId\":11}}]}}";

        var plan = PlanFor(json, "skip", existing);
        Assert.Equal("willSkip", plan[0].Status);
        Assert.Equal(555, plan[0].ExistingInstanceId);
        Assert.Equal("willLink", plan[1].Status);

        var again = PlanFor(json, "linkAgain", existing);
        Assert.Equal("willLink", again[0].Status);
        Assert.Contains(again[0].Notes, n => n.Contains("second instance"));
    }

    [Fact]
    public void RefusesTheSameFileTwiceForOneView()
    {
        var plan = PlanFor($"{{\"viewId\":10,\"paths\":[{JsonPath(Plan1)},{JsonPath(Plan1)}]}}");
        Assert.Equal("willLink", plan[0].Status);
        Assert.Equal("invalid", plan[1].Status);
    }

    [Fact]
    public void ParseErrors()
    {
        var errors = new List<string>();
        DwgLinkPlanner.Parse(new Dictionary<string, object?> { ["unit"] = "yards", ["placement"] = "random" }, errors);
        Assert.Contains(errors, e => e.StartsWith("unit 'yards'"));
        Assert.Contains(errors, e => e.StartsWith("placement 'random'"));
        Assert.Contains(errors, e => e.StartsWith("Nothing to link"));
    }

    [Theory]
    [InlineData(80, 40, false)]
    [InlineData(80000, 40000, true)]
    [InlineData(0.08, 0.04, true)]
    public void ExtentsCheckFlagsLikelyWrongUnits(double width, double height, bool flagged) =>
        Assert.Equal(flagged, DwgLinkPlanner.CheckExtents(width, height) != null);

    [Fact]
    public void SurfaceIsInTheDocumentsGroupClassifiedAndLongRunning()
    {
        Assert.Equal("documents", BridgeToolGroups.GroupOf("revit_preview_link_dwg"));
        Assert.Equal("documents", BridgeToolGroups.GroupOf("revit_link_dwg"));
        Assert.Equal(PlanToolKind.Creates, PlanToolClassification.Classify("revit_link_dwg"));
        Assert.True(LongRunningTools.IsLongRunning("revit_link_dwg"));
        Assert.Equal("ReadOnly", ToolSourceScan.Addin.Value["revit_preview_link_dwg"].Permission);
        Assert.Equal("RequiresApproval", ToolSourceScan.Addin.Value["revit_link_dwg"].Permission);
        Assert.True(Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == "revit_preview_link_dwg").ReadOnly);
        Assert.False(Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == "revit_link_dwg").ReadOnly);
    }

    private static string JsonPath(string path) => JToken.FromObject(path).ToString(Newtonsoft.Json.Formatting.None);
}
