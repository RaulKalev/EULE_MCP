using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.Plans;
using RevitMCP.Bridge;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#147: revit_create_detail_family's spec parsing, save path, template lookup and line style matching.</summary>
public class DetailFamilySpecTests
{
    private const string Folder = @"C:\Families\Detail";

    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] values)
    {
        var args = new Dictionary<string, object?>
        {
            ["savePath"] = Folder,
            ["familyName"] = "EL_Kilp"
        };
        foreach (var (key, value) in values) args[key] = value is string s && (s.StartsWith("[") || s.StartsWith("{")) ? JToken.Parse(s) : value;
        return args;
    }

    private static (DetailFamilySpec Spec, List<string> Errors) Parse(Dictionary<string, object?> args)
    {
        var errors = new List<string>();
        var spec = DetailFamilySpecParser.Parse(args, errors);
        return (spec, errors);
    }

    [Fact]
    public void ParsesLinesRectanglesArcsAndCircles()
    {
        var (spec, errors) = Parse(Args(
            ("lines", "[{\"start\":[0,0],\"end\":[100,0],\"lineStyle\":\"EL_Kaabel\"}]"),
            ("rectangles", "[{\"min\":{\"x\":50,\"y\":20},\"max\":[-50,-20]}]"),
            ("arcs", "[{\"center\":[0,0],\"radius\":10,\"startAngle\":0,\"endAngle\":90},{\"start\":[0,0],\"mid\":[5,5],\"end\":[10,0]}]"),
            ("circles", "[{\"center\":[0,0],\"radius\":5}]"),
            ("lineStyle", "Thin Lines")));

        Assert.Empty(errors);
        Assert.Equal(8, spec.Curves.Count);
        Assert.Equal(5, spec.CountsByKind()["line"]);
        Assert.Equal(2, spec.CountsByKind()["arc"]);
        Assert.Equal(1, spec.CountsByKind()["circle"]);
        Assert.Equal("EL_Kaabel", spec.Curves[0].LineStyle);
        Assert.All(spec.Curves.Skip(1), c => Assert.Equal("Thin Lines", c.LineStyle));
        Assert.Equal(new[] { "EL_Kaabel", "Thin Lines" }, spec.LineStyleNames());
        Assert.Equal(System.IO.Path.Combine(Folder, "EL_Kilp.rfa"), spec.SavePath);
    }

    [Fact]
    public void RectangleCornersAreNormalised()
    {
        var (spec, errors) = Parse(Args(("rectangles", "[{\"min\":[10,10],\"max\":[-10,-5]}]")));
        Assert.Empty(errors);
        var first = spec.Curves[0];
        Assert.Equal(-10, first.Start.X);
        Assert.Equal(-5, first.Start.Y);
        Assert.Equal(10, first.End.X);
        Assert.All(spec.Curves, c => Assert.Equal("rectangles[0]", c.Source));
    }

    [Fact]
    public void SavePathIsRequired()
    {
        var args = Args(("lines", "[{\"start\":[0,0],\"end\":[10,0]}]"));
        args["savePath"] = "";
        var (_, errors) = Parse(args);
        Assert.Contains(errors, e => e.Contains("savePath is required") && e.Contains("ask the user"));
    }

    [Fact]
    public void FullRfaPathGivesTheFamilyName()
    {
        var (path, name, error) = DetailFamilySpecParser.ResolveSavePath(@"C:\Lib\EL_Pistik.rfa", "");
        Assert.Null(error);
        Assert.Equal("EL_Pistik", name);
        Assert.Equal(@"C:\Lib\EL_Pistik.rfa", path);
    }

    [Theory]
    [InlineData(@"C:\Lib\EL_Pistik.rfa", "Other", "differs from the file name")]
    [InlineData(@"C:\Lib", "", "familyName is required")]
    [InlineData(@"C:\Lib", "Bad:Name", "characters")]
    [InlineData(@"Lib", "Name", "absolute")]
    [InlineData(@"C:\Lib\notes.txt", "Name", "folder or a .rfa")]
    public void SavePathProblems(string savePath, string familyName, string expected)
    {
        var (_, _, error) = DetailFamilySpecParser.ResolveSavePath(savePath, familyName);
        Assert.NotNull(error);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void RefusesEmptyAndDegenerateGeometry()
    {
        var (_, empty) = Parse(Args());
        Assert.Contains(empty, e => e.StartsWith("Nothing to draw"));

        var (spec, errors) = Parse(Args(
            ("lines", "[{\"start\":[0,0],\"end\":[0.1,0]},{\"start\":[0,0]}]"),
            ("arcs", "[{\"start\":[0,0],\"mid\":[5,0],\"end\":[10,0]},{\"center\":[0,0],\"radius\":5,\"startAngle\":0,\"endAngle\":360}]"),
            ("circles", "[{\"center\":[0,0],\"radius\":-1}]")));
        Assert.Empty(spec.Curves);
        Assert.Contains(errors, e => e.StartsWith("lines[0] is shorter"));
        Assert.Contains(errors, e => e.StartsWith("lines[1].end must be a point"));
        Assert.Contains(errors, e => e.StartsWith("arcs[0]") && e.Contains("one line"));
        Assert.Contains(errors, e => e.StartsWith("arcs[1]") && e.Contains("full circle"));
        Assert.Contains(errors, e => e.StartsWith("circles[0].radius"));
    }

    [Fact]
    public void RefusesUnknownIfExists()
    {
        var (_, errors) = Parse(Args(("lines", "[{\"start\":[0,0],\"end\":[10,0]}]"), ("ifExists", "overwriteAll")));
        Assert.Contains(errors, e => e.Contains("ifExists"));
    }

    [Theory]
    [InlineData(0, 90, 90)]
    [InlineData(270, 90, 180)]
    [InlineData(-90, 90, 180)]
    [InlineData(0, 360, 360)]
    [InlineData(45, 45, 0)]
    public void ArcSweepIsCounterClockwise(double start, double end, double expected) =>
        Assert.Equal(expected, DetailFamilySpecParser.Sweep(start, end), 6);

    [Fact]
    public void TemplatePrefersExplicitThenRootThenEnglishThenBelow()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\T\custom.rft",
            @"C:\T\English\Metric Detail Item.rft"
        };
        Assert.Equal(@"C:\T\custom.rft", DetailFamilySpecParser.ResolveTemplate(@"C:\T\custom.rft", @"C:\T", files.Contains, _ => []).Path);
        Assert.Equal(@"C:\T\English\Metric Detail Item.rft", DetailFamilySpecParser.ResolveTemplate("", @"C:\T", files.Contains, _ => []).Path);

        var below = DetailFamilySpecParser.ResolveTemplate("", @"C:\X", _ => false,
            _ => new[] { @"C:\X\Estonian\Sub\Metric Detail Item.rft", @"C:\X\Other\Metric Detail Item.rft" });
        Assert.Equal(@"C:\X\Other\Metric Detail Item.rft", below.Path);

        Assert.Contains("not a family template", DetailFamilySpecParser.ResolveTemplate(@"C:\T\a.rfa", @"C:\T", _ => true, _ => []).Error);
        Assert.Contains("pass templatePath", DetailFamilySpecParser.ResolveTemplate("", @"C:\none", _ => false, _ => []).Error);
    }

    [Fact]
    public void LineStylesMatchTheProjectCaseInsensitively()
    {
        var (spec, _) = Parse(Args(("lines", "[{\"start\":[0,0],\"end\":[10,0],\"lineStyle\":\"el_kaabel\"},{\"start\":[0,0],\"end\":[0,10],\"lineStyle\":\"EL_Puudub\"}]")));
        var project = new List<ProjectLineStyleInfo>
        {
            new() { Name = "EL_Kaabel", Weight = 3, Color = "#FF0000", Pattern = "Dash" },
            new() { Name = "EL_Pistik", Weight = 1 }
        };
        var plan = DetailFamilyLineStylePlanner.Plan(spec, project);
        Assert.Equal("project", plan[0].Status);
        Assert.Equal("EL_Kaabel", plan[0].Project!.Name);
        Assert.Equal("missing", plan[1].Status);
        Assert.Equal(new[] { "lines[1]" }, plan[1].UsedBy);
        Assert.Contains("EL_Kaabel", DetailFamilyLineStylePlanner.Suggest("EL_Kaab", project));
    }

    [Fact]
    public void ToolsAreInTheEditGroupAndClassified()
    {
        Assert.Equal("edit", BridgeToolGroups.GroupOf("revit_preview_create_detail_family"));
        Assert.Equal("edit", BridgeToolGroups.GroupOf("revit_create_detail_family"));
        Assert.Equal(PlanToolKind.Creates, PlanToolClassification.Classify("revit_create_detail_family"));
        Assert.Equal("ReadOnly", ToolSourceScan.Addin.Value["revit_preview_create_detail_family"].Permission);
        Assert.Equal("RequiresApproval", ToolSourceScan.Addin.Value["revit_create_detail_family"].Permission);
        Assert.True(Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == "revit_preview_create_detail_family").ReadOnly);
        Assert.False(Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == "revit_create_detail_family").ReadOnly);
    }
}
