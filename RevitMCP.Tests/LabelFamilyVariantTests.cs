using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.Plans;
using RevitMCP.Bridge;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>revit_list_label_families and revit_(preview_)create_label_family: arguments and plans.</summary>
public class LabelFamilyVariantTests
{
    private static (LabelFamilyVariantSpec Spec, List<string> Errors) Parse(string json)
    {
        var errors = new List<string>();
        var args = JObject.Parse(json).Properties().ToDictionary(p => p.Name, p => (object?)p.Value);
        return (LabelFamilyVariantParser.Parse(args, errors), errors);
    }

    private const string Base = "\"seed\":\"EL_Silt\",\"savePath\":\"C:\\\\Families\",\"familyName\":\"EL_Silt 3.5\"";

    [Fact]
    public void Reads_a_full_variant()
    {
        var (spec, errors) = Parse("{" + Base + ",\"types\":[{\"name\":\"3.5 mm\",\"textSize\":3.5,\"font\":\"Arial\",\"bold\":true," +
                                   "\"italic\":\"no\",\"underline\":0,\"widthFactor\":0.8,\"color\":[255,0,0]},\"Teine\"]," +
                                   "\"renameParameter\":{\"from\":\"Tekst\",\"to\":\"Kilbi nimi\"},\"makeInstance\":true," +
                                   "\"sampleValue\":\"K1\",\"loadIntoProject\":true,\"ifExists\":\"Overwrite\",\"overwrite\":true}");

        Assert.Empty(errors);
        Assert.Equal(@"C:\Families\EL_Silt 3.5.rfa", spec.SavePath);
        Assert.Equal("EL_Silt", spec.SeedName);
        Assert.False(spec.SeedIsPath);
        Assert.Equal(new[] { "3.5 mm", "Teine" }, spec.Types.Select(t => t.Name));
        Assert.Equal(3.5, spec.Appearance.SizeMm);
        Assert.Equal("Arial", spec.Appearance.Font);
        Assert.True(spec.Appearance.Bold);
        Assert.False(spec.Appearance.Italic);
        Assert.False(spec.Appearance.Underline);
        Assert.Equal(0.8, spec.Appearance.WidthFactor);
        Assert.Equal("#FF0000", spec.Appearance.Color);
        Assert.Equal("Tekst", spec.Parameter);
        Assert.Equal("Kilbi nimi", spec.RenameTo);
        Assert.True(spec.MakeInstance);
        Assert.Equal("K1", spec.SampleValue);
        Assert.True(spec.LoadIntoProject);
        Assert.Equal("overwrite", spec.IfExists);
        Assert.True(spec.ChangesParameter);
    }

    [Fact]
    public void A_plain_copy_under_a_new_name_needs_nothing_else()
    {
        var (spec, errors) = Parse("{\"seed\":\"C:\\\\Seeds\\\\Silt.rfa\",\"savePath\":\"C:\\\\Families\\\\Silt 2.rfa\"}");
        Assert.Empty(errors);
        Assert.True(spec.SeedIsPath);
        Assert.Equal("Silt", spec.SeedName);
        Assert.Equal("Silt 2", spec.FamilyName);
        Assert.False(spec.ChangesParameter);
        Assert.False(spec.Appearance.HasValues);
    }

    [Theory]
    [InlineData("{\"savePath\":\"C:\\\\F\",\"familyName\":\"A\"}", "seed is required")]
    [InlineData("{\"seed\":\"Silt.rfa\",\"savePath\":\"C:\\\\F\",\"familyName\":\"A\"}", "absolute .rfa path")]
    [InlineData("{\"seed\":\"EL_Silt\",\"familyName\":\"A\"}", "savePath is required")]
    [InlineData("{\"seed\":\"EL_Silt\",\"savePath\":\"C:\\\\F\",\"familyName\":\"el_silt\"}", "name of its own")]
    [InlineData("{\"seed\":\"C:\\\\F\\\\A.rfa\",\"savePath\":\"C:\\\\F\\\\A.rfa\"}", "seed's own file")]
    [InlineData("{" + Base + ",\"ifExists\":\"merge\"}", "skip | overwrite")]
    [InlineData("{" + Base + ",\"types\":[{\"textSize\":2}]}", "needs a name")]
    [InlineData("{" + Base + ",\"types\":[\"A\",\"a\"]}", "listed twice")]
    [InlineData("{" + Base + ",\"types\":[{\"name\":\"A:B\"}]}", "characters Revit refuses")]
    [InlineData("{" + Base + ",\"types\":[{\"name\":\"A\",\"textSize\":0}]}", "textSize")]
    [InlineData("{" + Base + ",\"types\":[{\"name\":\"A\",\"widthFactor\":20}]}", "widthFactor")]
    [InlineData("{" + Base + ",\"types\":[{\"name\":\"A\",\"color\":\"red\"}]}", "not a colour")]
    [InlineData("{" + Base + ",\"types\":[{\"name\":\"A\",\"bold\":\"maybe\"}]}", "bold must be true or false")]
    [InlineData("{" + Base + ",\"renameParameter\":{\"from\":\"A\"}}", "needs 'to'")]
    [InlineData("{" + Base + ",\"parameter\":\"A\",\"renameParameter\":{\"from\":\"B\",\"to\":\"C\"}}", "different parameters")]
    [InlineData("{" + Base + ",\"makeInstance\":\"sometimes\"}", "makeInstance")]
    public void Refuses_bad_arguments(string json, string expected)
    {
        var (_, errors) = Parse(json);
        Assert.Contains(errors, e => e.Contains(expected));
    }

    [Fact]
    public void Types_that_disagree_on_the_text_are_refused_because_the_label_has_one_text_type()
    {
        var (_, errors) = Parse("{" + Base + ",\"types\":[{\"name\":\"A\",\"textSize\":2.5},{\"name\":\"B\",\"textSize\":3.5,\"font\":\"Arial\"},{\"name\":\"C\",\"font\":\"Arial\"}]}");
        var error = Assert.Single(errors);
        Assert.Contains("different textSize", error);
        Assert.Contains("one variant per text appearance", error);
    }

    [Fact]
    public void A_field_given_in_one_type_applies_to_all()
    {
        var (spec, errors) = Parse("{" + Base + ",\"types\":[{\"name\":\"A\",\"textSize\":2.5},{\"name\":\"B\",\"textSize\":2.5,\"italic\":true}]}");
        Assert.Empty(errors);
        Assert.Equal(2.5, spec.Appearance.SizeMm);
        Assert.True(spec.Appearance.Italic);
        Assert.Equal("size 2.5 mm, italic", spec.Appearance.Describe());
    }

    private static readonly List<LabelFamilyParameterInfo> SeedParameters = new()
    {
        new() { Name = "Tekst", DataType = "text", IsInstance = true },
        new() { Name = "Kogus", DataType = "integer", Formula = "1 + 1" },
        new() { Name = "Jagatud", DataType = "text", IsShared = true }
    };

    [Fact]
    public void The_parameter_is_the_named_one_or_the_only_editable_one()
    {
        var errors = new List<string>();
        var (named, _) = Parse("{" + Base + ",\"renameParameter\":{\"from\":\"tekst\",\"to\":\"Nimi\"}}");
        Assert.Equal("Tekst", LabelFamilyVariantParser.ResolveParameter(named, SeedParameters, errors)!.Name);

        var (implicitOne, _) = Parse("{" + Base + ",\"sampleValue\":\"X\"}");
        var two = SeedParameters.Take(2).ToList();   // Kogus has a formula, so Tekst is the only editable one
        Assert.Equal("Tekst", LabelFamilyVariantParser.ResolveParameter(implicitOne, two, errors)!.Name);
        Assert.Empty(errors);

        // Nothing changes the parameter: none is needed.
        var (copy, _) = Parse("{" + Base + "}");
        Assert.Null(LabelFamilyVariantParser.ResolveParameter(copy, SeedParameters, errors));
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("\"parameter\":\"Teksti\",\"sampleValue\":\"X\"", "did you mean 'Tekst'")]
    [InlineData("\"sampleValue\":\"X\"", "several parameters")]
    [InlineData("\"renameParameter\":{\"from\":\"Jagatud\",\"to\":\"Uus\"}", "does not rename shared parameters")]
    [InlineData("\"renameParameter\":{\"from\":\"Tekst\",\"to\":\"kogus\"}", "must be a free name")]
    [InlineData("\"parameter\":\"Kogus\",\"sampleValue\":\"3\"", "set by the formula")]
    public void Refuses_parameter_changes_the_seed_cannot_take(string extra, string expected)
    {
        var (spec, parseErrors) = Parse("{" + Base + "," + extra + "}");
        Assert.Empty(parseErrors);
        var errors = new List<string>();
        LabelFamilyVariantParser.ResolveParameter(spec, SeedParameters, errors);
        Assert.Contains(errors, e => e.Contains(expected));
    }

    [Fact]
    public void Plans_types_keep_rename_create_and_delete()
    {
        var requested = new[] { "2.5 mm", "3.5 MM", "5 mm" }.Select(n => new LabelFamilyTypeSpec { Name = n }).ToList();
        var plan = LabelFamilyVariantParser.PlanTypes(new[] { "2.5 mm", "3.5 mm", "Vana" }, requested);

        Assert.Equal(new[] { "2.5 mm:keep", "3.5 MM:rename", "5 mm:create", "Vana:delete" }, plan.Select(p => $"{p.Name}:{p.Action}"));
        Assert.Equal("3.5 mm", plan[1].SeedName);

        var unchanged = LabelFamilyVariantParser.PlanTypes(new[] { "A", "B" }, new List<LabelFamilyTypeSpec>());
        Assert.All(unchanged, p => Assert.Equal("keep", p.Action));
    }

    [Fact]
    public void Lists_named_families_and_caps_how_many_are_opened()
    {
        var loaded = new[] { "Silt A", "Silt B", "Silt C", "Nool" };
        var errors = new List<string>();
        var (listed, inspect) = LabelFamilyVariantParser.PlanListing(loaded, Array.Empty<string>(), 2, true, errors);
        Assert.Equal(new[] { "Nool", "Silt A", "Silt B", "Silt C" }, listed);
        Assert.Equal(new[] { "Nool", "Silt A" }, inspect);

        (listed, inspect) = LabelFamilyVariantParser.PlanListing(loaded, new[] { "silt c", "Silt" }, 0, true, errors);
        Assert.Equal(new[] { "Silt C" }, listed);
        Assert.Equal(new[] { "Silt C" }, inspect);
        Assert.Contains(errors, e => e.Contains("'Silt' is not") && e.Contains("did you mean"));

        (_, inspect) = LabelFamilyVariantParser.PlanListing(loaded, Array.Empty<string>(), 100, false, errors);
        Assert.Empty(inspect);
        (_, inspect) = LabelFamilyVariantParser.PlanListing(Enumerable.Range(0, 40).Select(i => $"F{i:00}").ToList(), Array.Empty<string>(), 100, true, errors);
        Assert.Equal(LabelFamilyVariantParser.MaxInspectLimit, inspect.Count);
    }

    [Theory]
    [InlineData("#000000", 0)]
    [InlineData("#FF0000", 255)]
    [InlineData("#00FF00", 65280)]
    [InlineData("#0000FF", 16711680)]
    [InlineData("#123456", 0x12 + (0x34 << 8) + (0x56 << 16))]
    public void Converts_colours_to_revit_integers_and_back(string hex, int value)
    {
        Assert.Equal(value, LabelFamilyVariantParser.ColorToRevitInt(hex));
        Assert.Equal(hex, LabelFamilyVariantParser.RevitIntToColor(value));
    }

    // ── Tool surface ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("revit_list_label_families", "ReadOnly")]
    [InlineData("revit_preview_create_label_family", "ReadOnly")]
    [InlineData("revit_create_label_family", "RequiresApproval")]
    public void The_tools_match_on_both_sides(string tool, string permission)
    {
        Assert.Equal("edit", BridgeToolGroups.GroupOf(tool));
        Assert.Equal(permission, ToolSourceScan.Addin.Value[tool].Permission);
        var bridge = Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == tool);
        Assert.Equal(permission == "ReadOnly", bridge.ReadOnly);
        Assert.Contains(tool, bridge.Targets);
        Assert.Contains("document", bridge.Source);
    }

    [Fact]
    public void The_write_tool_has_an_approval_summary()
        => Assert.Contains("IApprovalSummaryTool", ToolSourceScan.Addin.Value["revit_create_label_family"].Source);

    [Fact]
    public void Plans_count_creating_a_label_family_as_creating()
        => Assert.Equal(PlanToolKind.Creates, PlanToolClassification.Classify("revit_create_label_family"));
}
