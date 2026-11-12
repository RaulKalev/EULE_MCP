using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Plans;
using RevitMCP.Addin.ViewFilters;
using RevitMCP.Addin.ViewTemplates;
using RevitMCP.Bridge;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>View templates: revit_get_view_template, revit_create_view_templates, revit_set_view_templates.</summary>
public class ViewTemplateTests
{
    private static (List<ViewTemplateEdit> Edits, List<string> Errors) Edits(string json)
    {
        var errors = new List<string>();
        var edits = ViewTemplateRequestParser.ReadEdits(new Dictionary<string, object?> { ["templates"] = JToken.Parse(json) }, "templates", errors);
        return (edits, errors);
    }

    private static (List<ViewTemplateCreateSpec> Specs, List<string> Errors) Creates(string json)
    {
        var errors = new List<string>();
        var specs = ViewTemplateRequestParser.ReadCreates(new Dictionary<string, object?> { ["templates"] = JToken.Parse(json) }, "templates", errors);
        return (specs, errors);
    }

    // Template parameter ids are negative BuiltInParameter values in Revit; the numbers here are only test ids.
    private static ViewTemplateState State() => new()
    {
        Id = 100,
        Name = "EN Plaan",
        ViewType = "FloorPlan",
        Scale = 100,
        DetailLevel = "Medium",
        Discipline = "Electrical",
        DisplayStyle = "HLR",
        CanSetScale = true,
        CanSetDetailLevel = true,
        CanSetDiscipline = true,
        CanSetDisplayStyle = true,
        TemplateParameters =
        {
            new NamedRef(-1, "View Scale", "VIEW_SCALE_PULLDOWN_METRIC"),
            new NamedRef(-2, "Detail Level", "VIEW_DETAIL_LEVEL"),
            new NamedRef(-3, "Discipline", "VIEW_DISCIPLINE"),
            new NamedRef(-4, "V/G Overrides Model", "VIS_GRAPHICS_MODEL"),
            new NamedRef(-5, "Phase", "VIEW_PHASE"),
            new NamedRef(-6, "V/G Overrides Filters", "VIS_GRAPHICS_FILTERS")
        },
        NonControlled = { -2, -5 },
        Categories =
        {
            new ViewTemplateCategory { Id = 10, Name = "Doors", BuiltInName = "OST_Doors", Kind = "Model", CanHide = true },
            new ViewTemplateCategory { Id = 11, Name = "Furniture", BuiltInName = "OST_Furniture", Kind = "Model", CanHide = true, Hidden = true },
            new ViewTemplateCategory { Id = 12, Name = "Doors: Frame/Mullion", Kind = "Model", CanHide = true },
            new ViewTemplateCategory { Id = 13, Name = "Grids", BuiltInName = "OST_Grids", Kind = "Annotation", CanHide = false }
        },
        ParameterNames = { "Phase", "Phase Filter", "View Scale", "Detail Level", "Title on Sheet" },
        ViewCount = 12
    };

    [Fact]
    public void Reads_every_edit_field()
    {
        var (edits, errors) = Edits(
            "[{\"template\":\"EN Plaan\",\"newName\":\"EN Plaan 2\",\"scale\":\"1:50\",\"detailLevel\":\"fine\",\"discipline\":\"electrical\"," +
            "\"displayStyle\":\"Hidden Line\",\"include\":[\"Detail Level\"],\"exclude\":\"Phase\",\"hideCategories\":[\"Doors\",\"OST_Furniture\"]," +
            "\"showCategories\":[\"Grids\"],\"parameters\":{\"Phase\":\"New Construction\",\"Title on Sheet\":null,\"Some flag\":true,\"Num\":2.5}}]");

        Assert.Empty(errors);
        var edit = Assert.Single(edits);
        Assert.Equal("EN Plaan 2", edit.NewName);
        Assert.Equal(50, edit.Scale);
        Assert.Equal("Fine", edit.DetailLevel);
        Assert.Equal("Electrical", edit.Discipline);
        Assert.Equal("HLR", edit.DisplayStyle);
        Assert.Equal(new[] { "Detail Level" }, edit.Include);
        Assert.Equal(new[] { "Phase" }, edit.Exclude);          // one string is a list of one
        Assert.Equal(2, edit.HideCategories.Count);
        Assert.Equal(new[] { "Phase", "Title on Sheet", "Some flag", "Num" }, edit.Parameters.Select(p => p.Key));
        Assert.Equal(new[] { "New Construction", "", "1", "2.5" }, edit.Parameters.Select(p => p.Value));
    }

    [Theory]
    [InlineData(50, 50)]
    [InlineData("1:100", 100)]
    [InlineData("1 : 20", 20)]
    [InlineData("200", 200)]
    public void Reads_scales(object value, int expected)
        => Assert.Equal(expected, ViewTemplateRequestParser.ParseScale(JToken.FromObject(value)));

    [Theory]
    [InlineData("2:100")]
    [InlineData("0")]
    [InlineData("1:0.5")]
    [InlineData("big")]
    [InlineData("30000")]
    public void Refuses_bad_scales(string value) => Assert.Null(ViewTemplateRequestParser.ParseScale(JToken.FromObject(value)));

    [Theory]
    [InlineData("shaded", "Shading")]
    [InlineData("Shaded with edges", "ShadingWithEdges")]
    [InlineData("Consistent Colors", "FlatColors")]
    [InlineData("realistic", "Realistic")]
    [InlineData("WIREFRAME", "Wireframe")]
    public void Reads_display_styles_by_their_dialog_names(string text, string expected)
        => Assert.Equal(expected, ViewTemplateRequestParser.NormalizeDisplayStyle(text));

    [Theory]
    [InlineData("[{\"scale\":50}]", "needs template")]
    [InlineData("[{\"template\":\"A\"}]", "nothing to change")]
    [InlineData("[{\"template\":\"A\",\"scale\":50},{\"template\":\"a\",\"scale\":20}]", "listed twice")]
    [InlineData("[{\"template\":\"A\",\"scale\":\"1:x\"}]", "must be a scale")]
    [InlineData("[{\"template\":\"A\",\"detailLevel\":\"Extra\"}]", "Coarse, Medium, Fine")]
    [InlineData("[{\"template\":\"A\",\"discipline\":\"Fire\"}]", "is not one of")]
    [InlineData("[{\"template\":\"A\",\"displayStyle\":\"Sketchy\"}]", "is not one of")]
    [InlineData("[{\"template\":\"A\",\"include\":[\"Phase\"],\"exclude\":[\"phase\"]}]", "both include and exclude")]
    [InlineData("[{\"template\":\"A\",\"hideCategories\":[\"Doors\"],\"showCategories\":[\"DOORS\"]}]", "both hideCategories and showCategories")]
    [InlineData("[{\"template\":\"A\",\"newName\":\"B:C\"}]", "characters Revit refuses")]
    [InlineData("[{\"template\":\"A\",\"parameters\":[1]}]", "must be an object")]
    [InlineData("[{\"template\":\"A\",\"parameters\":{\"X\":{\"a\":1}}}]", "text, a number")]
    public void Refuses_bad_edits(string json, string expected)
    {
        var (edits, errors) = Edits(json);
        Assert.Contains(errors, e => e.Contains(expected));
        Assert.True(edits.Count <= 1);   // a repeated template keeps its first item only
    }

    [Theory]
    [InlineData("[{\"fromView\":\"V\"}]", "needs a name")]
    [InlineData("[{\"name\":\"T\"}]", "needs fromView")]
    [InlineData("[{\"name\":\"T\",\"fromView\":\"V\",\"duplicateFrom\":\"U\"}]", "not both")]
    [InlineData("[{\"name\":\"T\",\"fromView\":\"V\"},{\"name\":\"t\",\"fromView\":\"W\"}]", "given twice")]
    [InlineData("[{\"name\":\"T\",\"fromView\":\"V\",\"newName\":\"X\"}]", "does not apply")]
    [InlineData("[{\"name\":\"EN 1:50\",\"fromView\":\"V\"}]", "characters Revit refuses")]
    public void Refuses_bad_creates(string json, string expected)
    {
        var (_, errors) = Creates(json);
        Assert.Contains(errors, e => e.Contains(expected));
    }

    [Fact]
    public void Reads_creates_with_edits()
    {
        var (specs, errors) = Creates("[{\"name\":\"EN 1-50\",\"duplicateFrom\":\"EN Plaan\",\"scale\":50},{\"name\":\"Uus\",\"fromView\":12345}]");
        Assert.Empty(errors);
        Assert.Equal("duplicate", specs[0].Mode);
        Assert.Equal(50, specs[0].Edit.Scale);
        Assert.Equal("EN 1-50", specs[0].Edit.Template);
        Assert.Equal("fromView", specs[1].Mode);
        Assert.Equal("12345", specs[1].FromView);
        Assert.False(specs[1].Edit.HasChanges);
    }

    [Fact]
    public void Plans_from_and_to_per_field()
    {
        var (edits, _) = Edits(
            "[{\"template\":\"EN Plaan\",\"newName\":\"EN Plaan uus\",\"scale\":50,\"detailLevel\":\"Fine\",\"discipline\":\"Electrical\"," +
            "\"include\":[\"Detail Level\"],\"exclude\":[\"V/G Overrides Model\"],\"hideCategories\":[\"OST_Doors\"],\"showCategories\":[\"Furniture\"]}]");
        var plan = ViewTemplatePlanner.Plan(edits[0], State(), new[] { "Other" });

        Assert.Empty(plan.Problems);
        Assert.Equal("edit", plan.Action);
        Assert.Equal("EN Plaan uus", plan.NewName);
        Assert.Equal(50, plan.Scale);
        Assert.Equal("Fine", plan.DetailLevel);
        Assert.Null(plan.Discipline);                                   // already Electrical: no change
        Assert.Equal(new long[] { -5, -4 }, plan.NonControlled);        // Detail Level now controlled, V/G Model not
        Assert.Equal(new long[] { 10 }, plan.Hide);
        Assert.Equal(new long[] { 11 }, plan.Show);

        Assert.Contains(plan.Changes, c => c.Field == "scale" && c.From == "1:100" && c.To == "1:50");
        Assert.Contains(plan.Changes, c => c.Field == "detailLevel" && c.From == "Medium" && c.To == "Fine");
        Assert.Contains(plan.Changes, c => c.Field == "controls" && c.Name == "Detail Level" && c.From == "no" && c.To == "yes");
        Assert.Contains(plan.Changes, c => c.Field == "controls" && c.Name == "V/G Overrides Model" && c.To == "no");
        Assert.Contains(plan.Changes, c => c.Field == "category" && c.Name == "Doors" && c.To == "hidden");
        Assert.DoesNotContain(plan.Changes, c => c.Field == "discipline");

        // Hiding a model category while V/G Overrides Model is excluded does not reach the views.
        Assert.Contains(plan.Warnings, w => w.Contains("model categories") && w.Contains("V/G Overrides Model"));
    }

    [Fact]
    public void An_edit_that_matches_the_template_is_unchanged()
    {
        var (edits, _) = Edits("[{\"template\":\"EN Plaan\",\"scale\":100,\"include\":[\"View Scale\"],\"showCategories\":[\"Doors\"]}]");
        var plan = ViewTemplatePlanner.Plan(edits[0], State(), Array.Empty<string>());
        Assert.Empty(plan.Problems);
        Assert.Equal("unchanged", plan.Action);
        Assert.Null(plan.NonControlled);
    }

    [Fact]
    public void Warns_when_the_template_does_not_control_what_is_set()
    {
        var (edits, _) = Edits("[{\"template\":\"EN Plaan\",\"detailLevel\":\"Coarse\"}]");
        var plan = ViewTemplatePlanner.Plan(edits[0], State(), Array.Empty<string>());
        Assert.Contains(plan.Warnings, w => w.Contains("detail level") && w.Contains("add 'Detail Level' to include"));

        // Including it in the same edit removes the warning.
        (edits, _) = Edits("[{\"template\":\"EN Plaan\",\"detailLevel\":\"Coarse\",\"include\":[\"VIEW_DETAIL_LEVEL\"]}]");
        plan = ViewTemplatePlanner.Plan(edits[0], State(), Array.Empty<string>());
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Reports_unknown_names_with_close_suggestions()
    {
        var (edits, _) = Edits("[{\"template\":\"EN Plaan\",\"include\":[\"Detail Levl\"],\"hideCategories\":[\"Door\"],\"parameters\":{\"Phase Filtr\":\"x\"}}]");
        var plan = ViewTemplatePlanner.Plan(edits[0], State(), Array.Empty<string>());
        Assert.Equal("invalid", plan.Action);
        Assert.Contains(plan.Problems, p => p.Contains("'Detail Levl' is not a template parameter") && p.Contains("'Detail Level'"));
        Assert.Contains(plan.Problems, p => p.Contains("'Door' is not a category") && p.Contains("'Doors'"));
        Assert.Contains(plan.Problems, p => p.Contains("no parameter 'Phase Filtr'") && p.Contains("'Phase Filter'"));
    }

    [Fact]
    public void Refuses_what_the_template_cannot_take()
    {
        var state = State();
        state.CanSetDisplayStyle = false;
        state.Parameters["Title on Sheet"] = new ViewTemplateParameterValue { Id = 50, Name = "Title on Sheet", ReadOnly = true };
        var (edits, _) = Edits(
            "[{\"template\":\"EN Plaan\",\"newName\":\"Other\",\"displayStyle\":\"Shaded\",\"hideCategories\":[\"Grids\"]," +
            "\"include\":[\"View Scale\",\"VIEW_SCALE_PULLDOWN_METRIC\"],\"parameters\":{\"Title on Sheet\":\"x\"}}]");
        var plan = ViewTemplatePlanner.Plan(edits[0], state, new[] { "other" });
        Assert.Contains(plan.Problems, p => p.Contains("already named 'Other'"));
        Assert.Contains(plan.Problems, p => p.Contains("display style") && p.Contains("cannot be set"));
        Assert.Contains(plan.Problems, p => p.Contains("'Grids' cannot be hidden"));
        Assert.Contains(plan.Problems, p => p.Contains("same template parameter"));
        Assert.Contains(plan.Problems, p => p.Contains("read-only"));
    }

    [Fact]
    public void Plans_parameter_values_and_warns_about_uncontrolled_ones()
    {
        var state = State();
        state.Parameters["phase"] = new ViewTemplateParameterValue { Id = -5, Name = "Phase", Value = "Existing" };
        state.Parameters["Title on Sheet"] = new ViewTemplateParameterValue { Id = 50, Name = "Title on Sheet", Value = "x" };
        var (edits, _) = Edits("[{\"template\":\"EN Plaan\",\"parameters\":{\"phase\":\"New Construction\",\"Title on Sheet\":\"X\"}}]");
        var plan = ViewTemplatePlanner.Plan(edits[0], state, Array.Empty<string>());

        Assert.Empty(plan.Problems);
        var change = Assert.Single(plan.Changes);                       // "X" equals "x": no change
        Assert.Equal("parameter", change.Field);
        Assert.Equal("Phase", change.Name);                             // the template's own spelling
        Assert.Equal("Existing", change.From);
        Assert.Equal("New Construction", change.To);
        Assert.Contains(plan.Warnings, w => w.Contains("does not control 'Phase'"));
    }

    [Fact]
    public void A_new_name_that_is_taken_is_a_problem()
    {
        var (specs, _) = Creates("[{\"name\":\"en plaan\",\"fromView\":\"V\"}]");
        Assert.Contains("already exists", ViewTemplatePlanner.CheckNewName(specs[0], new[] { "EN Plaan" }));
        Assert.Null(ViewTemplatePlanner.CheckNewName(specs[0], new[] { "Muu" }));
    }

    [Fact]
    public void The_approval_text_says_how_many_views_change()
    {
        var (edits, _) = Edits("[{\"template\":\"EN Plaan\",\"scale\":50},{\"template\":\"123\",\"hideCategories\":[\"Doors\"]}]");
        var text = ViewTemplateApprovalText.ForSet(edits, 0, "Projekt",
            new Dictionary<string, (string Name, int Views)> { ["EN Plaan"] = ("EN Plaan", 12), ["123"] = ("EN Lagi", 3) });
        Assert.Contains("'EN Plaan', used by 12 view(s): scale 1:50", text);
        Assert.Contains("'EN Lagi', used by 3 view(s): hides 1 category", text);
        Assert.Contains("changes every view that uses it (15 view(s) in all)", text);
        Assert.Contains("'Projekt'", text);

        var (specs, _) = Creates("[{\"name\":\"Uus\",\"fromView\":\"EN 1. korrus\",\"detailLevel\":\"Fine\"}]");
        var create = ViewTemplateApprovalText.ForCreate(specs, 1, "");
        Assert.Contains("'Uus' (from view 'EN 1. korrus', then detail level Fine)", create);
        Assert.Contains("the active document", create);
        Assert.Contains("1 invalid item", create);
    }

    // ── Tool surface ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("revit_get_view_template", "ReadOnly")]
    [InlineData("revit_preview_create_view_templates", "ReadOnly")]
    [InlineData("revit_create_view_templates", "RequiresApproval")]
    [InlineData("revit_preview_set_view_templates", "ReadOnly")]
    [InlineData("revit_set_view_templates", "RequiresApproval")]
    public void The_tools_match_on_both_sides(string tool, string permission)
    {
        Assert.Equal("views", BridgeToolGroups.GroupOf(tool));
        Assert.Equal(permission, ToolSourceScan.Addin.Value[tool].Permission);
        var bridge = Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == tool);
        Assert.Equal(permission == "ReadOnly", bridge.ReadOnly);
        Assert.Contains(tool, bridge.Targets);
        Assert.Contains("\"document\"", bridge.Source);
    }

    [Fact]
    public void Plans_classify_the_writes()
    {
        Assert.Equal(PlanToolKind.Creates, PlanToolClassification.Classify("revit_create_view_templates"));
        Assert.Equal(PlanToolKind.Modifies, PlanToolClassification.Classify("revit_set_view_templates"));
    }
}
