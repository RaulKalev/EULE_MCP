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

    // ── Renaming ─────────────────────────────────────────────────────────────

    private static (List<LineStyleDefinition> Styles, List<string> Errors) ReadWithRename(string json)
    {
        var errors = new List<string>();
        var styles = DetailFamilySpecParser.ReadLineStyleDefinitions(
            new Dictionary<string, object?> { ["styles"] = JToken.Parse(json) }, "styles", errors, allowRename: true);
        return (styles, errors);
    }

    [Fact]
    public void NewName_is_read_only_when_renaming_is_allowed()
    {
        const string json = "[{\"name\":\"EL_Vana\",\"newName\":\"EL_Uus\",\"weight\":2}]";
        var (renamed, errors) = ReadWithRename(json);
        Assert.Empty(errors);
        Assert.Equal("EL_Uus", renamed[0].NewName);
        Assert.Equal(2, renamed[0].Weight);

        // revit_create_detail_family's lineStyles keep their behaviour: newName is not read there.
        var (plain, plainErrors) = Read(json);
        Assert.Empty(plainErrors);
        Assert.Null(plain[0].NewName);
    }

    [Fact]
    public void A_new_name_equal_to_the_name_is_no_rename_but_a_case_change_is()
    {
        var (styles, errors) = ReadWithRename("[{\"name\":\"A\",\"newName\":\"A\"},{\"name\":\"b\",\"newName\":\"B\"}]");
        Assert.Empty(errors);
        Assert.Null(styles[0].NewName);
        Assert.Equal("B", styles[1].NewName);
    }

    [Theory]
    [InlineData("[{\"name\":\"A\",\"newName\":\"X:Y\"}]", "characters Revit refuses")]
    [InlineData("[{\"name\":\"A\",\"newName\":\" \"}]", "newName is empty")]
    [InlineData("[{\"name\":\"A\",\"newName\":\"C\"},{\"name\":\"B\",\"newName\":\"c\"}]", "new name of another style")]
    [InlineData("[{\"name\":\"A\",\"newName\":\"B\"},{\"name\":\"B\",\"weight\":2}]", "also listed as a style name")]
    public void Refuses_bad_renames(string json, string expected)
    {
        var (_, errors) = ReadWithRename(json);
        Assert.Contains(errors, e => e.Contains(expected));
    }

    // ── Object style definitions ─────────────────────────────────────────────

    private static (List<ObjectStyleDefinition> Styles, List<string> Errors) ReadObject(string json)
    {
        var errors = new List<string>();
        var styles = DetailFamilySpecParser.ReadObjectStyleDefinitions(
            new Dictionary<string, object?> { ["styles"] = JToken.Parse(json) }, "styles", errors);
        return (styles, errors);
    }

    [Fact]
    public void Reads_object_styles()
    {
        var (styles, errors) = ReadObject(
            "[{\"category\":\"Walls\",\"projectionWeight\":2,\"cutWeight\":5,\"color\":\"#0000ff\",\"material\":\"Concrete\"}," +
            "{\"category\":\"Generic Annotations\",\"subcategory\":\"EL_Sümbol\",\"weight\":1,\"pattern\":\"solid\"}," +
            "{\"category\":\"Walls\",\"subcategory\":\"Uus\"}," +
            "{\"category\":\"Doors\",\"material\":\"none\"}]");

        Assert.Empty(errors);
        Assert.Equal(4, styles.Count);
        Assert.Null(styles[0].Subcategory);
        Assert.Equal(2, styles[0].ProjectionWeight);
        Assert.Equal(5, styles[0].CutWeight);
        Assert.Equal("#0000FF", styles[0].Color);
        Assert.Equal("Concrete", styles[0].Material);
        Assert.Equal("Generic Annotations / EL_Sümbol", styles[1].Label);
        Assert.Equal(1, styles[1].ProjectionWeight);           // "weight" is the projection weight
        Assert.Equal(string.Empty, styles[1].Pattern);         // solid
        Assert.False(styles[2].HasValues);                     // a subcategory to create with Revit's defaults
        Assert.Equal(string.Empty, styles[3].Material);        // none clears the material
    }

    [Theory]
    [InlineData("[{\"projectionWeight\":2}]", "needs a category")]
    [InlineData("[{\"category\":\"Walls\"}]", "nothing to change")]
    [InlineData("[{\"category\":\"Walls\",\"cutWeight\":0}]", "cutWeight must be a whole number from 1 to 16")]
    [InlineData("[{\"category\":\"Walls\",\"projectionWeight\":1.5}]", "projectionWeight must be a whole number")]
    [InlineData("[{\"category\":\"Walls\",\"color\":\"blue\"}]", "not a colour")]
    [InlineData("[{\"category\":\"Walls\",\"subcategory\":\"A|B\"}]", "subcategory name 'A|B' contains characters Revit refuses")]
    [InlineData("[{\"category\":\"Walls\",\"subcategory\":\"X\"},{\"category\":\"walls\",\"subcategory\":\"x\"}]", "listed twice")]
    public void Refuses_bad_object_styles(string json, string expected)
    {
        var (_, errors) = ReadObject(json);
        Assert.Contains(errors, e => e.Contains(expected));
    }

    [Fact]
    public void The_line_style_name_message_is_unchanged()
        => Assert.Equal("The line style name ' A' starts or ends with a space.", DetailFamilySpecParser.CheckLineStyleName(" A"));

    // ── Tool surface ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("revit_list_line_styles", "ReadOnly")]
    [InlineData("revit_preview_set_line_styles", "ReadOnly")]
    [InlineData("revit_set_line_styles", "RequiresApproval")]
    [InlineData("revit_list_object_styles", "ReadOnly")]
    [InlineData("revit_preview_set_object_styles", "ReadOnly")]
    [InlineData("revit_set_object_styles", "RequiresApproval")]
    [InlineData("revit_get_style_usage", "ReadOnly")]
    [InlineData("revit_preview_delete_styles", "ReadOnly")]
    [InlineData("revit_delete_styles", "DestructiveRequiresManualApproval")]
    public void The_tools_match_on_both_sides(string tool, string permission)
    {
        Assert.Equal("tags", BridgeToolGroups.GroupOf(tool));
        Assert.Equal(permission, ToolSourceScan.Addin.Value[tool].Permission);
        var bridge = Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == tool);
        Assert.Equal(permission == "ReadOnly", bridge.ReadOnly);
        Assert.Contains(tool, bridge.Targets);
        Assert.Contains("\"document\"", bridge.Source);
    }

    [Theory]
    [InlineData("revit_set_line_styles")]
    [InlineData("revit_set_object_styles")]
    [InlineData("revit_delete_styles")]
    public void Write_tools_build_their_own_approval_summary(string tool)
        => Assert.Contains("IApprovalSummaryTool", ToolSourceScan.Addin.Value[tool].Source);

    [Fact]
    public void Deleting_styles_is_classified_like_the_other_deletes()
    {
        // Destructive on the bridge, manual approval in the add-in (plans never cover it), and it re-reads its
        // targets by name when it runs, so its approval may wait for the user (docs/execution-safety.md).
        Assert.Contains("[McpServerTool(Name = \"revit_delete_styles\", Destructive = true)", ToolSourceScan.Bridge.Value.Single(b => b.Name == "revit_delete_styles").Source);
        Assert.Contains("IApprovalRevalidatesTargets", ToolSourceScan.Addin.Value["revit_delete_styles"].Source);
        Assert.Equal(PlanToolKind.Modifies, PlanToolClassification.Classify("revit_delete_styles"));
    }

    [Fact]
    public void Plans_count_setting_line_and_object_styles_as_creating_or_modifying()
    {
        Assert.Equal(PlanToolKind.Creates, PlanToolClassification.Classify("revit_set_line_styles"));
        // A subcategory the category does not have is created.
        Assert.Equal(PlanToolKind.Creates, PlanToolClassification.Classify("revit_set_object_styles"));
    }
}
