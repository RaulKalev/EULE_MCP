using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.Plans;
using RevitMCP.Bridge;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>Line style definitions: revit_set_line_styles and lineStyles of revit_create_detail_family.</summary>
public class LineStyleDefinitionTests
{
    private static (List<LineStyleDefinition> Styles, List<string> Errors) Read(string json)
    {
        var errors = new List<string>();
        var styles = DetailFamilySpecParser.ReadLineStyleDefinitions(
            new Dictionary<string, object?> { ["styles"] = JToken.Parse(json) }, "styles", errors);
        return (styles, errors);
    }

    [Fact]
    public void Reads_name_weight_colour_and_pattern()
    {
        var (styles, errors) = Read(
            "[{\"name\":\"EL_Kaabel\",\"weight\":3,\"color\":\"#ff0000\",\"pattern\":\"Dash\"}," +
            "{\"name\":\"EL_Õhuke\",\"size\":1,\"colour\":[0,0,255],\"pattern\":\"solid\"}," +
            "{\"name\":\"EL_Ainult nimi\"}]");

        Assert.Empty(errors);
        Assert.Equal(3, styles.Count);
        Assert.Equal(3, styles[0].Weight);
        Assert.Equal("#FF0000", styles[0].Color);
        Assert.Equal("Dash", styles[0].Pattern);
        Assert.Equal(1, styles[1].Weight);              // "size" is accepted for the weight
        Assert.Equal("#0000FF", styles[1].Color);
        Assert.Equal(string.Empty, styles[1].Pattern);  // solid
        Assert.False(styles[2].HasValues);
        Assert.Null(styles[2].Pattern);
    }

    [Theory]
    [InlineData("[{\"weight\":2}]", "needs a name")]
    [InlineData("[{\"name\":\"A\"},{\"name\":\"a\"}]", "listed twice")]
    [InlineData("[{\"name\":\"A\",\"weight\":0.35}]", "1 to 16")]
    [InlineData("[{\"name\":\"A\",\"weight\":17}]", "1 to 16")]
    [InlineData("[{\"name\":\"A\",\"color\":\"red\"}]", "not a colour")]
    [InlineData("[{\"name\":\"A:B\"}]", "characters Revit refuses")]
    public void Refuses_bad_definitions(string json, string expected)
    {
        var (_, errors) = Read(json);
        Assert.Contains(errors, e => e.Contains(expected));
    }

    [Fact]
    public void A_new_style_gets_defaults_and_an_existing_one_keeps_what_is_not_given()
    {
        var (styles, _) = Read("[{\"name\":\"Uus\",\"color\":\"#00FF00\"},{\"name\":\"el_kaabel\",\"weight\":5}]");

        var created = DetailFamilySpecParser.MergeLineStyle(styles[0], null);
        Assert.Equal(DetailFamilySpecParser.DefaultLineWeight, created.Weight);
        Assert.Equal("#00FF00", created.Color);
        Assert.Equal(string.Empty, created.Pattern);

        var existing = new ProjectLineStyleInfo { Name = "EL_Kaabel", Weight = 3, Color = "#FF0000", Pattern = "Dash" };
        var edited = DetailFamilySpecParser.MergeLineStyle(styles[1], existing);
        Assert.Equal("EL_Kaabel", edited.Name);      // the document's own spelling
        Assert.Equal(5, edited.Weight);
        Assert.Equal("#FF0000", edited.Color);
        Assert.Equal("Dash", edited.Pattern);
    }

    [Fact]
    public void A_family_can_use_a_style_the_project_does_not_have_when_it_defines_it()
    {
        var errors = new List<string>();
        var spec = DetailFamilySpecParser.Parse(new Dictionary<string, object?>
        {
            ["savePath"] = @"C:\Families\Detail",
            ["familyName"] = "EL_Kilp",
            ["lines"] = JToken.Parse("[{\"start\":[0,0],\"end\":[10,0],\"lineStyle\":\"Oma\"},{\"start\":[0,5],\"end\":[10,5],\"lineStyle\":\"Puudub\"}]"),
            ["lineStyles"] = JToken.Parse("[{\"name\":\"Oma\",\"weight\":4,\"color\":\"#112233\",\"pattern\":\"Dash\"},{\"name\":\"Varuks\"},{\"name\":\"EL_Kaabel\",\"weight\":6}]")
        }, errors);
        Assert.Empty(errors);

        var plan = DetailFamilyLineStylePlanner.Plan(spec, new[]
        {
            new ProjectLineStyleInfo { Name = "EL_Kaabel", Weight = 3, Color = "#FF0000", Pattern = "Center" }
        });

        var own = plan.Single(p => p.Requested == "Oma");
        Assert.Equal("defined", own.Status);
        Assert.Equal(4, own.Project!.Weight);
        Assert.Equal("#112233", own.Project.Color);
        Assert.Equal("Dash", own.Project.Pattern);
        Assert.Equal(new[] { "lines[0]" }, own.UsedBy);

        Assert.Equal("missing", plan.Single(p => p.Requested == "Puudub").Status);

        var spare = plan.Single(p => p.Requested == "Varuks");       // defined, used by nothing: still planned
        Assert.Equal("defined", spare.Status);
        Assert.Empty(spare.UsedBy);
        Assert.Equal(1, spare.Project!.Weight);

        var overridden = plan.Single(p => p.Requested == "EL_Kaabel");   // the project's style with one field changed
        Assert.Equal("defined", overridden.Status);
        Assert.Equal(6, overridden.Project!.Weight);
        Assert.Equal("#FF0000", overridden.Project.Color);
        Assert.Equal("Center", overridden.Project.Pattern);
    }

    // ── Tool surface ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("revit_list_line_styles", "ReadOnly")]
    [InlineData("revit_preview_set_line_styles", "ReadOnly")]
    [InlineData("revit_set_line_styles", "RequiresApproval")]
    public void The_tools_match_on_both_sides(string tool, string permission)
    {
        Assert.Equal("tags", BridgeToolGroups.GroupOf(tool));
        Assert.Equal(permission, ToolSourceScan.Addin.Value[tool].Permission);
        var bridge = Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == tool);
        Assert.Equal(permission == "ReadOnly", bridge.ReadOnly);
        Assert.Contains(tool, bridge.Targets);
        Assert.Contains("\"document\"", bridge.Source);
    }

    [Fact]
    public void Plans_count_setting_line_styles_as_creating()
        => Assert.Equal(PlanToolKind.Creates, PlanToolClassification.Classify("revit_set_line_styles"));
}
