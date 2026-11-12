using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.Plans;
using RevitMCP.Bridge;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>Several types per detail family, and revit_edit_detail_family's arguments and checks.</summary>
public class DetailFamilyEditTests
{
    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] values)
    {
        var args = new Dictionary<string, object?>();
        foreach (var (key, value) in values) args[key] = value is string s && (s.StartsWith("[") || s.StartsWith("{")) ? JToken.Parse(s) : value;
        return args;
    }

    private static (DetailFamilySpec Spec, List<string> Errors) Create(params (string Key, object? Value)[] values)
    {
        var args = Args(values);
        args["savePath"] = @"C:\Families\Detail";
        args["familyName"] = "EL_Kilp";
        if (!args.ContainsKey("lines")) args["lines"] = JToken.Parse("[{\"start\":[0,0],\"end\":[10,0]}]");
        var errors = new List<string>();
        return (DetailFamilySpecParser.Parse(args, errors), errors);
    }

    private static (DetailFamilyEditSpec Edit, List<string> Errors) Edit(params (string Key, object? Value)[] values)
    {
        var errors = new List<string>();
        return (DetailFamilySpecParser.ParseEdit(Args(values), errors), errors);
    }

    private const string Parameters =
        "[{\"name\":\"Laius\",\"dataType\":\"length\",\"value\":20}," +
        "{\"name\":\"Tähis\",\"dataType\":\"text\",\"instance\":true}," +
        "{\"name\":\"Pool\",\"dataType\":\"length\",\"formula\":\"Laius / 2\"}," +
        "{\"name\":\"Näita\",\"dataType\":\"yesNo\"}," +
        "{\"name\":\"EULE_Tähis\",\"shared\":true}]";

    // ── Types ────────────────────────────────────────────────────────────────

    [Fact]
    public void Types_are_read_with_their_values()
    {
        var (spec, errors) = Create(("parameters", Parameters),
            ("types", "[{\"name\":\"600\",\"values\":{\"Laius\":600,\"Tähis\":\"K6\",\"Näita\":true,\"EULE_Tähis\":\"x\"}},{\"name\":\"Tühi\"}]"));

        Assert.Empty(errors);
        Assert.Equal(new[] { "600", "Tühi" }, spec.Types.Select(t => t.Name));
        Assert.Equal(4, spec.Types[0].Values.Count);
        Assert.Equal("types[0].values['Laius']", spec.Types[0].Values[0].Source);
        Assert.Empty(spec.Types[1].Values);
    }

    [Fact]
    public void Without_types_nothing_changes()
    {
        var (spec, errors) = Create(("parameters", Parameters));
        Assert.Empty(errors);
        Assert.Empty(spec.Types);
    }

    [Theory]
    [InlineData("[{\"values\":{}}]", "needs a name")]
    [InlineData("[{\"name\":\"A\"},{\"name\":\"a\"}]", "listed twice")]
    [InlineData("[{\"name\":\"A:B\"}]", "type name")]
    [InlineData("[{\"name\":\"A\",\"values\":[1,2]}]", "must be an object")]
    [InlineData("[{\"name\":\"A\",\"values\":{\"Laius\":null}}]", "is null")]
    [InlineData("[{\"name\":\"A\",\"values\":{\"Laiu\":5}}]", "did you mean 'Laius'")]
    [InlineData("[{\"name\":\"A\",\"values\":{\"Pool\":5}}]", "has a formula")]
    [InlineData("[{\"name\":\"A\",\"values\":{\"Laius\":\"lai\"}}]", "must be a number (mm)")]
    [InlineData("[{\"name\":\"A\",\"values\":{\"Näita\":\"maybe\"}}]", "true or false")]
    public void Bad_types_are_refused(string types, string expected)
    {
        var (_, errors) = Create(("parameters", Parameters), ("types", types));
        Assert.Contains(errors, e => e.Contains(expected));
    }

    [Fact]
    public void An_instance_parameter_takes_a_per_type_default_and_a_shared_one_is_left_to_revit()
    {
        var (_, errors) = Create(("parameters", Parameters), ("types", "[{\"name\":\"A\",\"values\":{\"Tähis\":\"K1\",\"EULE_Tähis\":42}}]"));
        Assert.Empty(errors);
    }

    // ── Edit arguments ───────────────────────────────────────────────────────

    [Fact]
    public void An_edit_needs_one_target_and_something_to_change()
    {
        Assert.Contains(Edit(("lines", "[{\"start\":[0,0],\"end\":[10,0]}]")).Errors, e => e.StartsWith("Nothing to edit"));
        Assert.Contains(Edit(("family", "A"), ("path", @"C:\F\A.rfa"), ("overwrite", true), ("lines", "[{\"start\":[0,0],\"end\":[10,0]}]")).Errors,
            e => e.Contains("not both"));
        Assert.Contains(Edit(("family", "A")).Errors, e => e.StartsWith("Nothing to change"));
        Assert.Contains(Edit(("path", @"C:\F\A.txt"), ("overwrite", true), ("remove", "{\"curves\":true}")).Errors, e => e.Contains("not a family file"));
        Assert.Contains(Edit(("path", @"F\A.rfa"), ("overwrite", true), ("remove", "{\"curves\":true}")).Errors, e => e.Contains("absolute"));
        Assert.Contains(Edit(("path", @"C:\F\A.rfa"), ("remove", "{\"curves\":true}")).Errors, e => e.Contains("overwrite=true"));
        Assert.Contains(Edit(("path", @"C:\F\A.rfa"), ("overwrite", true), ("overwriteParameterValues", true), ("remove", "{\"curves\":true}")).Errors,
            e => e.Contains("overwriteParameterValues applies"));

        var (edit, errors) = Edit(("family", "EL_Kilp"), ("remove", "{\"textNotes\":true}"));
        Assert.Empty(errors);
        Assert.False(edit.IsPathTarget);
        Assert.False(edit.Content.Purge);          // an edit does not purge unless asked
        Assert.True(Edit(("family", "EL_Kilp"), ("purge", true), ("remove", "{\"textNotes\":true}")).Edit.Content.Purge);
    }

    [Fact]
    public void An_edit_reads_the_same_additions_as_create()
    {
        var (edit, errors) = Edit(("family", "EL_Kilp"),
            ("lines", "[{\"start\":[0,0],\"end\":[10,0],\"lineStyle\":\"Oma\"}]"),
            ("lineStyles", "[{\"name\":\"Oma\",\"weight\":3}]"),
            ("filledRegions", "[{\"min\":[0,0],\"max\":[5,5]}]"),
            ("parameters", "[{\"name\":\"Uus\",\"dataType\":\"text\"}]"),
            ("labels", "[{\"text\":\"K1\",\"position\":[0,9]}]"),
            ("types", "[{\"name\":\"T1\",\"values\":{\"Uus\":\"a\"}}]"));
        Assert.Empty(errors);
        Assert.True(edit.AddsAnything);
        Assert.Single(edit.Content.Curves);
        Assert.Single(edit.Content.LineStyles);
        Assert.Single(edit.Content.FilledRegions);
        Assert.Single(edit.Content.Labels);
        Assert.Single(edit.Content.Types);
    }

    [Fact]
    public void Remove_takes_true_or_ids_and_names()
    {
        var (edit, errors) = Edit(("family", "A"),
            ("remove", "{\"curves\":true,\"filledRegions\":[12,13,12],\"labels\":false,\"parameters\":[\"Laius\",\"Tähis\"]}"));
        Assert.Empty(errors);
        Assert.True(edit.Remove.Curves.All);
        Assert.Equal(new long[] { 12, 13 }, edit.Remove.FilledRegions.Ids);
        Assert.False(edit.Remove.Labels.Any);
        Assert.Equal(new[] { "Laius", "Tähis" }, edit.Remove.Parameters);
    }

    [Theory]
    [InlineData("[1,2]", "must be an object")]
    [InlineData("{\"curve\":true}", "did you mean curves")]
    [InlineData("{\"curves\":\"all\"}", "true (all of them) or an array")]
    [InlineData("{\"curves\":[1.5]}", "element id")]
    [InlineData("{\"curves\":[-3]}", "element id")]
    [InlineData("{\"parameters\":true}", "there is no 'all'")]
    [InlineData("{\"parameters\":[\"A\",\"a\"]}", "listed twice")]
    [InlineData("{\"parameters\":[3]}", "must be a parameter name")]
    public void Bad_removes_are_refused(string remove, string expected)
    {
        var (_, errors) = Edit(("family", "A"), ("remove", remove));
        Assert.Contains(errors, e => e.Contains(expected));
    }

    [Fact]
    public void A_loaded_family_keeps_its_name_when_saved_and_a_file_may_be_renamed()
    {
        var (path, name, error) = DetailFamilySpecParser.ResolveEditSavePath(@"C:\Out\Teine.rfa", "EL_Kilp", keepName: true);
        Assert.Null(path);
        Assert.Contains("must be the family's name 'EL_Kilp'", error);

        (path, name, error) = DetailFamilySpecParser.ResolveEditSavePath(@"C:\Out", "EL_Kilp", keepName: true);
        Assert.Null(error);
        Assert.Equal(System.IO.Path.Combine(@"C:\Out", "EL_Kilp.rfa"), path);

        (path, name, error) = DetailFamilySpecParser.ResolveEditSavePath(@"C:\Out\Teine.rfa", "EL_Kilp", keepName: false);
        Assert.Null(error);
        Assert.Equal("Teine", name);
        Assert.Equal(@"C:\Out\Teine.rfa", path);
    }

    // ── Removals ─────────────────────────────────────────────────────────────

    [Fact]
    public void Element_removals_resolve_against_the_family()
    {
        var errors = new List<string>();
        var all = DetailFamilySpecParser.ResolveElementRemoval(new ElementRemoval { All = true }, "remove.curves", "a detail line",
            new long[] { 30, 10, 20 }, _ => null, errors);
        Assert.Equal(new long[] { 10, 20, 30 }, all);

        var some = new ElementRemoval();
        some.Ids.AddRange(new long[] { 10, 99, 55 });
        var picked = DetailFamilySpecParser.ResolveElementRemoval(some, "remove.curves", "a detail line",
            new long[] { 10, 20 }, id => id == 55 ? "a FilledRegion" : null, errors);
        Assert.Equal(new long[] { 10 }, picked);
        Assert.Contains("remove.curves: the family has no element 99.", errors);
        Assert.Contains("remove.curves: element 55 is a FilledRegion, not a detail line.", errors);
    }

    private static Dictionary<string, KnownFamilyParameter> Known(params KnownFamilyParameter[] parameters) =>
        parameters.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    private static KnownFamilyParameter P(string name, string? friendly = "length", string formula = "", bool builtIn = false, params long[] linked)
    {
        var p = new KnownFamilyParameter { Name = name, Friendly = friendly, DataTypeLabel = friendly ?? "Material", Formula = formula, IsBuiltIn = builtIn };
        p.AssociatedElementIds.AddRange(linked);
        return p;
    }

    [Fact]
    public void Parameter_removals_refuse_built_in_unknown_used_and_linked_ones()
    {
        var existing = Known(
            P("Laius"), P("Pool", formula: "Laius / 2"), P("Kõrgus"),
            P("Type Comments", "text", builtIn: true),
            P("Näita", "yesNo", linked: new long[] { 100, 101 }),
            P("Silt", "text", linked: new long[] { 200 }));
        var errors = new List<string>();
        var plan = DetailFamilySpecParser.PlanParameterRemovals(
            new[] { "Laius", "Type Comments", "Kõrgu", "Näita", "Silt" }, existing, new HashSet<long> { 200 }, errors);

        Assert.Contains(errors, e => e.Contains("'Laius' is used in the formula of 'Pool' (Laius / 2)"));
        Assert.Contains(errors, e => e.Contains("'Type Comments' is a built-in parameter"));
        Assert.Contains(errors, e => e.Contains("no parameter 'Kõrgu'") && e.Contains("'Kõrgus'"));
        Assert.Contains(errors, e => e.Contains("'Näita' is linked to 2 element(s) that stay (ids 100, 101)"));
        var silt = Assert.Single(plan);              // its only linked element is removed by the same edit
        Assert.Equal("Silt", silt.Name);
        Assert.Equal(new long[] { 200 }, silt.UnlinkedElementIds);
    }

    [Fact]
    public void A_parameter_and_the_one_whose_formula_uses_it_go_together_formula_first()
    {
        var existing = Known(P("Laius"), P("Pool", formula: "Laius / 2"), P("Veerand", formula: "Pool / 2"));
        var errors = new List<string>();
        var plan = DetailFamilySpecParser.PlanParameterRemovals(new[] { "Laius", "Pool", "Veerand" }, existing, new HashSet<long>(), errors);
        Assert.Empty(errors);
        Assert.Equal(new[] { "Veerand", "Pool", "Laius" }, plan.Select(p => p.Name));
    }

    [Theory]
    [InlineData("Laius / 2", "Laius", true)]
    [InlineData("if(Näita, Laius, 0 mm)", "laius", true)]
    [InlineData("Laius2 * 2", "Laius", false)]
    [InlineData("Uus_Laius + 1", "Laius", false)]
    [InlineData("Kilbi laius / 2", "Kilbi laius", true)]
    [InlineData("", "Laius", false)]
    public void Formula_references_respect_name_boundaries(string formula, string name, bool expected)
        => Assert.Equal(expected, DetailFamilySpecParser.FormulaUses(formula, name));

    // ── References against the family ────────────────────────────────────────

    [Fact]
    public void Visibility_labels_and_type_values_may_use_the_family_s_own_parameters()
    {
        var (edit, errors) = Edit(("family", "EL_Kilp"),
            ("lines", "[{\"start\":[0,0],\"end\":[10,0],\"visibleParameter\":\"Näita\"},{\"start\":[0,5],\"end\":[10,5],\"visibleParameter\":\"Tähis\"}]"),
            ("labels", "[{\"parameter\":\"Tähis\",\"position\":[0,0],\"labelFamily\":\"EULE_Silt\"},{\"parameter\":\"Kadunud\",\"position\":[0,0],\"labelFamily\":\"EULE_Silt\"}]"),
            ("types", "[{\"name\":\"T\",\"values\":{\"Laius\":5,\"Pool\":1,\"Materjal\":\"x\",\"Kadunud\":\"y\"}}]"),
            ("remove", "{\"parameters\":[\"Kadunud\"]}"));
        Assert.Empty(errors);
        edit.Content.FamilyName = "EL_Kilp";

        var existing = Known(P("Näita", "yesNo"), P("Tähis", "text"), P("Laius"), P("Pool", formula: "Laius / 2"),
            P("Materjal", null), P("Kadunud", "text"));
        var removed = new HashSet<string>(edit.Remove.Parameters, StringComparer.OrdinalIgnoreCase);
        DetailFamilySpecParser.CheckParameterReferences(edit.Content, errors, existing, removed);
        DetailFamilySpecParser.CheckTypeValues(edit.Content, errors, existing, removed);

        Assert.Contains(errors, e => e.Contains("lines[1].visibleParameter 'Tähis' is a text parameter of the family"));
        Assert.DoesNotContain(errors, e => e.Contains("lines[0]"));
        Assert.Contains(errors, e => e.Contains("labels[1].parameter 'Kadunud'") && e.Contains("remove.parameters removes it"));
        Assert.DoesNotContain(errors, e => e.Contains("labels[0]"));
        Assert.Contains(errors, e => e.Contains("'Pool' has the formula 'Laius / 2'"));
        Assert.Contains(errors, e => e.Contains("values['Materjal']") && e.Contains("cannot be set here"));
        Assert.Contains(errors, e => e.Contains("values['Kadunud']") && e.Contains("remove.parameters removes it"));
        Assert.DoesNotContain(errors, e => e.Contains("values['Laius']"));
    }

    [Fact]
    public void A_removed_parameter_added_again_may_be_used()
    {
        var (edit, errors) = Edit(("family", "EL_Kilp"),
            ("parameters", "[{\"name\":\"Laius\",\"dataType\":\"length\"}]"),
            ("types", "[{\"name\":\"T\",\"values\":{\"Laius\":5}}]"),
            ("remove", "{\"parameters\":[\"Laius\"]}"));
        var removed = new HashSet<string>(edit.Remove.Parameters, StringComparer.OrdinalIgnoreCase);
        DetailFamilySpecParser.CheckTypeValues(edit.Content, errors, Known(P("Laius", "text")), removed);
        Assert.Empty(errors);
    }

    [Fact]
    public void New_label_families_miss_the_names_the_family_already_nests()
    {
        var (spec, errors) = Create(("labels", "[{\"text\":\"A\",\"position\":[0,0]},{\"text\":\"B\",\"position\":[0,5]}]"));
        Assert.Empty(errors);
        var groups = DetailFamilySpecParser.GroupLabels(spec, errors);
        DetailFamilySpecParser.RenameLabelGroups(groups, "EL_Kilp", new[] { "EL_Kilp_Text1", "el_kilp_text3", "Muu" });
        Assert.Equal(new[] { "EL_Kilp_Text2", "EL_Kilp_Text4" }, groups.Select(g => g.FamilyName));
    }

    // ── Tool surface ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("revit_preview_edit_detail_family", "ReadOnly")]
    [InlineData("revit_edit_detail_family", "RequiresApproval")]
    public void The_edit_tools_match_on_both_sides(string tool, string permission)
    {
        Assert.Equal("edit", BridgeToolGroups.GroupOf(tool));
        Assert.Equal(permission, ToolSourceScan.Addin.Value[tool].Permission);
        var bridge = Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == tool);
        Assert.Equal(permission == "ReadOnly", bridge.ReadOnly);
        Assert.Contains(tool, bridge.Targets);
        Assert.Contains("SendEditDetailFamily", bridge.Source);
    }

    [Fact]
    public void Create_sends_types_and_editing_counts_as_creating()
    {
        var edit = Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == "revit_edit_detail_family").Source;
        foreach (var argument in new[] { "\"family\"", "\"path\"", "\"remove\"", "\"types\"", "\"overwriteParameterValues\"", "\"document\"" })
            Assert.Contains(argument, edit);
        Assert.Contains("\"types\"", Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == "revit_create_detail_family").Source);
        Assert.Equal(PlanToolKind.Creates, PlanToolClassification.Classify("revit_edit_detail_family"));
    }
}
