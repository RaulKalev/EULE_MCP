using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Keynotes;
using RevitMCP.Addin.Legends;
using RevitMCP.Addin.Plans;
using RevitMCP.Bridge;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>Legends (revit_list_legends, revit_populate_legend) and keynotes (revit_get_keynotes, revit_reload_keynote_table).</summary>
public class LegendsAndKeynotesTests
{
    private static (LegendPopulateRequest Request, List<string> Errors) Parse(string json)
    {
        var errors = new List<string>();
        var args = JObject.Parse(json).Properties().ToDictionary(p => p.Name, p => (object?)p.Value);
        return (LegendPlanning.Parse(args, errors), errors);
    }

    private static readonly List<LegendTypeInfo> Available = new()
    {
        new() { Id = 10, Family = "EL_Pistikupesa", Type = "2x", Category = "Electrical Fixtures", Kind = LegendPlacementKind.Component },
        new() { Id = 11, Family = "EL_Pistikupesa", Type = "1x", Category = "Electrical Fixtures", Kind = LegendPlacementKind.Component },
        new() { Id = 12, Family = "EL_Lüliti", Type = "1x", Category = "Lighting Devices", Kind = LegendPlacementKind.Component },
        new() { Id = 20, Family = "EN_Kaabel", Type = "Standard", Category = "Detail Items", Kind = LegendPlacementKind.DetailInstance },
        new() { Id = 30, Family = "Door Tag", Type = "Standard", Category = "Door Tags", Kind = LegendPlacementKind.Unsupported, UnsupportedReason = "tag" }
    };

    // ── Arguments ────────────────────────────────────────────────────────────

    [Fact]
    public void Reads_types_grid_label_and_options()
    {
        var (r, errors) = Parse(
            "{\"legend\":\"EN tingmärgid\",\"types\":[10,{\"family\":\"EL_Lüliti\",\"type\":\"1x\"},{\"id\":20},\"EL_Pistikupesa: 1x\",{\"family\":\"EN_Kaabel\"}]," +
            "\"columns\":3,\"rowSpacingMm\":8,\"columnSpacingMm\":60,\"origin\":[5,-10],\"label\":\"Description\",\"textType\":\"2.5mm\"," +
            "\"textOffsetMm\":9,\"clearExisting\":true,\"sortBy\":\"Family\",\"seedId\":555,\"dryRun\":false}");

        Assert.Empty(errors);
        Assert.Equal("EN tingmärgid", r.Legend);
        Assert.Equal(5, r.Types.Count);
        Assert.Equal(10, r.Types[0].Id);
        Assert.Equal("EL_Lüliti", r.Types[1].Family);
        Assert.Equal(20, r.Types[2].Id);
        Assert.Equal(("EL_Pistikupesa", "1x"), (r.Types[3].Family, r.Types[3].Type));
        Assert.Null(r.Types[4].Type);
        Assert.Equal(3, r.Columns);
        Assert.Equal(8, r.RowSpacingMm);
        Assert.Equal(60, r.ColumnSpacingMm);
        Assert.Equal(new[] { 5.0, -10.0 }, r.Origin);
        Assert.Equal(LegendLabelMode.Parameter, r.Label);
        Assert.Equal("Description", r.LabelParameter);
        Assert.Equal("2.5mm", r.TextType);
        Assert.Equal(9, r.TextOffsetMm);
        Assert.True(r.ClearExisting);
        Assert.Equal("family", r.SortBy);
        Assert.Equal(555, r.SeedId);
        Assert.False(r.DryRun);
    }

    [Theory]
    [InlineData("typeName", LegendLabelMode.TypeName)]
    [InlineData("familyAndType", LegendLabelMode.FamilyAndType)]
    [InlineData("none", LegendLabelMode.None)]
    [InlineData("Type Mark", LegendLabelMode.Parameter)]
    public void Reads_the_label_mode(string label, LegendLabelMode expected)
    {
        var (r, errors) = Parse($"{{\"legend\":\"L\",\"category\":\"Electrical Fixtures\",\"label\":\"{label}\"}}");
        Assert.Empty(errors);
        Assert.Equal(expected, r.Label);
        Assert.Equal("family", r.SortBy);   // filters sort by family by default
    }

    [Theory]
    [InlineData("{\"types\":[10]}", "Provide 'legend'")]
    [InlineData("{\"legend\":\"L\"}", "Provide types")]
    [InlineData("{\"legend\":\"L\",\"types\":[10],\"category\":\"Doors\"}", "not both")]
    [InlineData("{\"legend\":\"L\",\"types\":[{\"note\":1}]}", "needs an id")]
    [InlineData("{\"legend\":\"L\",\"types\":[-4]}", "not a valid type id")]
    [InlineData("{\"legend\":\"L\",\"types\":[10],\"columns\":0}", "columns must be")]
    [InlineData("{\"legend\":\"L\",\"types\":[10],\"rowSpacingMm\":0}", "rowSpacingMm must be")]
    [InlineData("{\"legend\":\"L\",\"types\":[10],\"origin\":[1]}", "origin must be")]
    [InlineData("{\"legend\":\"L\",\"types\":[10],\"sortBy\":\"size\"}", "sortBy 'size'")]
    [InlineData("{\"legend\":\"L\",\"types\":[10],\"seedId\":\"x\"}", "seedId must be")]
    public void Refuses_bad_arguments(string json, string expected)
    {
        var (_, errors) = Parse(json);
        Assert.Contains(errors, e => e.Contains(expected));
    }

    // ── Matching ─────────────────────────────────────────────────────────────

    [Fact]
    public void Types_are_matched_by_id_name_and_family_in_request_order_without_duplicates()
    {
        var (r, _) = Parse("{\"legend\":\"L\",\"types\":[20,{\"family\":\"el_pistikupesa\"},11,{\"family\":\"EL_Lüliti\",\"type\":\"1X\"}]}");
        var problems = new List<string>();
        var picked = LegendPlanning.Select(r, Available, null, problems);

        Assert.Empty(problems);
        Assert.Equal(new long[] { 20, 11, 10, 12 }, picked.Select(p => p.Id));   // the family's types by name; 11 not twice
    }

    [Fact]
    public void Unknown_and_ambiguous_references_are_problems_with_close_names()
    {
        var (r, _) = Parse("{\"legend\":\"L\",\"types\":[999,{\"family\":\"EL_Pistikupes\",\"type\":\"2x\"},{\"family\":\"EL_Pistikupesa\",\"type\":\"1xx\"},{\"type\":\"1x\"}]}");
        var problems = new List<string>();
        LegendPlanning.Select(r, Available, null, problems);

        Assert.Contains(problems, p => p.Contains("999 is not the id of a type"));
        Assert.Contains(problems, p => p.Contains("did you mean 'EL_Pistikupesa'"));
        Assert.Contains(problems, p => p.Contains("'EL_Pistikupesa: 1xx'") && p.Contains("'1x'"));
        Assert.Contains(problems, p => p.Contains("is in 2 families"));
    }

    [Fact]
    public void Category_and_families_filters_select_every_matching_type_sorted_by_family()
    {
        var (r, _) = Parse("{\"legend\":\"L\",\"category\":\"electrical fixtures\"}");
        var problems = new List<string>();
        var picked = LegendPlanning.Select(r, Available, "Electrical Fixtures", problems);
        Assert.Empty(problems);
        Assert.Equal(new long[] { 11, 10 }, picked.Select(p => p.Id));   // family, then type name

        var (both, _) = Parse("{\"legend\":\"L\",\"families\":[\"EN_Kaabel\",\"EL_Lülit\"]}");
        problems.Clear();
        picked = LegendPlanning.Select(both, Available, null, problems);
        Assert.Contains(problems, p => p.Contains("no family 'EL_Lülit'") && p.Contains("'EL_Lüliti'"));
        Assert.Equal(new long[] { 20 }, picked.Select(p => p.Id));
    }

    [Fact]
    public void A_filter_that_matches_nothing_is_a_problem()
    {
        var (r, _) = Parse("{\"legend\":\"L\",\"category\":\"Doors\"}");
        var problems = new List<string>();
        Assert.Empty(LegendPlanning.Select(r, Available, "Doors", problems));
        Assert.Contains(problems, p => p.Contains("No type matches category 'Doors'"));
    }

    [Fact]
    public void Labels_follow_the_mode()
    {
        var type = new LegendTypeInfo { Family = "EL_Pistikupesa", Type = "2x", LabelParameterValue = "  Pistikupesa 2-kohaline " };
        LegendPopulateRequest With(string label) => Parse($"{{\"legend\":\"L\",\"types\":[1],\"label\":\"{label}\"}}").Request;

        Assert.Equal("2x", LegendPlanning.LabelText(With("typeName"), type));
        Assert.Equal("EL_Pistikupesa: 2x", LegendPlanning.LabelText(With("familyAndType"), type));
        Assert.Null(LegendPlanning.LabelText(With("none"), type));
        Assert.Equal("Pistikupesa 2-kohaline", LegendPlanning.LabelText(With("Description"), type));
        Assert.Null(LegendPlanning.LabelText(With("Description"), new LegendTypeInfo { Type = "x", LabelParameterValue = " " }));
    }

    // ── Grid ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Cells_fill_rows_left_to_right_then_go_down()
    {
        var cells = LegendPlanning.Cells(5, 2, rowSpacingMm: 10, columnSpacingMm: 80, origin: new[] { 0.0, 100.0 }, textOffsetMm: 12);

        Assert.Equal(new[] { (0, 0), (0, 1), (1, 0), (1, 1), (2, 0) }, cells.Select(c => (c.Row, c.Column)));
        Assert.Equal(80, cells[1].XMm);
        Assert.Equal(100, cells[1].YMm);
        Assert.Equal(80, cells[4].YMm);
        Assert.Equal(12, cells[4].LabelXMm);
        Assert.Equal(cells[4].YMm, cells[4].LabelYMm);
    }

    [Fact]
    public void The_default_origin_is_one_row_under_what_stays_aligned_with_the_seed()
    {
        var kept = new[]
        {
            new LegendExistingItem { Id = 1, Kind = "component", MinXMm = 2, MinYMm = 40 },
            new LegendExistingItem { Id = 2, Kind = "textNote", MinXMm = 15, MinYMm = 37.5 }
        };
        Assert.Equal(new[] { 5.0, 27.5 }, LegendPlanning.DefaultOrigin(new[] { 5.0, 42.0 }, kept, 10));
        // Only the seed stays (clearExisting): one row under it.
        Assert.Equal(new[] { 5.0, 32.0 }, LegendPlanning.DefaultOrigin(new[] { 5.0, 42.0 }, Array.Empty<LegendExistingItem>(), 10));
        // No seed: under the lowest element, at the leftmost one; an empty legend starts at 0, 0.
        Assert.Equal(new[] { 2.0, 27.5 }, LegendPlanning.DefaultOrigin(null, kept, 10));
        Assert.Equal(new[] { 0.0, 0.0 }, LegendPlanning.DefaultOrigin(null, Array.Empty<LegendExistingItem>(), 10));
    }

    [Fact]
    public void Printed_mm_and_model_feet_convert_with_the_scale()
    {
        Assert.Equal(10 * 50 / 304.8, LegendPlanning.PaperMmToModelFeet(10, 50), 9);
        Assert.Equal(10, LegendPlanning.ModelFeetToPaperMm(LegendPlanning.PaperMmToModelFeet(10, 50), 50));
    }

    // ── clearExisting and approval ───────────────────────────────────────────

    [Fact]
    public void Clear_existing_keeps_the_seed()
    {
        var existing = new[]
        {
            new LegendExistingItem { Id = 1, Kind = "component" }, new LegendExistingItem { Id = 2, Kind = "component" },
            new LegendExistingItem { Id = 3, Kind = "textNote" }, new LegendExistingItem { Id = 4, Kind = "detailItem" }
        };
        var toClear = LegendPlanning.ToClear(existing, seedId: 1);
        Assert.Equal(new long[] { 2, 3, 4 }, toClear.Select(c => c.Id));

        var summary = LegendPlanning.ApprovalSummary("P125_EN", "EN tingmärgid", 6, 6, toClear, clearExisting: true, problems: 0);
        Assert.Contains("Place 6 symbol(s) with 6 text label(s) in legend 'EN tingmärgid' of 'P125_EN'", summary);
        Assert.Contains("DELETES 3 element(s)", summary);
        Assert.Contains("1 legend component(s), 1 text note(s), 1 detail item(s)", summary);
        Assert.Contains("the seed stays", summary);
        Assert.DoesNotContain("DELETES", LegendPlanning.ApprovalSummary("P", "L", 2, 0, toClear, clearExisting: false, problems: 0));
        Assert.Contains("DELETES the legend components", LegendPlanning.ApprovalSummary("", "L", 2, 0, null, clearExisting: true, problems: 1));
    }

    // ── Keynotes ─────────────────────────────────────────────────────────────

    private static readonly List<KeynoteEntryInfo> Table = new()
    {
        new() { Key = "E", Text = "Elekter" },
        new() { Key = "E.10", Text = "Pistikupesad", ParentKey = "E" },
        new() { Key = "E.2", Text = "Valgustid", ParentKey = "E" },
        new() { Key = "E.2.1", Text = "Laevalgusti", ParentKey = "E.2" },
        new() { Key = "N", Text = "Nõrkvool" }
    };

    [Fact]
    public void Keynotes_are_searched_filtered_by_parent_and_in_natural_key_order()
    {
        Assert.Equal(new[] { "E", "E.2", "E.2.1", "E.10", "N" }, KeynoteLogic.Filter(Table, null, null).Select(e => e.Key));
        Assert.Equal(new[] { "E.2", "E.10" }, KeynoteLogic.Filter(Table, null, "E").Select(e => e.Key));
        Assert.Equal(new[] { "E", "N" }, KeynoteLogic.Filter(Table, null, "root").Select(e => e.Key));
        Assert.Equal(new[] { "E", "N" }, KeynoteLogic.Filter(Table, null, "").Select(e => e.Key));
        Assert.Equal(new[] { "E.2", "E.2.1" }, KeynoteLogic.Filter(Table, "valgust", null).Select(e => e.Key));
        Assert.Equal(2, KeynoteLogic.ChildCounts(Table)["E"]);
        Assert.Equal((0, 100), KeynoteLogic.Page(-5, 0));
        Assert.Equal((20, 1000), KeynoteLogic.Page(20, 5000));
    }

    [Fact]
    public void A_keynote_file_is_parsed_with_its_problems()
    {
        var parsed = KeynoteLogic.ParseFile(
            "\uFEFFE\tElekter\r\nE.10\tPistikupesad\tE\r\n\r\n# kommentaar\r\nilma tabita\r\n\tTühi võti\r\nE.10\tKaks korda\tE\r\nX.1\tOrb\tX\n");

        Assert.Equal(new[] { "E", "E.10", "X.1" }, parsed.Entries.Select(e => e.Key));
        Assert.Equal("E", parsed.Entries[1].ParentKey);
        Assert.Contains(parsed.Problems, p => p.StartsWith("line 5: no TAB"));
        Assert.Contains(parsed.Problems, p => p.StartsWith("line 6: the key is empty"));
        Assert.Contains(parsed.Problems, p => p.StartsWith("line 7: the key 'E.10' is listed twice"));
        Assert.Contains(parsed.Problems, p => p.Contains("parent 'X' is not in the file"));
    }

    [Fact]
    public void Two_tables_are_compared_by_key()
    {
        var after = new List<KeynoteEntryInfo>
        {
            new() { Key = "E", Text = "Elekter" },
            new() { Key = "E.10", Text = "Pistikupesad ja lülitid", ParentKey = "E" },
            new() { Key = "E.2", Text = "Valgustid", ParentKey = "N" },
            new() { Key = "N", Text = "Nõrkvool" },
            new() { Key = "N.1", Text = "Andmeside", ParentKey = "N" }
        };
        var diff = KeynoteLogic.Compare(Table, after);

        Assert.Equal(new[] { "N.1" }, diff.Added.Select(e => e.Key));
        Assert.Equal(new[] { "E.2.1" }, diff.Removed.Select(e => e.Key));
        Assert.Equal(new[] { "E.2", "E.10" }, diff.Changed.Select(c => c.Key));
        Assert.Equal("Pistikupesad ja lülitid", diff.Changed[1].ToText);
        Assert.Equal(("E", "N"), (diff.Changed[0].FromParent, diff.Changed[0].ToParent));
        Assert.Equal(2, diff.Unchanged);
        Assert.False(KeynoteLogic.Compare(Table, Table).HasChanges);
    }

    [Fact]
    public void Usage_is_counted_per_key_with_keys_the_table_lacks()
    {
        var usage = KeynoteLogic.CountUsage(new[]
        {
            ("E.10", "type", 1L), ("E.10", "type", 2L), (" E.10 ", "material", 3L), ("E.10", "element", 4L), ("Z.9", "type", 5L), ("", "type", 6L)
        }, Table.Select(e => e.Key), out var unknown);

        Assert.Equal(2, usage["E.10"].Types);
        Assert.Equal(1, usage["E.10"].Materials);
        Assert.Equal(1, usage["E.10"].Elements);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, usage["E.10"].SampleIds);
        Assert.Equal(new[] { "Z.9" }, unknown.Select(u => u.Key));
        Assert.Equal(2, usage.Count);
    }

    [Fact]
    public void The_reload_approval_names_the_file()
    {
        Assert.Contains("from its current file 'C:\\K\\keynotes.txt'", KeynoteLogic.ApprovalSummary("P125", null, @"C:\K\keynotes.txt"));
        var other = KeynoteLogic.ApprovalSummary("P125", @"C:\K\uus.txt", @"C:\K\keynotes.txt");
        Assert.Contains("from 'C:\\K\\uus.txt' (it now points to 'C:\\K\\keynotes.txt')", other);
        Assert.Contains("One transaction, one undo", other);
    }

    // ── Tool surface ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("revit_list_legends", "ReadOnly", "views")]
    [InlineData("revit_preview_populate_legend", "ReadOnly", "views")]
    [InlineData("revit_populate_legend", "RequiresApproval", "views")]
    [InlineData("revit_get_keynotes", "ReadOnly", "tags")]
    [InlineData("revit_preview_reload_keynote_table", "ReadOnly", "tags")]
    [InlineData("revit_reload_keynote_table", "RequiresApproval", "tags")]
    public void The_tools_match_on_both_sides(string tool, string permission, string group)
    {
        Assert.Equal(group, BridgeToolGroups.GroupOf(tool));
        Assert.Equal(permission, ToolSourceScan.Addin.Value[tool].Permission);
        var bridge = Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == tool);
        Assert.Equal(permission == "ReadOnly", bridge.ReadOnly);
        Assert.Contains(tool, bridge.Targets);
        Assert.Contains("\"document\"", bridge.Source);
    }

    [Fact]
    public void Plans_count_populating_a_legend_as_creating_and_reloading_keynotes_as_modifying()
    {
        Assert.Equal(PlanToolKind.Creates, PlanToolClassification.Classify("revit_populate_legend"));
        Assert.Equal(PlanToolKind.Modifies, PlanToolClassification.Classify("revit_reload_keynote_table"));
    }
}
