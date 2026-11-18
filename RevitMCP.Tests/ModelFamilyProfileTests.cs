using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelFamilies;
using RevitMCP.Addin.Plans;
using RevitMCP.Bridge;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>Forms from CAD profiles or family curves (#177): the loop analysis, plane rules, form selection and the tool surface.</summary>
public class ModelFamilyProfileTests
{
    private static readonly ModelFamilyFormFrame Level = ModelFamilyFormFrame.XY;

    private static ModelFamilyProfileCurve L(double x1, double y1, double x2, double y2, long? id = null, string layer = "Outline") =>
        ModelFamilyProfileCurve.Line(new[] { x1, y1, 0 }, new[] { x2, y2, 0 }, layer, id);

    private static ModelFamilyProfileCurve A(double x1, double y1, double xm, double ym, double x2, double y2, long? id = null, string layer = "Outline") =>
        ModelFamilyProfileCurve.Arc(new[] { x1, y1, 0 }, new[] { xm, ym, 0 }, new[] { x2, y2, 0 }, layer, id);

    private static List<ModelFamilyProfileCurve> Rectangle(double x0, double y0, double x1, double y1, long firstId = 1, string layer = "Outline") => new()
    {
        L(x0, y0, x1, y0, firstId, layer), L(x1, y0, x1, y1, firstId + 1, layer), L(x1, y1, x0, y1, firstId + 2, layer), L(x0, y1, x0, y0, firstId + 3, layer)
    };

    private static List<ModelFamilyProfileCurve> Circle(double cx, double cy, double r, long id = 100, string layer = "Outline") =>
        ModelFamilyProfileCurve.Circle(new[] { cx, cy, 0 }, new double[] { 1, 0, 0 }, new double[] { 0, 1, 0 }, r, layer, id);

    private static ModelFamilyProfileAnalysis Analyse(IEnumerable<ModelFamilyProfileCurve> curves, double tolerance = 0.05) =>
        ModelFamilyProfileAnalysis.Run(curves.ToList(), Level, new ModelFamilyProfileOptions { ToleranceMm = tolerance });

    private static IEnumerable<string> Kinds(ModelFamilyProfileAnalysis analysis) => analysis.Issues.Select(i => i.Kind);

    // ── Loops ────────────────────────────────────────────────────────────────

    [Fact]
    public void Four_unordered_and_reversed_lines_make_one_rectangle()
    {
        var analysis = Analyse(new[] { L(200, 100, 0, 100, 3), L(0, 0, 200, 0, 1), L(0, 0, 0, 100, 4), L(200, 100, 200, 0, 2) });

        var loop = Assert.Single(analysis.Loops);
        Assert.Equal("rectangle", loop.Kind);
        Assert.Equal(200, loop.Width, 6);
        Assert.Equal(100, loop.Height, 6);
        Assert.Equal(20000, loop.Area, 6);
        Assert.True(loop.Usable);
        Assert.Null(loop.Parent);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, loop.SourceIds);
        Assert.Empty(analysis.Open);
        Assert.Empty(analysis.Issues);
        Assert.True(ModelFamilyFormGeometry.SignedArea(loop.Uv) > 0, "loops run counter-clockwise");
    }

    [Fact]
    public void A_circular_hole_is_a_child_loop_with_its_exact_area()
    {
        var analysis = Analyse(Rectangle(0, 0, 200, 100).Concat(Circle(100, 50, 20)));

        Assert.Equal(2, analysis.Loops.Count);
        var outer = analysis.Loops[0];
        var hole = analysis.Loops[1];
        Assert.Equal("rectangle", outer.Kind);
        Assert.Equal("circle", hole.Kind);
        Assert.Equal(Math.PI * 400, hole.Area, 6);
        Assert.Equal(0, hole.Parent);
        Assert.Equal(1, hole.Depth);
        Assert.Equal(new[] { 1 }, outer.Children);
        Assert.Equal(40, hole.Width, 6);
    }

    [Fact]
    public void Nested_three_deep_gives_depths_zero_one_two()
    {
        var analysis = Analyse(Rectangle(0, 0, 300, 300, 1).Concat(Rectangle(50, 50, 250, 250, 11)).Concat(Rectangle(100, 100, 200, 200, 21)));

        Assert.Equal(new int?[] { null, 0, 1 }, analysis.Loops.Select(l => l.Parent));
        Assert.Equal(new[] { 0, 1, 2 }, analysis.Loops.Select(l => l.Depth));
        Assert.Equal(new double[] { 90000, 40000, 10000 }, analysis.Loops.Select(l => Math.Round(l.Area)));
    }

    [Fact]
    public void A_gap_of_point_three_mm_is_open_at_the_default_tolerance_and_closes_at_half_a_mm()
    {
        var curves = new[] { L(0, 0, 100, 0, 1), L(100, 0, 100, 50, 2), L(100, 50, 0, 50, 3), L(0, 50, 0, 0.3, 4) };

        var open = Analyse(curves, 0.05);
        Assert.Empty(open.Loops);
        var chain = Assert.Single(open.Open);
        Assert.Equal(4, chain.Segments);
        Assert.Equal(2, chain.Ends.Count);
        Assert.All(chain.Ends, e => Assert.Equal(0.3, e.Gap!.Value, 6));

        var closed = Analyse(curves, 0.5);
        var loop = Assert.Single(closed.Loops);
        Assert.True(loop.Usable);
        Assert.Empty(closed.Open);
    }

    [Fact]
    public void A_duplicated_line_is_used_once_and_reported()
    {
        var curves = Rectangle(0, 0, 100, 50).Concat(new[] { L(100, 50, 100, 0, 9) }).ToList();

        var analysis = Analyse(curves);

        var loop = Assert.Single(analysis.Loops);
        Assert.Equal(4, loop.Uv.Count);
        var duplicate = Assert.Single(analysis.Issues, i => i.Kind == "duplicate");
        Assert.Contains(9L, duplicate.SourceIds);
        Assert.Contains(9L, loop.SourceIds);   // removeSourceCurves deletes the copy too
        Assert.Contains("deduplicated", duplicate.Message);
    }

    [Fact]
    public void A_duplicated_arc_is_detected_also_when_reversed()
    {
        var curves = new List<ModelFamilyProfileCurve>
        {
            L(0, 0, 100, 0, 1), A(100, 0, 110, 10, 100, 20, 2), L(100, 20, 0, 20, 3), A(0, 20, -10, 10, 0, 0, 4),
            A(100, 20, 110, 10, 100, 0, 5)
        };

        var analysis = Analyse(curves);

        Assert.Single(analysis.Loops);
        Assert.Single(analysis.Issues, i => i.Kind == "duplicate");
    }

    [Fact]
    public void A_T_junction_gives_two_loops_that_share_an_edge_and_flags_the_branches()
    {
        // A 200 x 100 rectangle divided by a vertical line at x = 100; top and bottom edges are split there.
        var curves = new[]
        {
            L(0, 0, 100, 0, 1), L(100, 0, 200, 0, 2), L(200, 0, 200, 100, 3), L(200, 100, 100, 100, 4), L(100, 100, 0, 100, 5),
            L(0, 100, 0, 0, 6), L(100, 0, 100, 100, 7)
        };

        var analysis = Analyse(curves);

        Assert.Equal(2, analysis.Loops.Count);
        Assert.All(analysis.Loops, l => Assert.True(l.SharesEdges));
        Assert.All(analysis.Loops, l => Assert.Equal(10000, l.Area, 6));
        Assert.Equal(0, analysis.Loops[0].Min[0], 6);
        Assert.Equal(100, analysis.Loops[1].Min[0], 6);
        var branches = analysis.Issues.Where(i => i.Kind == "branch").ToList();
        Assert.Equal(2, branches.Count);
        Assert.All(branches, b => Assert.Equal(new[] { 0, 1 }, b.Loops));
        Assert.DoesNotContain(analysis.Loops, l => Math.Abs(l.Area - 20000) < 1);   // the outline around both is not a loop
    }

    [Fact]
    public void Two_rectangles_drawn_side_by_side_share_one_deduplicated_edge()
    {
        var analysis = Analyse(Rectangle(0, 0, 100, 100, 1).Concat(new[] { L(100, 0, 200, 0, 11), L(200, 0, 200, 100, 12), L(200, 100, 100, 100, 13), L(100, 100, 100, 0, 14) }));

        Assert.Equal(2, analysis.Loops.Count);
        Assert.All(analysis.Loops, l => Assert.True(l.SharesEdges));
        Assert.All(analysis.Loops, l => Assert.Null(l.Parent));
        Assert.Contains("duplicate", Kinds(analysis));
    }

    [Fact]
    public void A_slot_of_lines_and_arcs_is_one_mixed_loop_with_the_exact_area()
    {
        var analysis = Analyse(new[] { L(0, 0, 100, 0), A(100, 0, 110, 10, 100, 20), L(100, 20, 0, 20), A(0, 20, -10, 10, 0, 0) });

        var loop = Assert.Single(analysis.Loops);
        Assert.Equal("mixed", loop.Kind);
        Assert.Equal(100 * 20 + Math.PI * 100, loop.Area, 6);
        Assert.Equal(120, loop.Width, 6);
        Assert.Equal(20, loop.Height, 6);
        Assert.True(loop.Usable);
    }

    [Fact]
    public void A_symmetric_bow_tie_is_not_a_loop_and_is_reported()
    {
        var analysis = Analyse(new[] { L(0, 0, 10, 10), L(10, 10, 10, 0), L(10, 0, 0, 10), L(0, 10, 0, 0) });

        Assert.Empty(analysis.Loops);
        Assert.Contains("crossing", Kinds(analysis));
        Assert.Contains("selfIntersecting", Kinds(analysis));
    }

    [Fact]
    public void An_uneven_bow_tie_is_a_loop_that_cannot_be_used()
    {
        var analysis = Analyse(new[] { L(0, 0, 20, 10), L(20, 10, 20, 0), L(20, 0, 0, 6), L(0, 6, 0, 0) });

        var loop = Assert.Single(analysis.Loops);
        Assert.True(loop.SelfIntersecting);
        Assert.False(loop.Usable);
        Assert.Contains("selfIntersecting", Kinds(analysis));
    }

    [Fact]
    public void A_segment_shorter_than_Revit_allows_makes_the_loop_unusable()
    {
        var analysis = Analyse(new[] { L(0, 0, 100, 0), L(100, 0, 100, 99.7), L(100, 99.7, 99.7, 100), L(99.7, 100, 0, 100), L(0, 100, 0, 0) });

        var loop = Assert.Single(analysis.Loops);
        Assert.Equal(1, loop.ShortSegments);
        Assert.False(loop.Usable);
        Assert.Contains(loop.Reasons, r => r.Contains("shorter than Revit's minimum"));
        Assert.Contains("shortSegment", Kinds(analysis));
    }

    [Fact]
    public void Three_endpoints_within_the_tolerance_are_one_vertex()
    {
        // Two lines end near (100, 0) within 0.04 mm of each other but further from a third: clustering is transitive.
        var analysis = Analyse(new[] { L(0, 0, 100, 0), L(100.04, 0, 100, 50), L(100, 50, 0, 0), L(100.08, 0, 150, -10) }, 0.05);

        Assert.Single(analysis.Loops);
        Assert.Single(analysis.Issues, i => i.Kind == "branch");
        Assert.Single(analysis.Open);
    }

    [Fact]
    public void An_end_on_the_middle_of_another_line_is_reported_and_stays_open()
    {
        var analysis = Analyse(Rectangle(0, 0, 200, 100).Concat(new[] { L(100, 100, 100, 50, 9) }));

        Assert.Single(analysis.Loops);
        Assert.Contains("endpointOnSegment", Kinds(analysis));
        var chain = Assert.Single(analysis.Open);
        Assert.Equal(new long[] { 9 }, chain.SourceIds);
    }

    [Fact]
    public void Collinear_overlapping_lines_are_reported_not_merged()
    {
        var analysis = Analyse(Rectangle(0, 0, 200, 100).Concat(new[] { L(50, 0, 150, 0, 9) }));

        Assert.Contains("overlap", Kinds(analysis));
        Assert.Single(analysis.Loops);
    }

    [Fact]
    public void A_line_joining_an_outline_to_a_hole_is_a_bridge_and_both_loops_remain()
    {
        var curves = new List<ModelFamilyProfileCurve>
        {
            L(0, 0, 100, 0, 1), L(100, 0, 200, 0, 2), L(200, 0, 200, 100, 3), L(200, 100, 0, 100, 4), L(0, 100, 0, 0, 5),
            L(100, 0, 100, 30, 6)
        };
        curves.AddRange(new[] { L(80, 30, 100, 30, 7), L(100, 30, 120, 30, 8), L(120, 30, 120, 60, 9), L(120, 60, 80, 60, 10), L(80, 60, 80, 30, 11) });

        var analysis = Analyse(curves);

        Assert.Equal(2, analysis.Loops.Count);
        Assert.Equal(0, analysis.Loops[1].Parent);
        var bridge = Assert.Single(analysis.Issues, i => i.Kind == "bridge");
        Assert.Equal(new long[] { 6 }, bridge.SourceIds);
    }

    [Fact]
    public void Approximated_curves_mark_their_loop()
    {
        var points = Enumerable.Range(0, 33).Select(i => new[] { 50 + 30 * Math.Cos(i * Math.PI / 16), 50 + 20 * Math.Sin(i * Math.PI / 16), 0.0 }).ToList();
        var ellipse = ModelFamilyProfileCurve.Polyline(points, "Outline", 7, approximated: true, minSpacing: ModelFamilyFormCheck.RevitShortCurveMm);

        var analysis = Analyse(ellipse);

        var loop = Assert.Single(analysis.Loops);
        Assert.True(loop.Approximated);
        Assert.True(loop.Usable);
        Assert.Contains("approximated", Kinds(analysis));
    }

    [Fact]
    public void Polyline_drops_points_closer_than_the_spacing_but_keeps_both_ends()
    {
        var points = new List<double[]> { new double[] { 0, 0, 0 }, new double[] { 0.1, 0, 0 }, new double[] { 5, 0, 0 }, new double[] { 9.5, 0, 0 }, new double[] { 10, 0, 0 } };

        var lines = ModelFamilyProfileCurve.Polyline(points, minSpacing: 0.79);

        Assert.Equal(new double[] { 0, 5, 10 }, lines.Select(l => l.Start[0]).Concat(new[] { lines[lines.Count - 1].End[0] }));
    }

    [Fact]
    public void Curves_off_the_work_plane_are_left_out_as_issues()
    {
        var curves = Rectangle(0, 0, 100, 50).Concat(new[] { ModelFamilyProfileCurve.Line(new double[] { 0, 0, 5 }, new double[] { 100, 0, 5 }, "Other", 9) });

        var analysis = Analyse(curves);

        Assert.Single(analysis.Loops);
        var offPlane = Assert.Single(analysis.Issues, i => i.Kind == "offPlane");
        Assert.Equal(new long[] { 9 }, offPlane.SourceIds);
    }

    [Fact]
    public void The_result_does_not_depend_on_the_order_or_direction_of_the_curves()
    {
        var curves = Rectangle(0, 0, 200, 100, 1)
            .Concat(Circle(60, 50, 15, 20))
            .Concat(new[] { L(120, 30, 170, 30, 30), A(170, 30, 180, 40, 170, 50, 31), L(170, 50, 120, 50, 32), A(120, 50, 110, 40, 120, 30, 33) })
            .Concat(new[] { L(250, 0, 300, 0, 40), L(300, 0, 300, 40, 41) })
            .Concat(new[] { L(0, 100, 0, 0, 50) })
            .ToList();
        var expected = Analyse(curves).ToJson(200).ToString();

        var random = new Random(177);
        for (var round = 0; round < 8; round++)
        {
            var shuffled = curves.OrderBy(_ => random.Next()).Select(c => random.Next(2) == 0 ? c : Reverse(c)).ToList();
            Assert.Equal(expected, Analyse(shuffled).ToJson(200).ToString());
        }
    }

    private static ModelFamilyProfileCurve Reverse(ModelFamilyProfileCurve c) => c.Kind == ModelFamilyFormSegmentKind.Line
        ? ModelFamilyProfileCurve.Line(c.End, c.Start, c.Layer, c.SourceId)
        : ModelFamilyProfileCurve.Arc(c.End, c.Mid!, c.Start, c.Layer, c.SourceId);

    [Fact]
    public void The_innermost_loop_around_a_point_is_found()
    {
        var analysis = Analyse(Rectangle(0, 0, 200, 100).Concat(Circle(100, 50, 20)));

        Assert.Equal(1, analysis.LoopAt(new double[] { 100, 50, 0 })!.Index);
        Assert.Equal(0, analysis.LoopAt(new double[] { 10, 10, 0 })!.Index);
        Assert.Null(analysis.LoopAt(new double[] { 300, 10, 0 }));
    }

    [Fact]
    public void The_json_lists_loops_in_family_millimetres()
    {
        var front = ModelFamilyProfilePlane.FrameFor(new double[] { 0, 0, 0 }, new double[] { 0, -1, 0 });
        var curves = new[]
        {
            ModelFamilyProfileCurve.Line(new double[] { -50, 0, 0 }, new double[] { 50, 0, 0 }),
            ModelFamilyProfileCurve.Line(new double[] { 50, 0, 0 }, new double[] { 50, 0, 80 }),
            ModelFamilyProfileCurve.Line(new double[] { 50, 0, 80 }, new double[] { -50, 0, 80 }),
            ModelFamilyProfileCurve.Line(new double[] { -50, 0, 80 }, new double[] { -50, 0, 0 })
        };

        var json = ModelFamilyProfileAnalysis.Run(curves, front).ToJson(10);

        var loop = (JObject)json["loops"]![0]!;
        Assert.Equal(100, (double)loop["width"]!);
        Assert.Equal(80, (double)loop["height"]!);
        Assert.Equal("[-50.0,0.0,0.0]", loop["min"]!.ToString(Newtonsoft.Json.Formatting.None));
        Assert.Equal("[0.0,0.0,40.0]", loop["centre"]!.ToString(Newtonsoft.Json.Formatting.None));
        Assert.Equal(1, (int)json["counts"]!["loops"]!);
    }

    // ── Planes ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0, 1, 1, 0, 0, 0, 1, 0)]
    [InlineData(0, 0, -1, 1, 0, 0, 0, 1, 0)]
    [InlineData(0, -1, 0, 1, 0, 0, 0, 0, 1)]
    [InlineData(1, 0, 0, 0, 1, 0, 0, 0, 1)]
    public void The_frame_axes_follow_the_family_axes(double nx, double ny, double nz, double xx, double xy, double xz, double yx, double yy, double yz)
    {
        var frame = ModelFamilyProfilePlane.FrameFor(new double[] { 3, 4, 5 }, new[] { nx, ny, nz });

        Assert.Equal(new[] { xx, xy, xz }, frame.XAxis.Select(v => Math.Round(v, 9) + 0.0));
        Assert.Equal(new[] { yx, yy, yz }, frame.YAxis.Select(v => Math.Round(v, 9) + 0.0));
        Assert.Equal(0, frame.OffsetOf(new double[] { 3, 4, 5 }), 9);
    }

    [Fact]
    public void A_fitted_plane_snaps_to_the_family_axis_and_reports_the_spread()
    {
        var points = new List<double[]> { new double[] { 0, 0, 10 }, new double[] { 100, 0, 10 }, new double[] { 100, 50, 10.2 }, new double[] { 0, 50, 10 } };

        var fit = ModelFamilyProfilePlane.Fit(points, out var spread);

        Assert.NotNull(fit);
        Assert.True(spread > 0.1 && spread < 0.3, spread.ToString());

        var flat = ModelFamilyProfilePlane.Fit(new List<double[]> { new double[] { 0, 0, 10 }, new double[] { 100, 0, 10 }, new double[] { 100, 50, 10 } }, out var none);
        Assert.Equal(new double[] { 0, 0, 1 }, flat!.Value.Normal);
        Assert.Equal(10, flat.Value.Origin[2], 9);
        Assert.Equal(0, none, 9);

        Assert.Null(ModelFamilyProfilePlane.Fit(new List<double[]> { new double[] { 0, 0, 0 }, new double[] { 1, 0, 0 }, new double[] { 2, 0, 0 } }, out _));
    }

    // ── Choosing loops for forms ─────────────────────────────────────────────

    private static ModelFamilyProfilePlan Plan(string formsJson, out List<string> problems, string? extra = null)
    {
        problems = new List<string>();
        var args = new Dictionary<string, object?> { ["forms"] = JToken.Parse(formsJson) };
        if (extra != null)
            foreach (var property in JObject.Parse(extra).Properties()) args[property.Name] = property.Value;
        return ModelFamilyProfilePlan.Parse(args, problems);
    }

    private static ModelFamilyProfileFormSpec Form(string json)
    {
        var plan = Plan($"[{json}]", out var problems);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        return plan.Forms[0];
    }

    [Fact]
    public void Auto_holes_are_the_loops_directly_inside_and_the_extrusion_passes_the_form_checks()
    {
        var analysis = Analyse(Rectangle(0, 0, 200, 100).Concat(Circle(100, 50, 20)));
        var problems = new List<string>();

        var choice = ModelFamilyProfileSelection.Choose(analysis, Form("{\"loop\": 0, \"depth\": 80, \"expected\": {\"width\": 200, \"height\": 100, \"centre\": [100, 50, 0]}}"), problems);

        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.Equal(new[] { 1 }, choice!.Holes.Select(h => h.Index));
        var extrusion = ModelFamilyProfileSelection.ToExtrusion(choice, null);
        Assert.Equal("forms[0]", extrusion.Source);
        Assert.Equal(80, extrusion.End);
        var planar = extrusion.Profile.OnPlane(Level, new ModelFamilyFormCheck(), problems);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.Equal(200, planar!.Width, 6);
        Assert.Equal(20000 - Math.PI * 400, planar.Area, 3);
    }

    [Fact]
    public void Holes_none_and_a_point_pick_work()
    {
        var analysis = Analyse(Rectangle(0, 0, 200, 100).Concat(Circle(100, 50, 20)));
        var problems = new List<string>();

        var outer = ModelFamilyProfileSelection.Choose(analysis, Form("{\"loop\": 0, \"holes\": \"none\", \"depth\": 10}"), problems);
        var cutout = ModelFamilyProfileSelection.Choose(analysis, Form("{\"loop\": {\"at\": [100, 50, 0]}, \"solid\": false, \"start\": -5, \"end\": 90}"), problems);

        Assert.Empty(problems);
        Assert.Empty(outer!.Holes);
        Assert.Equal(1, cutout!.Outer.Index);
        Assert.False(cutout.Spec.Solid);
    }

    [Theory]
    [InlineData("{\"loop\": 5, \"depth\": 10}", "there is no loop 5")]
    [InlineData("{\"loop\": {\"at\": [500, 500, 0]}, \"depth\": 10}", "no closed loop contains")]
    [InlineData("{\"loop\": 0, \"holes\": [0], \"depth\": 10}", "is the outer loop itself")]
    [InlineData("{\"loop\": 1, \"holes\": [0], \"depth\": 10}", "is not directly inside loop 1")]
    [InlineData("{\"loop\": 0, \"depth\": 10, \"expected\": {\"width\": 210}}", "measures 200 mm, expected 210")]
    [InlineData("{\"loop\": 0, \"depth\": 10, \"expected\": {\"centre\": [0, 0, 0]}}", "expected.centre")]
    public void A_bad_choice_is_refused_before_anything_is_created(string json, string message)
    {
        var analysis = Analyse(Rectangle(0, 0, 200, 100).Concat(Circle(100, 50, 20)));
        var problems = new List<string>();

        Assert.Null(ModelFamilyProfileSelection.Choose(analysis, Form(json), problems));
        Assert.Contains(problems, p => p.Contains(message));
    }

    [Fact]
    public void An_invalid_outline_is_refused_before_anything_is_created()
    {
        // The acceptance: "an invalid outline leaves the family unchanged". The create tool refuses in the pure
        // selection, before its first transaction, for an open outline (no loop), a self-crossing one and one with a
        // segment Revit cannot make.
        var problems = new List<string>();
        var open = Analyse(new[] { L(0, 0, 100, 0), L(100, 0, 100, 50), L(100, 50, 0, 50), L(0, 50, 0, 0.3) });
        Assert.Null(ModelFamilyProfileSelection.Choose(open, Form("{\"loop\": 0, \"depth\": 10}"), problems));
        Assert.Contains(problems, p => p.Contains("do not close") && p.Contains("0.3"));

        problems.Clear();
        var crossed = Analyse(new[] { L(0, 0, 20, 10), L(20, 10, 20, 0), L(20, 0, 0, 6), L(0, 6, 0, 0) });
        Assert.Null(ModelFamilyProfileSelection.Choose(crossed, Form("{\"loop\": 0, \"depth\": 10}"), problems));
        Assert.Contains(problems, p => p.Contains("crosses itself"));

        problems.Clear();
        var shortEdge = Analyse(new[] { L(0, 0, 100, 0), L(100, 0, 100, 99.7), L(100, 99.7, 99.7, 100), L(99.7, 100, 0, 100), L(0, 100, 0, 0) });
        Assert.Null(ModelFamilyProfileSelection.Choose(shortEdge, Form("{\"loop\": 0, \"depth\": 10}"), problems));
        Assert.Contains(problems, p => p.Contains("shorter than Revit's minimum"));
    }

    [Fact]
    public void Auto_holes_refuse_an_unusable_child()
    {
        var curves = Rectangle(0, 0, 200, 200).Concat(new[] { L(50, 50, 80, 70), L(80, 70, 80, 50), L(80, 50, 50, 62), L(50, 62, 50, 50) });
        var analysis = Analyse(curves);
        var problems = new List<string>();

        Assert.Null(ModelFamilyProfileSelection.Choose(analysis, Form("{\"loop\": 0, \"depth\": 10}"), problems));
        Assert.Contains(problems, p => p.Contains("holes") && p.Contains("\"none\""));
        problems.Clear();
        Assert.NotNull(ModelFamilyProfileSelection.Choose(analysis, Form("{\"loop\": 0, \"holes\": \"none\", \"depth\": 10}"), problems));
    }

    [Theory]
    [InlineData("[{\"loop\": 0}]", null, "depth is required")]
    [InlineData("[{\"loop\": 0, \"depth\": 10, \"cut\": 5}]", null, "only a void")]
    [InlineData("[{\"loop\": 0, \"depth\": 10, \"solid\": false, \"cut\": {\"created\": \"forms[3]\"}}]", null, "forms[3] does not exist")]
    [InlineData("[{\"loop\": 0, \"depth\": 10, \"solid\": false, \"cut\": {\"created\": \"forms[0]\"}}]", null, "cannot cut itself")]
    [InlineData("[{\"loop\": 0, \"depth\": 10, \"solid\": false}, {\"loop\": 1, \"depth\": 5, \"solid\": false, \"cut\": {\"created\": \"forms[0]\"}}]", null, "is a void")]
    [InlineData("[{\"loop\": 0, \"depth\": 10, \"colour\": 1}]", null, "unknown key")]
    [InlineData("[{\"loop\": -1, \"depth\": 10}]", null, "is not a loop index")]
    [InlineData("[{\"loop\": 0, \"depth\": 10, \"holes\": \"some\"}]", null, "\"auto\"")]
    [InlineData("[{\"loop\": 0, \"depth\": 10}]", "{\"removeSourceCurves\": true}", "only for family curves")]
    [InlineData("[{\"loop\": 0, \"depth\": 10}]", "{\"tolerance\": 2}", "at most 1")]
    [InlineData("[{\"loop\": 0, \"depth\": 10}]", "{\"source\": {\"import\": 1, \"curves\": [2]}}", "only one of import, curves or view")]
    [InlineData("[{\"loop\": 0, \"depth\": 10}]", "{\"source\": {\"curves\": []}}", "non-empty list")]
    [InlineData("[{\"loop\": 0, \"depth\": 10}]", "{\"source\": {\"view\": \"Ref. Level\", \"layers\": [\"A\"]}}", "only for an import")]
    public void Bad_arguments_are_problems(string forms, string? extra, string message)
    {
        Plan(forms, out var problems, extra);
        Assert.Contains(problems, p => p.Contains(message));
    }

    [Fact]
    public void A_good_plan_reads_every_argument()
    {
        var plan = Plan("[{\"loop\": 0, \"depth\": 80, \"name\": \"Enclosure\", \"subcategory\": \"Body\", \"visible\": true}, " +
                        "{\"loop\": {\"at\": [0, 0, 0]}, \"holes\": [2, 3], \"solid\": false, \"start\": -10, \"end\": 90, \"cut\": {\"created\": \"forms[0]\"}}]",
            out var problems, "{\"source\": {\"curves\": [11, 12, 12]}, \"tolerance\": 0.2, \"removeSourceCurves\": true, \"workPlane\": \"Front\"}");

        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.Equal(ModelFamilyProfileSourceKind.Curves, plan.Request.Source.Kind);
        Assert.Equal(new long[] { 11, 12 }, plan.Request.Source.CurveIds);
        Assert.Equal(0.2, plan.Request.Tolerance);
        Assert.Equal("Front", (string?)plan.Request.WorkPlane);
        Assert.True(plan.RemoveSourceCurves);
        Assert.Equal("Enclosure", plan.Forms[0].Name);
        Assert.Equal(ModelFamilyProfileHoles.List, plan.Forms[1].HolesMode);
        Assert.Equal(0, plan.Forms[1].CutCreated);
        Assert.Equal(-10, plan.Forms[1].Start);
        Assert.Equal("2 forms (1 solid, 1 void), 1 cut, source curves removed from 2 family curve(s)", plan.Describe());
    }

    [Fact]
    public void The_source_defaults_to_the_only_import_and_reads_layers()
    {
        var problems = new List<string>();
        Assert.Equal(ModelFamilyProfileSourceKind.Import, ModelFamilyProfileSourceSpec.Parse(null, problems)!.Kind);
        var source = ModelFamilyProfileSourceSpec.Parse(JToken.Parse("{\"import\": \"Enclosure.dwg\", \"layers\": [\"0\", \"CUT\"]}"), problems)!;
        Assert.Empty(problems);
        Assert.Equal(new[] { "0", "CUT" }, source.Layers);
        Assert.False(source.FamilyCurves);
    }

    // ── Tool surface ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("revit_get_model_family_profiles", "ReadOnly", "ModelFamilySession.Read")]
    [InlineData("revit_preview_create_model_family_form_from_profile", "ReadOnly", "ModelFamilySession.Edit")]
    [InlineData("revit_create_model_family_form_from_profile", "RequiresApproval", "ModelFamilySession.Edit")]
    public void The_profile_tools_match_on_both_sides(string tool, string permission, string session)
    {
        Assert.Equal("familyeditor", BridgeToolGroups.GroupOf(tool));
        Assert.Equal(permission, ToolSourceScan.Addin.Value[tool].Permission);
        var bridge = Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == tool);
        Assert.Equal(permission == "ReadOnly", bridge.ReadOnly);
        Assert.Contains(tool, bridge.Targets);
        Assert.Contains(session, ToolSourceScan.Addin.Value[tool].Source);
        Assert.Contains("ModelFamilyArgs(target, document)", File.ReadAllText(Path.Combine(ToolSourceScan.Root(), "RevitMCP.Bridge", "RevitMcpTools.ModelFamilyProfiles.cs")));
    }

    [Fact]
    public void The_create_tool_sends_every_argument_and_counts_as_creating()
    {
        var text = File.ReadAllText(Path.Combine(ToolSourceScan.Root(), "RevitMCP.Bridge", "RevitMcpTools.ModelFamilyProfiles.cs"));
        foreach (var key in new[] { ModelFamilyProfileRequest.SourceKey, ModelFamilyProfileRequest.WorkPlaneKey, ModelFamilyProfileRequest.ToleranceKey,
                     ModelFamilyProfileRequest.LimitKey, ModelFamilyProfilePlan.FormsKey, ModelFamilyProfilePlan.RemoveSourceCurvesKey })
            Assert.Contains($"args[\"{key}\"]", text);
        Assert.Contains("IApprovalSummaryTool", ToolSourceScan.Addin.Value["revit_create_model_family_form_from_profile"].Source);
        Assert.Equal(PlanToolKind.Creates, PlanToolClassification.Classify("revit_create_model_family_form_from_profile"));
    }

    [Fact]
    public void The_imports_section_is_registered()
    {
        var registration = ToolSourceScan.AddinClasses.Value.Single(c => c.Class == "ModelFamilyProfileTools").Body;
        Assert.Contains("AddSection(\"imports\"", registration);
        Assert.Contains("RegisterTool(new GetModelFamilyProfilesTool())", registration);
        Assert.Contains("RegisterTool(new PreviewCreateModelFamilyFormFromProfileTool())", registration);
        Assert.Contains("RegisterTool(new CreateModelFamilyFormFromProfileTool())", registration);
    }
}
