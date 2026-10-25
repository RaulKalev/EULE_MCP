using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Documentation.Views;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#104: revit_create_views arguments → specs.</summary>
public class ViewCreateSpecParsingTests
{
    private static Dictionary<string, object?> Args(string viewsJson, string? ifNameExists = null, bool? atomic = null)
    {
        var args = new Dictionary<string, object?> { ["views"] = JToken.Parse(viewsJson) };
        if (ifNameExists != null) args["ifNameExists"] = new JValue(ifNameExists);
        if (atomic != null) args["atomic"] = new JValue(atomic.Value);
        return args;
    }

    private static ViewCreateSpec One(string specJson)
    {
        var request = ViewCreateSpecParser.Parse(Args("[" + specJson + "]"), out var error);
        Assert.True(request != null, error);
        return request!.Views.Single();
    }

    [Fact]
    public void FloorPlanSpec_ReadsTheCommonPart_AndTheFanOut()
    {
        var spec = One("""
            {"kind":"floorPlan","name":"EN {level}","levels":["1. korrus","2. korrus"],"template":"EN_Plaan","scale":"1:50",
             "detailLevel":"Medium","discipline":"Electrical","phase":"New Construction","scopeBox":"SB-1",
             "placeOnSheet":{"sheet":"EN-{n}","xMm":400,"yMm":300},
             "viewRange":{"topOffsetMm":2500,"cutOffsetMm":1200,"bottomOffsetMm":0,"depthOffsetMm":-300},
             "cropTo":{"room":12345,"paddingMm":300}}
            """);
        Assert.Equal(ViewKinds.FloorPlan, spec.Kind);
        Assert.Empty(spec.Errors);
        Assert.Equal("EN {level}", spec.Name);
        Assert.Equal(new[] { "1. korrus", "2. korrus" }, spec.Levels);
        Assert.Equal("EN_Plaan", spec.Template);
        Assert.Equal(50, spec.Scale);
        Assert.Equal("Medium", spec.DetailLevel);
        Assert.Equal("Electrical", spec.Discipline);
        Assert.Equal("SB-1", spec.ScopeBox);
        Assert.Equal("EN-{n}", spec.PlaceOnSheet!.Sheet);
        Assert.Equal(400, spec.PlaceOnSheet.XMm);
        Assert.Equal(1200, spec.ViewRange!.CutOffsetMm);
        Assert.Equal(-300, spec.ViewRange.DepthOffsetMm);
        Assert.Equal(12345, spec.CropTo!.RoomId);
        Assert.Equal(300, spec.CropTo.PaddingMm);
    }

    [Fact]
    public void LevelsAll_AndKindAliases()
    {
        Assert.True(One("""{"kind":"floor plan","levels":"all"}""").AllLevels);
        Assert.True(One("""{"kind":"FloorPlan","levels":["*"]}""").AllLevels);
        Assert.Equal(ViewKinds.ThreeD, One("""{"kind":"3d"}""").Kind);
        Assert.Equal(ViewKinds.CeilingPlan, One("""{"kind":"RCP","level":"1"}""").Kind);
        Assert.Equal("1", One("""{"kind":"ceilingPlan","level":1}""").Level);
    }

    [Fact]
    public void SectionSpec_ReadsPointsDepthAndFlip()
    {
        var spec = One("""{"kind":"section","from":{"x":0,"y":0,"z":1000},"to":[5000,0],"depthMm":1500,"heightMm":3000,"flip":true}""");
        Assert.Equal(ViewKinds.Section, spec.Kind);
        Assert.Equal(1000, spec.From!.Value.Z);
        Assert.Equal(5000, spec.To!.Value.X);
        Assert.Equal(0, spec.To.Value.Z);
        Assert.Equal(1500, spec.DepthMm);
        Assert.Equal(3000, spec.HeightMm);
        Assert.True(spec.Flip);
        Assert.Equal(SectionBoxMath.DefaultDepthMm, One("""{"kind":"section","from":[0,0],"to":[1,0]}""").DepthMm);
    }

    [Fact]
    public void CalloutAnd3DSpecs_ReadTheirRegion()
    {
        var callout = One("""{"kind":"callout","parentView":"EN 1. korrus","aroundRoom":{"id":77,"linkInstanceId":900},"paddingMm":250}""");
        Assert.Equal("EN 1. korrus", callout.ParentView);
        Assert.Equal(77, callout.Region!.RoomId);
        Assert.Equal(900, callout.Region.RoomLinkInstanceId);
        Assert.Equal(250, callout.Region.PaddingMm);

        var threeD = One("""{"kind":"threeD","aroundElements":[5,6,5],"isometric":false,"orientation":"iso-ne"}""");
        Assert.Equal(new long[] { 5, 6 }, threeD.Region!.ElementIds);
        Assert.False(threeD.Isometric);
        Assert.Equal("iso-ne", threeD.Orientation!.Preset);

        var boxed = One("""{"kind":"threeD","box":{"min":{"x":10,"y":20,"z":30},"max":{"x":0,"y":0,"z":0}}}""");
        Assert.Equal(0, boxed.Region!.Box!.Min.X);
        Assert.Equal(30, boxed.Region.Box.Max.Z);
    }

    [Fact]
    public void LegendSpec_ReadsTheSource()
    {
        var spec = One("""{"kind":"legend","name":"EN tingmärgid","sourceLegend":"Legend 1"}""");
        Assert.Equal("Legend 1", spec.SourceLegend);
    }

    [Fact]
    public void BadEntries_CarryErrors_InsteadOfFailingTheRequest()
    {
        var request = ViewCreateSpecParser.Parse(Args("""
            [{"kind":"elevation"},{"name":"x"},{"kind":"section","from":{"x":"a","y":0}},{"kind":"threeD","box":{"min":{"x":0,"y":0}},"aroundElements":[1]},
             {"kind":"floorPlan","level":"1","scale":-5},{"kind":"threeD","orientation":"sideways"},"not an object"]
            """), out _);
        Assert.NotNull(request);
        Assert.Contains("Unknown kind", request!.Views[0].Errors.Single());
        Assert.Contains("'kind' is required", request.Views[1].Errors.Single());
        Assert.NotEmpty(request.Views[2].Errors);
        Assert.Contains(request.Views[3].Errors, e => e.Contains("one extent source") || e.Contains("needs min and max") || e.Contains("box.max"));
        Assert.Contains(request.Views[4].Errors, e => e.Contains("scale"));
        Assert.Contains(request.Views[5].Errors, e => e.Contains("orientation"));
        Assert.Contains("not an object", request.Views[6].Errors.Single());
    }

    [Fact]
    public void RequestLevelChecks()
    {
        Assert.Null(ViewCreateSpecParser.Parse(new Dictionary<string, object?>(), out var missing));
        Assert.Contains("'views' is required", missing);
        Assert.Null(ViewCreateSpecParser.Parse(Args("[]"), out var empty));
        Assert.Contains("empty", empty);
        var tooMany = "[" + string.Join(",", Enumerable.Repeat("""{"kind":"drafting"}""", ViewCreateSpecParser.MaxViews + 1)) + "]";
        Assert.Null(ViewCreateSpecParser.Parse(Args(tooMany), out var limit));
        Assert.Contains("limit", limit);
        Assert.Null(ViewCreateSpecParser.Parse(Args("""[{"kind":"drafting"}]""", ifNameExists: "overwrite"), out var mode));
        Assert.Contains("ifNameExists", mode);

        var parsed = ViewCreateSpecParser.Parse(Args("""[{"kind":"drafting"}]""", ifNameExists: "SKIP", atomic: true), out _);
        Assert.Equal(NameConflictMode.Skip, parsed!.IfNameExists);
        Assert.True(parsed.Atomic);
        Assert.Equal(NameConflictMode.Suffix, ViewCreateSpecParser.Parse(Args("""[{"kind":"drafting"}]"""), out _)!.IfNameExists);
    }

    [Fact]
    public void ViewsGivenAsAJsonString_AreRead()
    {
        var args = new Dictionary<string, object?> { ["views"] = """[{"kind":"drafting","name":"D1"}]""" };
        var request = ViewCreateSpecParser.Parse(args, out var error);
        Assert.True(request != null, error);
        Assert.Equal("D1", request!.Views[0].Name);
    }

    [Fact]
    public void Region_ForSetViewExtent_AcceptsScopeBox_AndRejectsTwoSources()
    {
        var errors = new List<string>();
        var region = ViewCreateSpecParser.ParseRegion(JObject.Parse("""{"scopeBox":"SB 2","paddingMm":0}"""), errors, null, allowScopeBox: true);
        Assert.Empty(errors);
        Assert.Equal("SB 2", region.ScopeBox);
        Assert.Equal(1, region.SourceCount);

        ViewCreateSpecParser.ParseRegion(JObject.Parse("""{"room":5,"elements":[1,2]}"""), errors, "cropTo", allowScopeBox: true);
        Assert.Contains(errors, e => e.Contains("one extent source"));
    }
}
