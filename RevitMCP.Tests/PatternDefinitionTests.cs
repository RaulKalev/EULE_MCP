using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Patterns;
using RevitMCP.Addin.Plans;
using RevitMCP.Bridge;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>Line and fill pattern definitions: revit_set_line_patterns and revit_set_fill_patterns.</summary>
public class PatternDefinitionTests
{
    private static (List<LinePatternDefinition> Patterns, List<string> Errors) ReadLine(string json)
    {
        var errors = new List<string>();
        var patterns = PatternSpecParser.ReadLinePatterns(new Dictionary<string, object?> { ["patterns"] = JToken.Parse(json) }, "patterns", errors);
        return (patterns, errors);
    }

    private static (List<FillPatternDefinition> Patterns, List<string> Errors) ReadFill(string json)
    {
        var errors = new List<string>();
        var patterns = PatternSpecParser.ReadFillPatterns(new Dictionary<string, object?> { ["patterns"] = JToken.Parse(json) }, "patterns", errors);
        return (patterns, errors);
    }

    // ── Line patterns ────────────────────────────────────────────────────────

    [Fact]
    public void Reads_segments_as_objects_or_pat_numbers()
    {
        var (patterns, errors) = ReadLine(
            "[{\"name\":\"EL_Kaabel\",\"segments\":[{\"type\":\"dash\",\"length\":6},{\"type\":\"space\",\"length\":1.5},{\"type\":\"dot\"},{\"type\":\"space\",\"lengthMm\":\"1.5\"}]}," +
            "{\"name\":\"EL_Katkend\",\"segments\":[3,-1.5,0,-1.5]}]");

        Assert.Empty(errors);
        Assert.Equal(2, patterns.Count);
        Assert.Equal(new[] { "dash", "space", "dot", "space" }, patterns[0].Segments.Select(s => s.Type));
        Assert.Equal(6, patterns[0].Segments[0].LengthMm);
        Assert.Equal(0, patterns[0].Segments[2].LengthMm);
        Assert.Equal(1.5, patterns[0].Segments[3].LengthMm);
        Assert.Equal(new[] { "dash", "space", "dot", "space" }, patterns[1].Segments.Select(s => s.Type));
        Assert.Equal(1.5, patterns[1].Segments[1].LengthMm);
    }

    [Theory]
    [InlineData("[{\"segments\":[3,-1]}]", "needs a name")]
    [InlineData("[{\"name\":\"A\",\"segments\":[3,-1]},{\"name\":\"a\",\"segments\":[3,-1]}]", "listed twice")]
    [InlineData("[{\"name\":\"A:B\",\"segments\":[3,-1]}]", "characters Revit refuses")]
    [InlineData("[{\"name\":\"Solid\",\"segments\":[3,-1]}]", "built-in solid line")]
    [InlineData("[{\"name\":\"A\"}]", "must be an array")]
    [InlineData("[{\"name\":\"A\",\"segments\":[]}]", "at least a dash or dot and a space")]
    [InlineData("[{\"name\":\"A\",\"segments\":[-1,3]}]", "is a space where a dash or dot belongs")]
    [InlineData("[{\"name\":\"A\",\"segments\":[3,2,-1,-1]}]", "where a space belongs")]
    [InlineData("[{\"name\":\"A\",\"segments\":[3,-1,2]}]", "end it with a space")]
    [InlineData("[{\"name\":\"A\",\"segments\":[{\"type\":\"dot\",\"length\":1},{\"type\":\"space\",\"length\":1}]}]", "a dot has no length")]
    [InlineData("[{\"name\":\"A\",\"segments\":[{\"type\":\"dash\"},{\"type\":\"space\",\"length\":1}]}]", "needs a length")]
    [InlineData("[{\"name\":\"A\",\"segments\":[{\"type\":\"bar\",\"length\":1}]}]", "not dash, space or dot")]
    [InlineData("[{\"name\":\"A\",\"segments\":[{\"type\":\"dash\",\"length\":0.001},{\"type\":\"space\",\"length\":1}]}]", "at least 0.01 mm")]
    [InlineData("[{\"name\":\"A\",\"segments\":[3,-20000]}]", "at most 10000 mm")]
    public void Refuses_bad_line_patterns(string json, string expected)
    {
        var (patterns, errors) = ReadLine(json);
        Assert.Contains(errors, e => e.Contains(expected));
        Assert.DoesNotContain(patterns, p => p.Name == "A:B" || p.Name == "Solid");
    }

    [Fact]
    public void Refuses_more_segments_than_the_limit()
    {
        var segments = string.Join(",", Enumerable.Repeat("3,-1", PatternSpecParser.MaxLineSegments / 2 + 1));
        var (_, errors) = ReadLine($"[{{\"name\":\"A\",\"segments\":[{segments}]}}]");
        Assert.Contains(errors, e => e.Contains($"at most {PatternSpecParser.MaxLineSegments}"));
    }

    [Fact]
    public void An_item_that_is_not_an_object_keeps_the_index_of_the_next()
    {
        var (_, errors) = ReadLine("[1, {\"segments\":[3,-1]}]");
        Assert.Contains("patterns[0] must be an object.", errors);
        Assert.Contains("patterns[1] needs a name.", errors);
    }

    [Fact]
    public void Segments_compare_within_tolerance()
    {
        var a = new List<PatternSegment> { new("dash", 3), new("space", 1.5) };
        Assert.True(PatternSpecParser.SameSegments(a, new List<PatternSegment> { new("dash", 3.0004), new("space", 1.5) }));
        Assert.False(PatternSpecParser.SameSegments(a, new List<PatternSegment> { new("dash", 3.01), new("space", 1.5) }));
        Assert.False(PatternSpecParser.SameSegments(a, new List<PatternSegment> { new("dot", 0), new("space", 1.5) }));
    }

    // ── Fill patterns ────────────────────────────────────────────────────────

    [Fact]
    public void The_simple_form_becomes_one_grid_or_a_crosshatch()
    {
        var (patterns, errors) = ReadFill(
            "[{\"name\":\"Viirutus\",\"angle\":45,\"spacing\":2}," +
            "{\"name\":\"Rist\",\"target\":\"model\",\"spacing\":300,\"crosshatch\":true}," +
            "{\"name\":\"Rist2\",\"angle\":30,\"spacing\":2,\"crosshatch\":{\"spacing\":4}}]");

        Assert.Empty(errors);
        var hatch = patterns[0];
        Assert.True(hatch.Simple);
        Assert.Equal("drafting", hatch.Target);
        Assert.Null(hatch.Orientation);
        var grid = Assert.Single(hatch.Grids);
        Assert.Equal(45, grid.AngleDeg);
        Assert.Equal(2, grid.OffsetMm);
        Assert.Empty(grid.Segments);

        var cross = patterns[1];
        Assert.Equal("model", cross.Target);
        Assert.Equal(new[] { 0.0, 90.0 }, cross.Grids.Select(g => g.AngleDeg));
        Assert.All(cross.Grids, g => Assert.Equal(300, g.OffsetMm));

        Assert.Equal(new[] { 30.0, 120.0 }, patterns[2].Grids.Select(g => g.AngleDeg));
        Assert.Equal(new[] { 2.0, 4.0 }, patterns[2].Grids.Select(g => g.OffsetMm));
    }

    [Fact]
    public void Reads_grids_with_origin_shift_and_segments()
    {
        var (patterns, errors) = ReadFill(
            "[{\"name\":\"Tellis\",\"target\":\"model\",\"orientation\":\"align with element\",\"grids\":[" +
            "{\"angle\":0,\"origin\":[0,0],\"offset\":75,\"shift\":0}," +
            "{\"angle\":90,\"origin\":{\"x\":0,\"y\":0},\"offset\":125,\"shift\":75,\"segments\":[{\"type\":\"dash\",\"length\":75},{\"type\":\"space\",\"length\":75}]}," +
            "{\"angle\":-45,\"origin\":[10,5],\"spacing\":-30,\"segments\":[0,-2]}]}]");

        Assert.Empty(errors);
        var p = Assert.Single(patterns);
        Assert.False(p.Simple);
        Assert.Equal("toHost", p.Orientation);
        Assert.Equal(3, p.Grids.Count);
        Assert.Equal(75, p.Grids[1].ShiftMm);
        Assert.Equal(new[] { "dash", "space" }, p.Grids[1].Segments.Select(s => s.Type));
        Assert.Equal(315, p.Grids[2].AngleDeg);                 // normalised to [0, 360)
        Assert.Equal(10, p.Grids[2].OriginXMm);
        Assert.Equal(-30, p.Grids[2].OffsetMm);                 // a negative offset is kept, as .pat files allow
        Assert.Equal("dot", p.Grids[2].Segments[0].Type);
    }

    [Theory]
    [InlineData("[{\"spacing\":2}]", "needs a name")]
    [InlineData("[{\"name\":\"Solid fill\",\"spacing\":2}]", "solid fill; it cannot be created or edited")]
    [InlineData("[{\"name\":\"<Solid fill>\",\"spacing\":2}]", "solid fill; it cannot be created or edited")]
    [InlineData("[{\"name\":\"A\",\"target\":\"paper\",\"spacing\":2}]", "not drafting or model")]
    [InlineData("[{\"name\":\"A\",\"orientation\":\"sideways\",\"spacing\":2}]", "is not toView")]
    [InlineData("[{\"name\":\"A\"}]", "needs spacing")]
    [InlineData("[{\"name\":\"A\",\"spacing\":2,\"grids\":[{\"offset\":2}]}]", "not both")]
    [InlineData("[{\"name\":\"A\",\"spacing\":0}]", "at least 0.01 mm")]
    [InlineData("[{\"name\":\"A\",\"angle\":\"steep\",\"spacing\":2}]", "angle must be a number")]
    [InlineData("[{\"name\":\"A\",\"spacing\":2,\"crosshatch\":{\"angle\":180}}]", "parallel to angle")]
    [InlineData("[{\"name\":\"A\",\"spacing\":2,\"crosshatch\":\"yes\"}]", "must be true or")]
    [InlineData("[{\"name\":\"A\",\"grids\":[]}]", "non-empty array")]
    [InlineData("[{\"name\":\"A\",\"grids\":[{\"angle\":0}]}]", "offset must be the distance")]
    [InlineData("[{\"name\":\"A\",\"grids\":[{\"offset\":0}]}]", "at least 0.01 mm")]
    [InlineData("[{\"name\":\"A\",\"grids\":[{\"offset\":2,\"origin\":[1]}]}]", "origin must be [x, y]")]
    [InlineData("[{\"name\":\"A\",\"grids\":[{\"offset\":2,\"segments\":[3]}]}]", "end it with a space")]
    [InlineData("[{\"name\":\"A\",\"spacing\":2},{\"name\":\"a\",\"target\":\"drafting\",\"spacing\":3}]", "listed twice")]
    public void Refuses_bad_fill_patterns(string json, string expected)
    {
        var (_, errors) = ReadFill(json);
        Assert.Contains(errors, e => e.Contains(expected));
    }

    [Fact]
    public void The_same_name_may_exist_once_per_target()
    {
        var (patterns, errors) = ReadFill("[{\"name\":\"Liiv\",\"spacing\":2},{\"name\":\"Liiv\",\"target\":\"model\",\"spacing\":200}]");
        Assert.Empty(errors);
        Assert.Equal(new[] { "drafting", "model" }, patterns.Select(p => p.Target));
    }

    [Fact]
    public void A_grid_line_without_segments_is_solid()
    {
        var (patterns, errors) = ReadFill("[{\"name\":\"A\",\"grids\":[{\"offset\":2,\"segments\":[]}]}]");
        Assert.Empty(errors);
        Assert.Empty(patterns[0].Grids[0].Segments);
    }

    [Theory]
    [InlineData("view", "toView")]
    [InlineData("Orient to view", "toView")]
    [InlineData("keep readable", "asText")]
    [InlineData("asText", "asText")]
    [InlineData("toHost", "toHost")]
    public void Reads_orientations(string text, string expected) => Assert.Equal(expected, PatternSpecParser.ParseOrientation(text));

    [Fact]
    public void A_new_pattern_without_orientation_gets_the_targets_default()
    {
        Assert.Equal("toView", PatternSpecParser.DefaultOrientation("drafting"));
        Assert.Equal("toHost", PatternSpecParser.DefaultOrientation("model"));
    }

    [Fact]
    public void A_dense_drafting_pattern_is_warned_about()
    {
        var (patterns, _) = ReadFill("[{\"name\":\"Tihe\",\"spacing\":0.1},{\"name\":\"Mudel\",\"target\":\"model\",\"spacing\":0.1}]");
        Assert.Single(PatternSpecParser.DensityWarnings(patterns[0]));
        Assert.Empty(PatternSpecParser.DensityWarnings(patterns[1]));
    }

    [Fact]
    public void Grid_lengths_read_back_either_way()
    {
        // Alternating positive lengths: dash (or a dot when 0), then a space.
        var alternating = PatternSpecParser.SegmentsFromGridLengths(new[] { 3.0, 1.5, 0, 1.5 });
        Assert.Equal(new[] { "dash", "space", "dot", "space" }, alternating.Select(s => s.Type));
        Assert.Equal(1.5, alternating[3].LengthMm);

        // Signed lengths, the .pat way.
        var signed = PatternSpecParser.SegmentsFromGridLengths(new[] { 3.0, -1.5, 0, -1.5 });
        Assert.Equal(new[] { "dash", "space", "dot", "space" }, signed.Select(s => s.Type));
        Assert.Equal(1.5, signed[1].LengthMm);

        Assert.Equal(new[] { 3.0, 1.5, 0, 1.5 }, PatternSpecParser.ToGridLengths(signed));
    }

    [Fact]
    public void Grids_compare_within_tolerance_and_ignore_the_shift_of_solid_lines()
    {
        var a = new List<FillGridDefinition> { new() { AngleDeg = 0, OffsetMm = 2, ShiftMm = 1 } };
        Assert.True(PatternSpecParser.SameGrids(a, new List<FillGridDefinition> { new() { AngleDeg = 359.99999, OffsetMm = 2.0002, ShiftMm = 5 } }));
        Assert.False(PatternSpecParser.SameGrids(a, new List<FillGridDefinition> { new() { AngleDeg = 45, OffsetMm = 2 } }));
        Assert.False(PatternSpecParser.SameGrids(a, new List<FillGridDefinition> { new() { OffsetMm = 2 }, new() { AngleDeg = 90, OffsetMm = 2 } }));

        var dashed = new List<FillGridDefinition> { new() { OffsetMm = 2, ShiftMm = 1, Segments = { new("dash", 3), new("space", 1) } } };
        Assert.False(PatternSpecParser.SameGrids(dashed, new List<FillGridDefinition> { new() { OffsetMm = 2, ShiftMm = 2, Segments = { new("dash", 3), new("space", 1) } } }));
    }

    [Fact]
    public void Summaries_name_what_changes()
    {
        var (lines, _) = ReadLine("[{\"name\":\"EL_Kaabel\",\"segments\":[6,-1.5,0,-1.5]}]");
        var line = PatternSpecParser.LineSummary(lines, 0, "Projekt");
        Assert.Contains("'EL_Kaabel' (dash 6, space 1.5, dot, space 1.5 mm)", line);
        Assert.Contains("'Projekt'", line);

        var (fills, _) = ReadFill("[{\"name\":\"Rist\",\"angle\":45,\"spacing\":2,\"crosshatch\":true},{\"name\":\"Tellis\",\"target\":\"model\",\"grids\":[{\"offset\":75}]}]");
        var fill = PatternSpecParser.FillSummary(fills, 1, string.Empty);
        Assert.Contains("'Rist' (drafting, crosshatch 45° every 2 mm)", fill);
        Assert.Contains("'Tellis' (model, 1 grid(s))", fill);
        Assert.Contains("the active document", fill);
        Assert.Contains("1 invalid item(s)", fill);
    }

    // ── Tool surface ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("revit_list_line_patterns", "ReadOnly")]
    [InlineData("revit_preview_set_line_patterns", "ReadOnly")]
    [InlineData("revit_set_line_patterns", "RequiresApproval")]
    [InlineData("revit_list_fill_patterns", "ReadOnly")]
    [InlineData("revit_preview_set_fill_patterns", "ReadOnly")]
    [InlineData("revit_set_fill_patterns", "RequiresApproval")]
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
    [InlineData("revit_set_line_patterns")]
    [InlineData("revit_set_fill_patterns")]
    public void Plans_count_setting_patterns_as_creating(string tool)
        => Assert.Equal(PlanToolKind.Creates, PlanToolClassification.Classify(tool));
}
