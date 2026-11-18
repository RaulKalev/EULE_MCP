using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelFamilies;
using RevitMCP.Addin.Plans;
using RevitMCP.Bridge;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>Solid and void forms (#171): the profile model, the operation plan, face labels, size checks and the tool surface.</summary>
public class ModelFamilyFormTests
{
    private static readonly ModelFamilyFormCheck Check = new();

    /// <summary>The reference level: the family's XY plane.</summary>
    private static readonly ModelFamilyFormFrame Level = ModelFamilyFormFrame.XY;

    /// <summary>A front-facing plane through the origin (x along family X, y along family Z), like Center (Front/Back).</summary>
    private static readonly ModelFamilyFormFrame Front = new(new double[] { 0, 0, 0 }, new double[] { 1, 0, 0 }, new double[] { 0, 0, 1 });

    private static ModelFamilyFormPlanarProfile? OnPlane(string json, ModelFamilyFormFrame frame, List<string> problems, bool singleLoop = false)
    {
        var profile = ModelFamilyFormProfile.Parse(JToken.Parse(json), "profile", problems);
        return profile?.OnPlane(frame, Check, problems, singleLoop);
    }

    private static ModelFamilyFormPlanarProfile Valid(string json, ModelFamilyFormFrame? frame = null)
    {
        var problems = new List<string>();
        var result = OnPlane(json, frame ?? Level, problems);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        return result!;
    }

    private static List<string> Problems(string json, ModelFamilyFormFrame? frame = null, bool singleLoop = false)
    {
        var problems = new List<string>();
        var result = OnPlane(json, frame ?? Level, problems, singleLoop);
        Assert.NotEmpty(problems);
        Assert.Null(result);
        return problems;
    }

    // ── Loops ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_points_loop_is_a_closed_polygon_with_measured_size()
    {
        var profile = Valid("[{\"points\": [[0,0,0],[100,0,0],[100,50,0],[0,50,0]]}]");
        Assert.Single(profile.Loops);
        Assert.Equal(4, profile.Outer.Uv.Count);
        Assert.Equal(100, profile.Width, 6);
        Assert.Equal(50, profile.Height, 6);
        Assert.Equal(5000, profile.Area, 6);
        Assert.True(profile.Outer.Area > 0);   // counter-clockwise
        // The last edge closes back to the first point.
        Assert.Equal(new double[] { 0, 0, 0 }, profile.Outer.World[3].End);
    }

    [Fact]
    public void A_repeated_first_point_is_dropped_and_too_few_points_are_refused()
    {
        var profile = Valid("{\"points\": [[0,0],[10,0],[10,10],[0,10],[0,0]]}");
        Assert.Equal(4, profile.Outer.Uv.Count);
        Assert.Contains(Problems("{\"points\": [[0,0],[10,0]]}"), p => p.Contains("at least 3 points"));
    }

    [Fact]
    public void A_plane_square_to_the_family_axes_gets_family_aligned_axes_whatever_revit_chose()
    {
        // The level plane as Plane.CreateByNormalAndOrigin may return it: x and y rotated 30° about Z (documented as arbitrary).
        var c = Math.Cos(Math.PI / 6);
        var s = Math.Sin(Math.PI / 6);
        var level = ModelFamilyFormFrame.AlignedToFamily(new double[] { 0, 0, 0 }, new double[] { 0, 0, 1 }, new[] { c, s, 0 }, new[] { -s, c, 0 });
        var profile = Valid("{\"rectangle\": {\"min\": [-100, -50, 0], \"max\": [100, 50, 0]}}", level);
        Assert.Equal(200, profile.Width, 6);
        Assert.Equal(100, profile.Height, 6);
        Assert.Equal(new double[] { -100, -50, 0 }, profile.Outer.World[0].Start.Select(v => Math.Round(v, 9)).ToArray());
        Assert.Equal(new double[] { 0, 0, 1 }, level.Normal.Select(v => Math.Round(v, 9)).ToArray());

        // A plane facing −Y keeps its normal; x runs along family X and y along family Z.
        var front = ModelFamilyFormFrame.AlignedToFamily(new double[] { 0, -50, 0 }, new double[] { 0, -1, 0 }, new double[] { 0, 0, 1 }, new double[] { 1, 0, 0 });
        Assert.Equal(new double[] { 1, 0, 0 }, front.XAxis.Select(v => Math.Round(v, 9)).ToArray());
        Assert.Equal(new double[] { 0, 0, 1 }, front.YAxis.Select(v => Math.Round(v, 9)).ToArray());
        Assert.Equal(new double[] { 0, -1, 0 }, front.Normal.Select(v => Math.Round(v, 9)).ToArray());

        // A tilted plane keeps the axes it was given.
        var n = new[] { 0, -Math.Sqrt(0.5), Math.Sqrt(0.5) };
        var tilted = ModelFamilyFormFrame.AlignedToFamily(new double[] { 0, 0, 0 }, n, new double[] { 1, 0, 0 }, new[] { 0, Math.Sqrt(0.5), Math.Sqrt(0.5) });
        Assert.Equal(new double[] { 1, 0, 0 }, tilted.XAxis);
    }

    [Fact]
    public void A_rectangle_follows_the_work_plane_axes()
    {
        var level = Valid("[{\"rectangle\": {\"min\": [-50,-25,0], \"max\": [50,25,0]}}]");
        Assert.Equal(100, level.Width, 6);
        Assert.Equal(50, level.Height, 6);

        // On the front plane the rectangle spans family X and Z.
        var front = Valid("[{\"rectangle\": {\"min\": [0,0,0], \"max\": [120,0,80]}}]", Front);
        Assert.Equal(120, front.Width, 6);
        Assert.Equal(80, front.Height, 6);
        Assert.All(front.Outer.World, s => Assert.Equal(0, s.Start[1], 9));
        Assert.Contains(front.Outer.World, s => Math.Abs(s.Start[2] - 80) < 1e-9);
    }

    [Fact]
    public void A_rectangle_off_the_plane_or_without_width_is_a_problem()
    {
        Assert.Contains(Problems("[{\"rectangle\": {\"min\": [0,0,0], \"max\": [100,10,50]}}]", Front), p => p.Contains("rectangle.max") && p.Contains("10 mm off the work plane"));
        Assert.Contains(Problems("[{\"rectangle\": {\"min\": [0,0,0], \"max\": [100,0,0]}}]"), p => p.Contains("both sides must be at least"));
    }

    [Fact]
    public void A_circle_becomes_two_half_arcs()
    {
        var profile = Valid("[{\"circle\": {\"center\": [10,20,0], \"radius\": 30}}]");
        var loop = profile.Outer;
        Assert.Equal(2, loop.Uv.Count);
        Assert.All(loop.Uv, s => Assert.Equal(ModelFamilyFormSegmentKind.Arc, s.Kind));
        Assert.Equal(Math.PI * 900, Math.Abs(loop.Area), 6);
        Assert.Equal(60, profile.Width, 6);
        Assert.Equal(60, profile.Height, 6);
        Assert.Equal(-20, loop.Min[0], 6);
        Assert.Equal(50, loop.Max[1], 6);
        Assert.Contains(Problems("[{\"circle\": {\"center\": [0,0,0], \"radius\": 0}}]"), p => p.Contains("radius: must be greater than 0"));
    }

    [Fact]
    public void Segments_chain_and_close_with_lines_and_arcs()
    {
        // A 100 × 50 slot with a round right end: line, arc, line, line.
        var profile = Valid("[{\"segments\": [" +
                            "{\"line\": {\"start\": [0,0,0], \"end\": [100,0,0]}}," +
                            "{\"arc\": {\"start\": [100,0,0], \"mid\": [125,25,0], \"end\": [100,50,0]}}," +
                            "{\"line\": {\"start\": [100,50,0], \"end\": [0,50,0]}}," +
                            "{\"line\": {\"start\": [0,50,0], \"end\": [0,0,0]}}]}]");
        Assert.Equal(125, profile.Width, 6);
        Assert.Equal(50, profile.Height, 6);
        Assert.Equal(5000 + Math.PI * 625 / 2, profile.Area, 6);
    }

    [Fact]
    public void A_gap_within_the_tolerance_is_snapped_and_a_larger_one_is_a_problem()
    {
        var snapped = Valid("[{\"segments\": [{\"line\": {\"start\": [0,0], \"end\": [10,0]}}, {\"line\": {\"start\": [10.005,0], \"end\": [10,10]}}, {\"line\": {\"start\": [10,10], \"end\": [0,0]}}]}]");
        Assert.Equal(snapped.Outer.Uv[0].End, snapped.Outer.Uv[1].Start);

        var gap = Problems("[{\"segments\": [{\"line\": {\"start\": [0,0], \"end\": [10,0]}}, {\"line\": {\"start\": [10.5,0], \"end\": [10,10]}}, {\"line\": {\"start\": [10,10], \"end\": [0,0]}}]}]");
        Assert.Contains(gap, p => p.Contains("segment 0 ends") && p.Contains("gap 0.5 mm"));

        var open = Problems("[{\"segments\": [{\"line\": {\"start\": [0,0], \"end\": [10,0]}}, {\"line\": {\"start\": [10,0], \"end\": [10,10]}}, {\"line\": {\"start\": [10,10], \"end\": [0,5]}}]}]");
        Assert.Contains(open, p => p.Contains("does not close") && p.Contains("5 mm"));
    }

    [Fact]
    public void Segments_shorter_than_Revits_tolerance_are_reported_with_their_length()
    {
        var problems = Problems("{\"points\": [[0,0],[10,0],[10,10],[0.4,10],[0,10]]}");
        Assert.Contains(problems, p => p.Contains("segment 3 is 0.4 mm long") && p.Contains("0.79 mm"));
    }

    [Fact]
    public void Points_close_to_the_plane_are_projected_and_far_ones_are_problems()
    {
        var projected = Valid("{\"points\": [[0,0,0.3],[10,0,-0.2],[10,10,0],[0,10,0.4]]}");
        Assert.All(projected.Outer.World, s => Assert.Equal(0, s.Start[2], 9));

        var problems = Problems("{\"points\": [[0,0,0],[10,0,0],[10,10,0.6],[0,10,0]]}");
        Assert.Contains(problems, p => p.Contains("points[2]") && p.Contains("0.6 mm off the work plane") && p.Contains("wrong workPlane"));
    }

    [Fact]
    public void Self_intersecting_and_zero_area_loops_are_refused()
    {
        Assert.Contains(Problems("{\"points\": [[0,0],[10,10],[10,0],[0,20]]}"), p => p.Contains("crosses itself"));
        Assert.Contains(Problems("{\"points\": [[0,0],[10,0],[20,0]]}"), p => p.Contains("encloses no area"));
    }

    [Fact]
    public void Holes_must_lie_inside_the_outer_loop_without_touching_it()
    {
        const string outer = "{\"rectangle\": {\"min\": [0,0], \"max\": [100,100]}}";
        var withHole = Valid($"[{outer}, {{\"circle\": {{\"center\": [50,50], \"radius\": 10}}}}]");
        Assert.Equal(2, withHole.Loops.Count);
        Assert.Equal(10000 - Math.PI * 100, withHole.Area, 6);

        Assert.Contains(Problems($"[{outer}, {{\"rectangle\": {{\"min\": [200,0], \"max\": [210,10]}}}}]"), p => p.Contains("lies outside the outer loop"));
        Assert.Contains(Problems($"[{outer}, {{\"rectangle\": {{\"min\": [90,10], \"max\": [110,20]}}}}]"), p => p.Contains("crosses or touches the outer loop"));
        Assert.Contains(Problems($"[{{\"rectangle\": {{\"min\": [40,40], \"max\": [60,60]}}}}, {outer}]"), p => p.Contains("put the outer loop first"));
        Assert.Contains(Problems($"[{outer}, {{\"rectangle\": {{\"min\": [10,10], \"max\": [30,30]}}}}, {{\"rectangle\": {{\"min\": [20,20], \"max\": [40,40]}}}}]"), p => p.Contains("loops must not intersect"));
    }

    [Fact]
    public void Single_loop_profiles_refuse_holes()
    {
        Assert.Contains(Problems("[{\"rectangle\": {\"min\": [0,0], \"max\": [100,100]}}, {\"circle\": {\"center\": [50,50], \"radius\": 10}}]", singleLoop: true),
            p => p.Contains("exactly one loop"));
    }

    [Fact]
    public void Loop_syntax_problems_name_their_source()
    {
        var problems = new List<string>();
        ModelFamilyFormProfile.Parse(JToken.Parse("[{\"points\": [[0,0],[1,0],[1,1]], \"rectangle\": {}}, {\"poly\": []}, {\"segments\": [{\"line\": {\"start\": [0,0]}}]}]"), "extrusions[0].profile", problems);
        Assert.Contains(problems, p => p.StartsWith("extrusions[0].profile[0]: give only one of points, rectangle"));
        Assert.Contains(problems, p => p.StartsWith("extrusions[0].profile[1].poly: unknown key"));
        Assert.Contains(problems, p => p.StartsWith("extrusions[0].profile[2].segments[0].line.end is required"));
        Assert.Contains(Problems("{\"segments\": [{\"arc\": {\"start\": [0,0], \"mid\": [5,0], \"end\": [10,0]}}, {\"line\": {\"start\": [10,0], \"end\": [0,0]}}]}"),
            p => p.Contains("three points are on one line"));
    }

    // ── Planes and maths ─────────────────────────────────────────────────────

    [Fact]
    public void A_frame_maps_family_points_to_plane_coordinates_and_back()
    {
        var frame = new ModelFamilyFormFrame(new double[] { 10, 20, 30 }, new double[] { 0, 2, 0 }, new double[] { 0, 0.5, 1 });
        Assert.Equal(new double[] { 0, 1, 0 }, frame.XAxis);
        Assert.Equal(new double[] { 0, 0, 1 }, frame.YAxis);   // made perpendicular to x
        Assert.Equal(new double[] { 1, 0, 0 }, frame.Normal);
        var uv = frame.ToUv(new double[] { 13, 25, 40 });
        Assert.Equal(5, uv[0], 9);
        Assert.Equal(10, uv[1], 9);
        Assert.Equal(3, frame.OffsetOf(new double[] { 13, 25, 40 }), 9);
        Assert.Equal(new double[] { 10, 25, 40 }, frame.ToWorld(uv));
        Assert.Equal(new double[] { 12, 25, 40 }, frame.ToWorld(uv, 2));
    }

    [Fact]
    public void Arcs_have_a_signed_sweep_exact_area_and_bounds()
    {
        var ccw = ModelFamilyFormGeometry.Arc2D(new double[] { 10, 0, 0 }, new double[] { 0, 10, 0 }, new double[] { -10, 0, 0 })!.Value;
        Assert.Equal(10, ccw.Radius, 9);
        Assert.Equal(Math.PI, ccw.Sweep, 9);
        var cw = ModelFamilyFormGeometry.Arc2D(new double[] { 10, 0, 0 }, new double[] { 0, -10, 0 }, new double[] { -10, 0, 0 })!.Value;
        Assert.Equal(-Math.PI, cw.Sweep, 9);
        Assert.Null(ModelFamilyFormGeometry.Arc2D(new double[] { 0, 0, 0 }, new double[] { 1, 0, 0 }, new double[] { 2, 0, 0 }));

        // A quarter arc from east to north reaches x = 10 and y = 10, nothing beyond.
        var quarter = ModelFamilyFormSegment.Arc(new double[] { 10, 0, 0 }, new double[] { Math.Sqrt(50), Math.Sqrt(50), 0 }, new double[] { 0, 10, 0 });
        var (min, max) = ModelFamilyFormGeometry.Bounds(new[] { quarter });
        Assert.Equal(0, min[0], 9);
        Assert.Equal(10, max[0], 9);
        Assert.Equal(10, max[1], 9);
        Assert.Equal(Math.PI * 5, ModelFamilyFormGeometry.Length2D(quarter), 9);

        // A half circle closed by its diameter: area πr²/2, either way round.
        var half = new[]
        {
            ModelFamilyFormSegment.Arc(new double[] { 10, 0, 0 }, new double[] { 0, 10, 0 }, new double[] { -10, 0, 0 }),
            ModelFamilyFormSegment.Line(new double[] { -10, 0, 0 }, new double[] { 10, 0, 0 })
        };
        Assert.Equal(Math.PI * 50, ModelFamilyFormGeometry.SignedArea(half), 9);
    }

    [Fact]
    public void A_3D_arc_is_measured_through_its_three_points()
    {
        var arc = ModelFamilyFormSegment.Arc(new double[] { 0, 0, 10 }, new double[] { 0, 10, 0 }, new double[] { 0, 0, -10 });
        Assert.Equal(Math.PI * 10, ModelFamilyFormGeometry.Length3D(arc), 6);
    }

    [Fact]
    public void A_revolve_profile_may_touch_but_not_cross_its_axis()
    {
        var touching = Valid("{\"rectangle\": {\"min\": [0,0], \"max\": [20,50]}}");
        Assert.False(touching.CrossesLine(new double[] { 0, 0, 0 }, new double[] { 0, 100, 0 }));
        Assert.True(touching.CrossesLine(new double[] { 10, 0, 0 }, new double[] { 10, 100, 0 }));
    }

    // ── Paths and sweep profiles ─────────────────────────────────────────────

    [Fact]
    public void A_path_may_be_open_or_closed_and_lies_on_its_plane()
    {
        var problems = new List<string>();
        var open = ModelFamilyFormPath.Parse(JToken.Parse("[{\"line\": {\"start\": [0,0,0], \"end\": [100,0,0]}}, {\"arc\": {\"start\": [100,0,0], \"mid\": [135.355,14.645,0], \"end\": [150,50,0]}}]"), "sweeps[0].path", problems)!
            .OnPlane(Level, Check, problems)!;
        Assert.Empty(problems);
        Assert.False(open.Closed);
        Assert.Equal(100 + Math.PI * 25, open.Length, 2);

        var closed = ModelFamilyFormPath.Parse(JToken.Parse("{\"segments\": [{\"line\": {\"start\": [0,0], \"end\": [10,0]}}, {\"line\": {\"start\": [10,0], \"end\": [10,10]}}, {\"line\": {\"start\": [10,10], \"end\": [0,0]}}]}"), "p", problems)!
            .OnPlane(Level, Check, problems)!;
        Assert.True(closed.Closed);

        var single = ModelFamilyFormPath.Parse(JToken.Parse("{\"line\": {\"start\": [0,0,5], \"end\": [10,0,5]}}"), "p", problems)!;
        Assert.Null(single.OnPlane(Level, Check, problems));
        Assert.Contains(problems, p => p.Contains("5 mm off the work plane"));

        problems.Clear();
        var twoLines = ModelFamilyFormPath.Parse(JToken.Parse("[{\"line\": {\"start\": [0,0], \"end\": [10,0]}}, {\"line\": {\"start\": [10,0], \"end\": [10,10]}}]"), "p", problems)!;
        Assert.Null(twoLines.OnPlane(Level, Check, problems, singleCurve: true));
        Assert.Contains(problems, p => p.Contains("must be one curve"));
    }

    [Fact]
    public void A_sweep_profile_is_read_in_its_own_uv_coordinates()
    {
        var plan = Plan(("sweeps", "[{\"path\": [{\"line\": {\"start\": [0,0,0], \"end\": [0,500,0]}}], \"profile\": {\"circle\": {\"center\": [0,0], \"radius\": 15}}, \"profilePlane\": \"mid\"}]"));
        var sweep = Assert.Single(plan.Sweeps);
        Assert.Equal("middle", sweep.ProfilePlane);
        var problems = new List<string>();
        var profile = sweep.Profile.OnPlane(ModelFamilyFormFrame.XY, Check, problems)!;
        Assert.Empty(problems);
        Assert.Equal(30, profile.Width, 6);
        Assert.All(profile.Outer.Uv, s => Assert.Equal(0, s.Start[2]));
    }

    // ── The operation plan ───────────────────────────────────────────────────

    private static ModelFamilyFormPlan Plan(params (string Key, string Json)[] values) => Plan(out var problems, values) is var plan && problems.Count == 0
        ? plan
        : throw new Xunit.Sdk.XunitException(string.Join("\n", problems));

    private static ModelFamilyFormPlan Plan(out List<string> problems, params (string Key, string Json)[] values)
    {
        var args = values.ToDictionary(v => v.Key, v => (object?)JToken.Parse(v.Json));
        problems = new List<string>();
        return ModelFamilyFormPlan.Parse(args, problems);
    }

    private const string Box = "{\"rectangle\": {\"min\": [-100,-50,0], \"max\": [100,50,0]}}";

    [Fact]
    public void Operations_run_in_a_documented_order()
    {
        Assert.Equal(new[] { "remove", "extrusions", "blends", "revolves", "sweeps", "sweptBlends", "edit", "cuts", "joins" }, ModelFamilyFormPlan.Order);
        var plan = Plan(("extrusions", $"[{{\"profile\": {Box}, \"depth\": 80}}, {{\"profile\": {Box}, \"start\": 10, \"end\": 20, \"solid\": false}}]"),
                        ("revolves", $"[{{\"profile\": {{\"rectangle\": {{\"min\": [0,0], \"max\": [10,20]}}}}, \"axis\": {{\"start\": [0,0,0], \"end\": [0,100,0]}}}}]"));
        Assert.Equal(new[] { "extrusions[0]", "extrusions[1]", "revolves[0]" }, plan.Forms.Select(f => f.Source));
        Assert.Equal(0, plan.Extrusions[0].Start);
        Assert.Equal(80, plan.Extrusions[0].End);
        Assert.False(plan.Extrusions[1].Solid);
        Assert.Equal(360, plan.Revolves[0].EndAngle);
        Assert.Equal("2 extrusions, 1 revolve", plan.Describe());
    }

    [Fact]
    public void Extrusion_offsets_take_depth_or_start_and_end()
    {
        Plan(out var both, ("extrusions", $"[{{\"profile\": {Box}, \"depth\": 80, \"end\": 80}}]"));
        Assert.Contains(both, p => p.Contains("give depth, or start and end, not both"));
        Plan(out var none, ("extrusions", $"[{{\"profile\": {Box}}}]"));
        Assert.Contains(none, p => p.Contains("extrusions[0].end is required"));
        Plan(out var equal, ("extrusions", $"[{{\"profile\": {Box}, \"start\": 20, \"end\": 20}}]"));
        Assert.Contains(equal, p => p.Contains("must differ"));
        Plan(out var unknown, ("extrusions", $"[{{\"profile\": {Box}, \"depth\": 10, \"hieght\": 5}}]"));
        Assert.Contains(unknown, p => p.StartsWith("extrusions[0].hieght: unknown key"));
    }

    [Fact]
    public void Blends_and_swept_blends_take_one_loop_per_profile()
    {
        Plan(out var problems, ("blends", $"[{{\"bottomProfile\": [{Box}, {Box}], \"topProfile\": {Box}, \"depth\": 50}}]"),
                               ("sweptBlends", $"[{{\"path\": [{{\"line\": {{\"start\": [0,0], \"end\": [0,100]}}}}, {{\"line\": {{\"start\": [0,100], \"end\": [50,100]}}}}], \"bottomProfile\": {Box}, \"topProfile\": {Box}}}]"));
        Assert.Contains(problems, p => p.StartsWith("blends[0].bottomProfile: a blend takes exactly one loop"));
        Assert.Contains(problems, p => p.StartsWith("sweptBlends[0].path: a swept blend follows one curve"));
    }

    [Theory]
    [InlineData(0, 0, "must turn through an angle")]
    [InlineData(-10, 400, "between -360 and 360")]
    [InlineData(-200, 200, "more than a full turn")]
    public void Revolve_angles_are_checked(double start, double end, string expected)
    {
        Plan(out var problems, ("revolves", $"[{{\"profile\": {Box}, \"axis\": {{\"start\": [0,0,0], \"end\": [0,100,0]}}, \"startAngle\": {start}, \"endAngle\": {end}}}]"));
        Assert.Contains(problems, p => p.Contains(expected));
        Assert.True(ModelFamilyFormPlan.CheckAngles(-90, 90, "r", new List<string>()));
    }

    [Fact]
    public void Sweeps_check_the_profile_plane_and_curve()
    {
        Plan(out var problems, ("sweeps", "[{\"path\": [{\"line\": {\"start\": [0,0], \"end\": [100,0]}}], \"profile\": {\"circle\": {\"center\": [0,0], \"radius\": 5}}, \"profilePlane\": \"top\", \"profileCurve\": 3}]"));
        Assert.Contains(problems, p => p.Contains("profilePlane: 'top' is not one of start, middle, end"));
        Assert.Contains(problems, p => p.Contains("profileCurve: must be the index of a path segment (0 to 0)"));
        Plan(out var noAxis, ("revolves", $"[{{\"profile\": {Box}}}]"));
        Assert.Contains(noAxis, p => p.StartsWith("revolves[0].axis is required"));
    }

    [Fact]
    public void One_call_can_build_an_enclosure_and_cut_it()
    {
        var plan = Plan(("extrusions", $"[{{\"profile\": {Box}, \"depth\": 80}}, {{\"profile\": {{\"circle\": {{\"center\": [0,0,0], \"radius\": 10}}}}, \"start\": -10, \"end\": 90, \"solid\": false}}]"),
                        ("cuts", "[{\"void\": {\"created\": \"Extrusions[1]\"}, \"solid\": {\"created\": \"extrusions[0]\"}}]"),
                        ("edit", "[{\"form\": {\"created\": \"extrusions[0]\"}, \"end\": 100}]"));
        var cut = Assert.Single(plan.Cuts);
        Assert.Equal("extrusions[1]", cut.Void.Created);
        Assert.False(cut.Remove);
        var created = new Dictionary<string, long> { ["extrusions[0]"] = 501, ["extrusions[1]"] = 502 };
        Assert.Equal(502, cut.Void.Resolve(created));
        Assert.Equal(501, cut.Solid.Resolve(created));
        Assert.Equal(501, plan.Edits[0].Form.Resolve(created));
        Assert.Equal(100, plan.Edits[0].End);
        Assert.Null(new ModelFamilyFormRef { Created = "blends[0]" }.Resolve(created));
        Assert.Equal(77, new ModelFamilyFormRef { Id = 77 }.Resolve(created));
    }

    [Fact]
    public void Created_references_must_exist_and_cannot_be_removed()
    {
        Plan(out var problems, ("extrusions", $"[{{\"profile\": {Box}, \"depth\": 80}}]"),
                               ("cuts", "[{\"void\": {\"created\": \"extrusions[1]\"}, \"solid\": {\"created\": \"extrusions[0]\"}}]"),
                               ("joins", "[{\"elements\": [{\"created\": \"walls[0]\"}, 12]}]"),
                               ("remove", "[{\"created\": \"extrusions[0]\"}, \"abc\"]"));
        Assert.Contains(problems, p => p.StartsWith("cuts[0].void: extrusions[1] does not exist; this call has 1 extrusions entry"));
        Assert.Contains(problems, p => p.StartsWith("joins[0].elements[0].created: 'walls[0]' is not a form list entry"));
        Assert.Contains(problems, p => p.StartsWith("remove[0]: remove runs before anything is created"));
        Assert.Contains(problems, p => p.StartsWith("remove[1]: '\"abc\"' is not an element id"));
    }

    [Fact]
    public void Remove_takes_ids_or_form_objects()
    {
        var plan = Plan(("remove", "[12, {\"form\": 13}, \"14\"]"));
        Assert.Equal(new long?[] { 12, 13, 14 }, plan.Remove.Select(r => r.Id));
        Assert.Equal("3 forms removed", plan.Describe());
    }

    [Fact]
    public void Edits_need_a_change_and_refuse_profile_replacement()
    {
        Plan(out var profile, ("edit", $"[{{\"form\": 12, \"profile\": {Box}}}]"));
        Assert.Contains(profile, p => p.StartsWith("edit[0].profile: replacing a form's profile is not supported") && p.Contains("SketchEditScope") && p.Contains("Remove the form and create it again"));
        Plan(out var nothing, ("edit", "[{\"form\": 12}]"));
        Assert.Contains(nothing, p => p.Contains("nothing to change"));
        Plan(out var angles, ("edit", "[{\"form\": 12, \"startAngle\": 30, \"endAngle\": 30}]"));
        Assert.Contains(angles, p => p.Contains("must turn through an angle"));
        var ok = Plan(("edit", "[{\"form\": 12, \"visible\": false, \"subcategory\": \"Housing\"}]"));
        Assert.False(ok.Edits[0].Visible);
        Assert.Equal("Housing", ok.Edits[0].Subcategory);
    }

    [Fact]
    public void Cuts_and_joins_check_their_shape()
    {
        Plan(out var same, ("cuts", "[{\"void\": 12, \"solid\": 12}]"));
        Assert.Contains(same, p => p.Contains("void and solid are the same form"));
        Plan(out var single, ("joins", "[{\"elements\": [12]}]"));
        Assert.Contains(single, p => p.Contains("a join needs at least two forms"));
        Plan(out var mixed, ("joins", "[{\"combination\": 40, \"elements\": [1, 2]}]"));
        Assert.Contains(mixed, p => p.Contains("combination is only for unjoin=true"));
        var unjoin = Plan(("joins", "[{\"combination\": 40, \"unjoin\": true}]"), ("cuts", "[{\"void\": 5, \"solid\": 6, \"remove\": true}]"));
        Assert.Equal(40, unjoin.Joins[0].Combination!.Id);
        Assert.True(unjoin.Cuts[0].Remove);
        Assert.Equal("1 cut removed, 1 unjoin", unjoin.Describe());
    }

    [Fact]
    public void An_empty_call_has_nothing_to_do()
    {
        Plan(out var problems);
        Assert.Contains(problems, p => p.StartsWith("Nothing to do: give at least one of remove, extrusions"));
    }

    // ── Faces and size checks ────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0, 1, "top")]
    [InlineData(0, 0, -1, "bottom")]
    [InlineData(-1, 0, 0, "left")]
    [InlineData(1, 0, 0, "right")]
    [InlineData(0, -1, 0, "front")]
    [InlineData(0, 1, 0, "back")]
    [InlineData(0.01, 0, 1, "top")]       // 0.57° off
    [InlineData(0.035, 0, 1, null)]       // 2° off
    [InlineData(1, 1, 0, null)]
    public void Faces_are_labelled_by_their_outward_normal(double x, double y, double z, string? expected)
    {
        Assert.Equal(expected, ModelFamilyFormFaceLabels.LabelOf(x, y, z));
    }

    [Fact]
    public void Face_labels_round_trip_to_normals()
    {
        foreach (var label in ModelFamilyFormFaceLabels.Labels)
            Assert.Equal(label, ModelFamilyFormFaceLabels.LabelOf(ModelFamilyFormFaceLabels.NormalOf(label)));
        Assert.True(ModelFamilyFormFaceLabels.IsLabel(" Top "));
        Assert.False(ModelFamilyFormFaceLabels.IsLabel("side"));
    }

    [Fact]
    public void An_extrusion_is_measured_against_its_request()
    {
        // 200 × 100 on the level, 80 deep: the box matches.
        Assert.Empty(ModelFamilyFormMeasure.CompareExtrusion(Level, 200, 100, 80, new double[] { -100, -50, 0 }, new double[] { 100, 50, 80 })!);
        // On the front plane width runs along X, height along Z and depth along Y.
        var mismatch = ModelFamilyFormMeasure.CompareExtrusion(Front, 200, 100, -30, new double[] { -100, 0, 0 }, new double[] { 100, 30, 100.5 })!;
        var only = Assert.Single(mismatch);
        Assert.StartsWith("height", only.Name);
        Assert.Equal(100.5, only.Measured);
        Assert.Contains("requested 100 mm", only.ToString());
        // A tilted plane cannot be measured with an axis-aligned box.
        var tilted = new ModelFamilyFormFrame(new double[] { 0, 0, 0 }, new double[] { 1, 1, 0 }, new double[] { 0, 0, 1 });
        Assert.Null(ModelFamilyFormMeasure.CompareExtrusion(tilted, 10, 10, 10, new double[] { 0, 0, 0 }, new double[] { 1, 1, 1 }));
    }

    // ── Tool surface ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("revit_preview_edit_model_family_forms", "ReadOnly")]
    [InlineData("revit_edit_model_family_forms", "RequiresApproval")]
    public void The_form_tools_match_on_both_sides(string tool, string permission)
    {
        Assert.Equal("familyeditor", BridgeToolGroups.GroupOf(tool));
        Assert.Equal(permission, ToolSourceScan.Addin.Value[tool].Permission);
        var bridge = Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == tool);
        Assert.Equal(permission == "ReadOnly", bridge.ReadOnly);
        Assert.Contains(tool, bridge.Targets);
        Assert.Contains("SendEditModelFamilyForms", bridge.Source);
        Assert.Contains("ModelFamilySession.Edit", ToolSourceScan.Addin.Value[tool].Source);
    }

    [Fact]
    public void The_edit_sends_every_operation_list_and_counts_as_creating()
    {
        var edit = Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == "revit_edit_model_family_forms").Source;
        foreach (var key in ModelFamilyFormPlan.Order)
            Assert.Contains($"args[\"{key}\"]", edit);
        Assert.Contains("ModelFamilyArgs(target, document)", edit);
        Assert.Contains("IApprovalSummaryTool", ToolSourceScan.Addin.Value["revit_edit_model_family_forms"].Source);
        Assert.Equal(PlanToolKind.Creates, PlanToolClassification.Classify("revit_edit_model_family_forms"));
    }

    [Fact]
    public void The_forms_and_combinations_sections_are_registered()
    {
        var registration = ToolSourceScan.AddinClasses.Value.Single(c => c.Class == "ModelFamilyFormTools").Body;
        Assert.Contains("AddSection(\"forms\"", registration);
        Assert.Contains("AddSection(\"combinations\"", registration);
        Assert.Contains("RegisterTool(new PreviewEditModelFamilyFormsTool())", registration);
        Assert.Contains("RegisterTool(new EditModelFamilyFormsTool())", registration);
    }
}
