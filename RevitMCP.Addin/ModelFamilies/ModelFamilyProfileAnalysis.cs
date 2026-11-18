using Newtonsoft.Json.Linq;
using static RevitMCP.Addin.ModelFamilies.ModelFamilyFormGeometry;

namespace RevitMCP.Addin.ModelFamilies;

/// <summary>The tolerances of a profile analysis (#177), in millimetres.</summary>
public sealed class ModelFamilyProfileOptions
{
    public const double DefaultToleranceMm = 0.05;
    public const double MaxToleranceMm = 1;

    /// <summary>Endpoints closer than this are one vertex.</summary>
    public double ToleranceMm { get; set; } = DefaultToleranceMm;
    /// <summary>Revit refuses shorter curves: a loop with one cannot be built.</summary>
    public double MinSegmentMm { get; set; } = ModelFamilyFormCheck.RevitShortCurveMm;
    /// <summary>A curve point further than this from the work plane leaves the curve out (an issue).</summary>
    public double OffPlaneMm { get; set; } = 0.5;
    /// <summary>A loop enclosing less than this (mm²) has no area.</summary>
    public double MinAreaMm2 { get; set; } = 0.01;
    /// <summary>Overlap and crossing checks compare every pair of segments; above this many they are skipped (an issue).</summary>
    public int MaxPairCheckSegments { get; set; } = 4000;
}

/// <summary>Something the analysis found that the user should know before building from the curves.</summary>
public sealed class ModelFamilyProfileIssue
{
    /// <summary>
    /// <c>offPlane</c>, <c>zeroLength</c>, <c>duplicate</c>, <c>overlap</c>, <c>crossing</c>, <c>endpointOnSegment</c>,
    /// <c>branch</c>, <c>bridge</c>, <c>shortSegment</c>, <c>selfIntersecting</c>, <c>zeroArea</c>, <c>approximated</c>,
    /// <c>pairChecksSkipped</c>.
    /// </summary>
    public string Kind { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    /// <summary>Plane coordinates of where it is, when it has a place.</summary>
    public double[]? At { get; init; }
    public List<long> SourceIds { get; init; } = new();
    public List<string> Layers { get; init; } = new();
    /// <summary>Loops it concerns (filled in once loops have their indices).</summary>
    public List<int> Loops { get; } = new();
    internal int? Vertex { get; init; }
    internal List<int> Segments { get; init; } = new();

    public JObject ToJson(ModelFamilyFormFrame frame)
    {
        var row = new JObject { ["kind"] = Kind, ["message"] = Message };
        if (At != null) row["at"] = ModelFamilyProfileAnalysis.Point(frame.ToWorld(At));
        if (SourceIds.Count > 0) row["sourceIds"] = new JArray(SourceIds.Take(20));
        if (Layers.Count > 0) row["layers"] = new JArray(Layers);
        if (Loops.Count > 0) row["loops"] = new JArray(Loops);
        return row;
    }
}

/// <summary>A closed loop found in the curves: a minimal region, counter-clockwise in plane coordinates.</summary>
public sealed class ModelFamilyProfileLoop
{
    /// <summary>The stable index: loops are ordered by area (largest first), then by their lowest corner.</summary>
    public int Index { get; internal set; }
    /// <summary>Segments in plane coordinates <c>[u, v, 0]</c>, chained end to start, counter-clockwise.</summary>
    public List<ModelFamilyFormSegment> Uv { get; internal set; } = new();
    /// <summary>The same segments in family coordinates on the plane: what a form's profile is made from.</summary>
    public List<ModelFamilyFormSegment> World { get; internal set; } = new();
    public List<double[]> Polygon { get; internal set; } = new();
    /// <summary>Enclosed area, mm² (positive).</summary>
    public double Area { get; internal set; }
    public double[] Min { get; internal set; } = Vec(0, 0);
    public double[] Max { get; internal set; } = Vec(0, 0);
    public double Width => Max[0] - Min[0];
    public double Height => Max[1] - Min[1];
    public double[] CentreUv => Vec((Min[0] + Max[0]) / 2, (Min[1] + Max[1]) / 2);
    /// <summary>rectangle, circle, polygon or mixed.</summary>
    public string Kind { get; internal set; } = "mixed";
    public List<string> Layers { get; internal set; } = new();
    /// <summary>Elements the loop's curves came from, duplicates included (what removeSourceCurves deletes).</summary>
    public List<long> SourceIds { get; internal set; } = new();
    /// <summary>The loop that directly contains this one; null for an outer loop.</summary>
    public int? Parent { get; internal set; }
    /// <summary>0 for an outer loop, 1 for a hole, 2 for an island in a hole …</summary>
    public int Depth { get; internal set; }
    public List<int> Children { get; } = new();
    public bool Approximated { get; internal set; }
    /// <summary>Some of its segments also bound another loop (lines divide an outline into regions).</summary>
    public bool SharesEdges { get; internal set; }
    public bool SelfIntersecting { get; internal set; }
    /// <summary>The loop passes through one vertex twice.</summary>
    public bool TouchesItself { get; internal set; }
    public int ShortSegments { get; internal set; }
    /// <summary>Other loops whose curves cross this one's.</summary>
    public List<int> Crosses { get; } = new();
    internal List<int> SegmentIds { get; set; } = new();
    internal List<int> VertexIds { get; set; } = new();
    /// <summary>The outline with arcs split finely enough to stay within the join tolerance of the true arc: for containment.</summary>
    internal List<double[]> Fine { get; set; } = new();

    /// <summary>Whether a form can be built from it: not crossing or touching itself, no segment shorter than Revit's minimum.</summary>
    public bool Usable => Reasons.Count == 0;

    /// <summary>Why it cannot be used, as sentences; empty when it can.</summary>
    public List<string> Reasons
    {
        get
        {
            var reasons = new List<string>();
            if (SelfIntersecting) reasons.Add("the loop crosses itself");
            if (TouchesItself) reasons.Add("the loop passes through one point twice");
            if (ShortSegments > 0) reasons.Add($"{ShortSegments} segment(s) are shorter than Revit's minimum curve length {Mm(ModelFamilyFormCheck.RevitShortCurveMm)} mm");
            return reasons;
        }
    }

    public JObject ToJson(ModelFamilyFormFrame frame, int maxPoints = 48)
    {
        var row = new JObject
        {
            ["loop"] = Index,
            ["kind"] = Kind,
            ["segments"] = Uv.Count,
            ["width"] = Math.Round(Width, 3),
            ["height"] = Math.Round(Height, 3),
            ["area"] = Math.Round(Area, 3),
            ["min"] = ModelFamilyProfileAnalysis.Point(frame.ToWorld(Min)),
            ["max"] = ModelFamilyProfileAnalysis.Point(frame.ToWorld(Max)),
            ["centre"] = ModelFamilyProfileAnalysis.Point(frame.ToWorld(CentreUv)),
            ["layers"] = new JArray(Layers),
            ["parent"] = Parent,
            ["depth"] = Depth,
            ["usable"] = Usable
        };
        if (Children.Count > 0) row["children"] = new JArray(Children);
        if (!Usable) row["reasons"] = new JArray(Reasons);
        if (Approximated) row["approximated"] = true;
        if (SharesEdges) row["sharesEdges"] = true;
        if (Crosses.Count > 0) row["crosses"] = new JArray(Crosses);
        if (SourceIds.Count > 0) row["sourceIds"] = new JArray(SourceIds.Take(20));
        var step = Math.Max(1, (int)Math.Ceiling(Polygon.Count / (double)Math.Max(3, maxPoints)));
        row["points"] = new JArray(Polygon.Where((_, i) => i % step == 0).Select(p => ModelFamilyProfileAnalysis.Point(frame.ToWorld(p))));
        if (step > 1) row["pointsSimplified"] = true;
        return row;
    }
}

/// <summary>Curves that do not close: one chain of connected segments with loose ends.</summary>
public sealed class ModelFamilyProfileOpenChain
{
    public int Segments { get; internal set; }
    public double Length { get; internal set; }
    /// <summary>The loose ends (plane coordinates), each with the gap to the nearest other loose end.</summary>
    public List<(double[] Point, double? Gap, double[]? Nearest)> Ends { get; } = new();
    public List<string> Layers { get; internal set; } = new();
    public List<long> SourceIds { get; internal set; } = new();

    public JObject ToJson(ModelFamilyFormFrame frame) => new()
    {
        ["segments"] = Segments,
        ["length"] = Math.Round(Length, 3),
        ["ends"] = new JArray(Ends.Select(e => new JObject
        {
            ["point"] = ModelFamilyProfileAnalysis.Point(frame.ToWorld(e.Point)),
            ["gap"] = e.Gap == null ? null : Math.Round(e.Gap.Value, 3),
            ["nearest"] = e.Nearest == null ? null : ModelFamilyProfileAnalysis.Point(frame.ToWorld(e.Nearest))
        })),
        ["layers"] = new JArray(Layers),
        ["sourceIds"] = new JArray(SourceIds.Take(20))
    };
}

/// <summary>
/// Turns loose curves on a plane into closed loops (#177). Pure. The steps:
/// <list type="number">
/// <item>Curves off the plane are left out; the rest are projected to plane coordinates and put in a canonical order,
/// so the result does not depend on the order the curves came in.</item>
/// <item>Endpoints within the tolerance are clustered into vertices (transitively: three ends within the tolerance of
/// each other are one vertex) and snapped onto them.</item>
/// <item>The same curve twice (also reversed) is kept once and reported; collinear lines that overlap, curves that
/// cross without a vertex and ends that lie on another curve are reported and not merged.</item>
/// <item>Curves with a loose end are removed repeatedly and become open chains.</item>
/// <item>The rest is traced as a planar graph: at each vertex the curves are ordered by the angle they leave at, and
/// each region is walked with the region on its left. Every bounded region is a loop; the outlines of the regions are
/// the minimal faces, so two rectangles sharing an edge give two loops (each <c>sharesEdges</c>), not the outline
/// around both. A curve walked in both directions by one region joins two outlines (a bridge): it is removed and
/// reported, and the tracing repeats.</item>
/// <item>Loops are ordered by area (largest first), then by their lowest corner, and numbered; each gets the loop that
/// directly contains it (<c>parent</c>) and its depth.</item>
/// </list>
/// </summary>
public sealed class ModelFamilyProfileAnalysis
{
    public ModelFamilyFormFrame Frame { get; private set; } = ModelFamilyFormFrame.XY;
    public ModelFamilyProfileOptions Options { get; private set; } = new();
    public List<ModelFamilyProfileLoop> Loops { get; } = new();
    public List<ModelFamilyProfileOpenChain> Open { get; } = new();
    public List<ModelFamilyProfileIssue> Issues { get; } = new();
    /// <summary>Curves given, and segments analysed after the off-plane, zero-length and duplicate checks.</summary>
    public int CurveCount { get; private set; }
    public int SegmentCount { get; private set; }

    internal static JArray Point(double[] p) => new(p.Take(3).Select(v => Math.Round(v, 3)));

    private sealed class Seg
    {
        public int Id;
        public ModelFamilyFormSegmentKind Kind;
        public double[] S = null!;
        public double[]? M;
        public double[] E = null!;
        public string Layer = string.Empty;
        public long? SourceId;
        public bool Approximated;
        public readonly List<long> AlsoIds = new();
        public int A, B;
        /// <summary>null while in the graph; "duplicate", "open", "bridge" once taken out.</summary>
        public string? Removed;
        public double[] Center = null!;
        public double Radius;
        public double StartAngle;
        public double Sweep;
        public double Length;
        public bool Crosses;

        public ModelFamilyFormSegment Forward() => Kind == ModelFamilyFormSegmentKind.Line
            ? ModelFamilyFormSegment.Line(Copy(S), Copy(E))
            : ModelFamilyFormSegment.Arc(Copy(S), Copy(M!), Copy(E));

        public ModelFamilyFormSegment Backward() => Kind == ModelFamilyFormSegmentKind.Line
            ? ModelFamilyFormSegment.Line(Copy(E), Copy(S))
            : ModelFamilyFormSegment.Arc(Copy(E), Copy(M!), Copy(S));

        public IEnumerable<long> Ids() => (SourceId == null ? Enumerable.Empty<long>() : new[] { SourceId.Value }).Concat(AlsoIds);

        public List<double[]> Polyline()
        {
            var points = Discretize(Forward(), Math.PI / 32);
            points.Add(Vec(E[0], E[1]));
            return points;
        }
    }

    /// <summary>A curve leaving a vertex: the segment, its direction, and the angle and curvature it leaves with.</summary>
    private readonly struct HalfEdge
    {
        public HalfEdge(int segment, bool forward, double angle, double curvature)
        {
            Segment = segment;
            Forward = forward;
            Angle = angle;
            Curvature = curvature;
        }

        public int Segment { get; }
        public bool Forward { get; }
        public double Angle { get; }
        public double Curvature { get; }
    }

    private readonly List<Seg> _segs = new();
    private readonly List<double[]> _vertices = new();
    private int[] _initialDegree = Array.Empty<int>();

    /// <summary>Analyses curves in family millimetres on the plane <paramref name="frame"/>.</summary>
    public static ModelFamilyProfileAnalysis Run(IReadOnlyList<ModelFamilyProfileCurve> curves, ModelFamilyFormFrame frame, ModelFamilyProfileOptions? options = null)
    {
        var analysis = new ModelFamilyProfileAnalysis { Frame = frame, Options = options ?? new ModelFamilyProfileOptions(), CurveCount = curves.Count };
        analysis.Project(curves);
        analysis.Cluster();
        analysis.Deduplicate();
        analysis.CheckLengths();
        analysis.CheckPairs();
        analysis.FindLoops();
        analysis.ReportApproximated();
        analysis.SortIssues();
        return analysis;
    }

    private double Tol => Math.Max(1e-9, Options.ToleranceMm);

    // ── 1. Projection and canonical order ───────────────────────────────────

    private void Project(IReadOnlyList<ModelFamilyProfileCurve> curves)
    {
        var offPlane = new List<(ModelFamilyProfileCurve Curve, double Offset)>();
        var projected = new List<Seg>();
        foreach (var curve in curves)
        {
            var offset = curve.Points().Max(p => Math.Abs(Frame.OffsetOf(p)));
            if (offset > Options.OffPlaneMm)
            {
                offPlane.Add((curve, offset));
                continue;
            }
            var s = Frame.ToUv(curve.Start);
            var e = Frame.ToUv(curve.End);
            var seg = new Seg { Kind = curve.Kind, S = s, E = e, Layer = curve.Layer, SourceId = curve.SourceId, Approximated = curve.Approximated };
            if (curve.Kind == ModelFamilyFormSegmentKind.Arc && curve.Mid != null)
            {
                var m = Frame.ToUv(curve.Mid);
                if (Arc2D(s, m, e) != null) seg.M = m;
                else seg.Kind = ModelFamilyFormSegmentKind.Line;   // its points are on one line on this plane
            }
            else seg.Kind = ModelFamilyFormSegmentKind.Line;
            if (Distance2D(s, e) <= Tol && seg.Kind == ModelFamilyFormSegmentKind.Line)
            {
                Issues.Add(new ModelFamilyProfileIssue
                {
                    Kind = "zeroLength", At = s, SourceIds = Ids(curve.SourceId), Layers = Layers(curve.Layer),
                    Message = $"A curve at {Format(Frame.ToWorld(s))} is shorter than the join tolerance ({Mm(Tol)} mm); it was left out."
                });
                continue;
            }
            projected.Add(seg);
        }
        foreach (var group in offPlane.GroupBy(o => o.Curve.SourceId))
        {
            var worst = group.Max(o => o.Offset);
            Issues.Add(new ModelFamilyProfileIssue
            {
                Kind = "offPlane", At = Frame.ToUv(group.First().Curve.Start), SourceIds = Ids(group.Key),
                Layers = group.Select(o => o.Curve.Layer).Where(l => l.Length > 0).Distinct().OrderBy(l => l, StringComparer.Ordinal).ToList(),
                Message = $"{group.Count()} curve(s) lie up to {Mm(worst)} mm off the work plane (more than {Mm(Options.OffPlaneMm)} mm) and were left out; pass the workPlane they are on, or leave their layer out."
            });
        }

        // Canonical order: the result must not depend on the order the curves came in.
        projected.Sort(CompareCanonical);
        for (var i = 0; i < projected.Count; i++) projected[i].Id = i;
        _segs.AddRange(projected);
    }

    private static int CompareCanonical(Seg a, Seg b)
    {
        var ka = Key(a);
        var kb = Key(b);
        for (var i = 0; i < ka.Length; i++)
        {
            var c = ka[i].CompareTo(kb[i]);
            if (c != 0) return c;
        }
        var layer = string.CompareOrdinal(a.Layer, b.Layer);
        if (layer != 0) return layer;
        return (a.SourceId ?? long.MinValue).CompareTo(b.SourceId ?? long.MinValue);
    }

    private static double[] Key(Seg s)
    {
        double R(double v) => Math.Round(v, 6);
        var first = R(s.S[0]) < R(s.E[0]) || R(s.S[0]) == R(s.E[0]) && R(s.S[1]) <= R(s.E[1]) ? s.S : s.E;
        var second = ReferenceEquals(first, s.S) ? s.E : s.S;
        var mid = s.M ?? Vec(0, 0);
        return new[] { R(first[0]), R(first[1]), R(second[0]), R(second[1]), (double)s.Kind, R(mid[0]), R(mid[1]) };
    }

    // ── 2. Vertices ──────────────────────────────────────────────────────────

    private void Cluster()
    {
        var points = new List<double[]>();
        foreach (var s in _segs) { points.Add(s.S); points.Add(s.E); }
        var parent = Enumerable.Range(0, points.Count).ToArray();
        int Find(int i)
        {
            while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
            return i;
        }
        void Union(int a, int b)
        {
            a = Find(a); b = Find(b);
            if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
        }

        var cell = Tol;
        var grid = new Dictionary<(long, long), List<int>>();
        (long, long) CellOf(double[] p) => ((long)Math.Floor(p[0] / cell), (long)Math.Floor(p[1] / cell));
        for (var i = 0; i < points.Count; i++)
        {
            var (cx, cy) = CellOf(points[i]);
            for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                    if (grid.TryGetValue((cx + dx, cy + dy), out var near))
                        foreach (var j in near)
                            if (Distance2D(points[i], points[j]) <= Tol) Union(i, j);
            if (!grid.TryGetValue((cx, cy), out var list)) grid[(cx, cy)] = list = new List<int>();
            list.Add(i);
        }

        // Each cluster's vertex is the mean of its points (summed in a fixed order); vertices are numbered by position.
        var clusters = Enumerable.Range(0, points.Count).GroupBy(Find).Select(g =>
        {
            var members = g.Select(i => points[i]).OrderBy(p => p[0]).ThenBy(p => p[1]).ToList();
            return (Members: g.ToList(), Point: Vec(members.Average(p => p[0]), members.Average(p => p[1])));
        }).OrderBy(c => Math.Round(c.Point[0], 6)).ThenBy(c => Math.Round(c.Point[1], 6)).ToList();
        var vertexOf = new int[points.Count];
        for (var v = 0; v < clusters.Count; v++)
        {
            _vertices.Add(clusters[v].Point);
            foreach (var i in clusters[v].Members) vertexOf[i] = v;
        }

        foreach (var s in _segs)
        {
            s.A = vertexOf[2 * s.Id];
            s.B = vertexOf[2 * s.Id + 1];
            s.S = Copy(_vertices[s.A]);
            s.E = Copy(_vertices[s.B]);
            if (s.Kind == ModelFamilyFormSegmentKind.Arc)
            {
                var arc = s.A == s.B ? null : Arc2D(s.S, s.M!, s.E);
                if (arc == null)
                {
                    s.Kind = ModelFamilyFormSegmentKind.Line;
                    s.M = null;
                }
                else
                {
                    (s.Center, s.Radius, s.StartAngle, s.Sweep) = arc.Value;
                    var half = s.StartAngle + s.Sweep / 2;
                    s.M = Vec(s.Center[0] + s.Radius * Math.Cos(half), s.Center[1] + s.Radius * Math.Sin(half));
                }
            }
            s.Length = Length2D(s.Forward());
            if (s.A == s.B)
            {
                s.Removed = "zeroLength";
                Issues.Add(new ModelFamilyProfileIssue
                {
                    Kind = "zeroLength", At = s.S, SourceIds = s.Ids().ToList(), Layers = Layers(s.Layer), Segments = { s.Id },
                    Message = $"A curve at {Format(Frame.ToWorld(s.S))} starts and ends within the join tolerance ({Mm(Tol)} mm); it was left out."
                });
            }
        }
    }

    // ── 3. Duplicates ────────────────────────────────────────────────────────

    private void Deduplicate()
    {
        foreach (var group in Active().GroupBy(s => (Math.Min(s.A, s.B), Math.Max(s.A, s.B))))
        {
            var kept = new List<Seg>();
            foreach (var s in group)
            {
                var same = kept.FirstOrDefault(k => SameGeometry(k, s));
                if (same == null)
                {
                    kept.Add(s);
                    continue;
                }
                s.Removed = "duplicate";
                same.AlsoIds.AddRange(s.Ids());
                var ids = same.SourceId == null ? s.Ids().ToList() : new[] { same.SourceId.Value }.Concat(s.Ids()).Distinct().ToList();
                Issues.Add(new ModelFamilyProfileIssue
                {
                    Kind = "duplicate", At = Midpoint(s), SourceIds = ids, Layers = Layers(same.Layer, s.Layer), Segments = { same.Id, s.Id },
                    Message = $"The same {(s.Kind == ModelFamilyFormSegmentKind.Line ? "line" : "arc")} is drawn twice near {Format(Frame.ToWorld(Midpoint(s)))}; it was used once (deduplicated)."
                });
            }
        }
    }

    private bool SameGeometry(Seg a, Seg b)
    {
        if (a.Kind != b.Kind) return false;
        if (a.Kind == ModelFamilyFormSegmentKind.Line) return true;   // same two vertices
        return Distance2D(a.Center, b.Center) <= Tol && Math.Abs(a.Radius - b.Radius) <= Tol && Distance2D(a.M!, b.M!) <= Tol;
    }

    private static double[] Midpoint(Seg s) => s.Kind == ModelFamilyFormSegmentKind.Arc ? Copy(s.M!) : Vec((s.S[0] + s.E[0]) / 2, (s.S[1] + s.E[1]) / 2);

    private IEnumerable<Seg> Active() => _segs.Where(s => s.Removed == null);

    // ── 4. Short segments ────────────────────────────────────────────────────

    private void CheckLengths()
    {
        foreach (var s in Active().Where(s => s.Length < Options.MinSegmentMm))
            Issues.Add(new ModelFamilyProfileIssue
            {
                Kind = "shortSegment", At = Midpoint(s), SourceIds = s.Ids().ToList(), Layers = Layers(s.Layer), Segments = { s.Id },
                Message = $"A segment near {Format(Frame.ToWorld(Midpoint(s)))} is {Mm(s.Length)} mm long; Revit refuses curves shorter than {Mm(Options.MinSegmentMm)} mm, so a loop through it cannot be built."
            });
    }

    // ── 5. Overlaps, crossings, ends on curves ───────────────────────────────

    private void CheckPairs()
    {
        var segs = Active().ToList();
        if (segs.Count > Options.MaxPairCheckSegments)
        {
            Issues.Add(new ModelFamilyProfileIssue
            {
                Kind = "pairChecksSkipped",
                Message = $"{segs.Count} segments: overlaps and crossings were not checked (more than {Options.MaxPairCheckSegments}). Narrow the layers to check them."
            });
            return;
        }
        var lines = segs.Select(s => s.Polyline()).ToList();
        var boxes = lines.Select(l => (Min: Vec(l.Min(p => p[0]) - Tol, l.Min(p => p[1]) - Tol), Max: Vec(l.Max(p => p[0]) + Tol, l.Max(p => p[1]) + Tol))).ToList();
        for (var i = 0; i < segs.Count; i++)
        {
            for (var j = i + 1; j < segs.Count; j++)
            {
                if (boxes[i].Max[0] < boxes[j].Min[0] || boxes[j].Max[0] < boxes[i].Min[0] ||
                    boxes[i].Max[1] < boxes[j].Min[1] || boxes[j].Max[1] < boxes[i].Min[1]) continue;
                var a = segs[i];
                var b = segs[j];
                if (a.Kind == ModelFamilyFormSegmentKind.Line && b.Kind == ModelFamilyFormSegmentKind.Line && Overlap(a, b) is { } overlap)
                {
                    Issues.Add(new ModelFamilyProfileIssue
                    {
                        Kind = "overlap", At = overlap.At, SourceIds = a.Ids().Concat(b.Ids()).Distinct().ToList(), Layers = Layers(a.Layer, b.Layer), Segments = { a.Id, b.Id },
                        Message = $"Two collinear lines overlap for {Mm(overlap.Length)} mm near {Format(Frame.ToWorld(overlap.At))}; they were not merged. Delete or trim one so the outline is drawn once."
                    });
                    continue;
                }
                var shared = new[] { a.A, a.B }.Intersect(new[] { b.A, b.B }).Any();
                var onCurve = EndOnCurve(a, b, lines[j]) ?? EndOnCurve(b, a, lines[i]);
                if (onCurve != null)
                {
                    Issues.Add(new ModelFamilyProfileIssue
                    {
                        Kind = "endpointOnSegment", At = onCurve, SourceIds = a.Ids().Concat(b.Ids()).Distinct().ToList(), Layers = Layers(a.Layer, b.Layer), Segments = { a.Id, b.Id },
                        Message = $"A curve ends at {Format(Frame.ToWorld(onCurve))} on the middle of another curve without a vertex there; the two are not connected. Split the other curve at that point if the outline turns there."
                    });
                    continue;
                }
                if (shared) continue;
                if (Crossing(lines[i], lines[j]) is { } at)
                {
                    a.Crosses = b.Crosses = true;
                    Issues.Add(new ModelFamilyProfileIssue
                    {
                        Kind = "crossing", At = at, SourceIds = a.Ids().Concat(b.Ids()).Distinct().ToList(), Layers = Layers(a.Layer, b.Layer), Segments = { a.Id, b.Id },
                        Message = $"Two curves cross at {Format(Frame.ToWorld(at))} without a vertex; a loop through both crosses itself or another loop."
                    });
                }
            }
        }
    }

    private (double[] At, double Length)? Overlap(Seg a, Seg b)
    {
        var direction = Normalize(Vec(a.E[0] - a.S[0], a.E[1] - a.S[1]));
        double Off(double[] p) => Math.Abs(direction[0] * (p[1] - a.S[1]) - direction[1] * (p[0] - a.S[0]));
        if (Off(b.S) > Tol || Off(b.E) > Tol) return null;
        double T(double[] p) => direction[0] * (p[0] - a.S[0]) + direction[1] * (p[1] - a.S[1]);
        var a0 = 0.0; var a1 = a.Length;
        var b0 = Math.Min(T(b.S), T(b.E)); var b1 = Math.Max(T(b.S), T(b.E));
        var from = Math.Max(a0, b0); var to = Math.Min(a1, b1);
        if (to - from <= Tol) return null;
        var t = (from + to) / 2;
        return (Vec(a.S[0] + direction[0] * t, a.S[1] + direction[1] * t), to - from);
    }

    /// <summary>An end of <paramref name="end"/> that lies on the interior of <paramref name="curve"/> (not at one of its vertices).</summary>
    private double[]? EndOnCurve(Seg end, Seg curve, List<double[]> polyline)
    {
        foreach (var (point, vertex) in new[] { (end.S, end.A), (end.E, end.B) })
        {
            if (vertex == curve.A || vertex == curve.B) continue;
            if (Distance2D(point, curve.S) <= Tol || Distance2D(point, curve.E) <= Tol) continue;
            for (var k = 0; k + 1 < polyline.Count; k++)
                if (DistancePointSegment(point, polyline[k], polyline[k + 1]) <= Tol) return Copy(point);
        }
        return null;
    }

    private static double[]? Crossing(List<double[]> first, List<double[]> second)
    {
        for (var i = 0; i + 1 < first.Count; i++)
            for (var j = 0; j + 1 < second.Count; j++)
                if (SegmentsIntersect(first[i], first[i + 1], second[j], second[j + 1], 1e-9))
                    return Intersection(first[i], first[i + 1], second[j], second[j + 1]);
        return null;
    }

    private static double[] Intersection(double[] a, double[] b, double[] c, double[] d)
    {
        var r = Vec(b[0] - a[0], b[1] - a[1]);
        var s = Vec(d[0] - c[0], d[1] - c[1]);
        var denominator = r[0] * s[1] - r[1] * s[0];
        if (Math.Abs(denominator) < 1e-12) return Vec((a[0] + b[0]) / 2, (a[1] + b[1]) / 2);
        var t = ((c[0] - a[0]) * s[1] - (c[1] - a[1]) * s[0]) / denominator;
        return Vec(a[0] + t * r[0], a[1] + t * r[1]);
    }

    // ── 6. Loops ─────────────────────────────────────────────────────────────

    private void FindLoops()
    {
        _initialDegree = Degrees();
        SegmentCount = Active().Count();
        foreach (var v in Enumerable.Range(0, _vertices.Count).Where(v => _initialDegree[v] > 2))
            Issues.Add(new ModelFamilyProfileIssue
            {
                Kind = "branch", At = _vertices[v], Vertex = v,
                SourceIds = Active().Where(s => s.A == v || s.B == v).SelectMany(s => s.Ids()).Distinct().ToList(),
                Layers = Active().Where(s => s.A == v || s.B == v).Select(s => s.Layer).Where(l => l.Length > 0).Distinct().OrderBy(l => l, StringComparer.Ordinal).ToList(),
                Message = $"{_initialDegree[v]} curves meet at {Format(Frame.ToWorld(_vertices[v]))}; the outlines through this point are ambiguous. Each region the curves enclose is its own loop (listed in loops); check you pick the right one, or hide the extra curves' layer."
            });

        Prune("open");
        var faces = new List<List<(int Segment, bool Forward)>>();
        for (var round = 0; round < 1000; round++)
        {
            faces = TraceFaces();
            var bridges = faces.SelectMany(face => face.GroupBy(h => h.Segment).Where(g => g.Count() > 1).Select(g => g.Key)).Distinct().ToList();
            if (bridges.Count == 0) break;
            foreach (var id in bridges) _segs[id].Removed = "bridge";
            Prune("bridge");
            foreach (var group in bridges.GroupBy(id => _segs[id].Layer))
                Issues.Add(new ModelFamilyProfileIssue
                {
                    Kind = "bridge", At = Midpoint(_segs[group.First()]), Segments = group.ToList(),
                    SourceIds = group.SelectMany(id => _segs[id].Ids()).Distinct().ToList(), Layers = Layers(group.Key),
                    Message = $"{group.Count()} curve(s) near {Format(Frame.ToWorld(Midpoint(_segs[group.First()])))} connect one outline to another without enclosing anything; they are not part of any loop."
                });
        }

        var candidates = new List<ModelFamilyProfileLoop>();
        var degenerate = new HashSet<string>(StringComparer.Ordinal);
        foreach (var traced in faces)
        {
            // Start each loop at its lowest-numbered vertex, so its segments come out in the same order however the curves came in.
            int From((int Segment, bool Forward) h) => h.Forward ? _segs[h.Segment].A : _segs[h.Segment].B;
            var first = Enumerable.Range(0, traced.Count).OrderBy(i => From(traced[i])).ThenBy(i => traced[i].Segment).First();
            var face = traced.Skip(first).Concat(traced.Take(first)).ToList();
            var uv = face.Select(h => h.Forward ? _segs[h.Segment].Forward() : _segs[h.Segment].Backward()).ToList();
            var area = SignedArea(uv);
            if (area < -Options.MinAreaMm2) continue;   // the outside of a group of curves
            var polygon = ModelFamilyFormGeometry.Polygon(uv);
            if (area <= Options.MinAreaMm2)
            {
                var key = string.Join(",", face.Select(h => h.Segment).OrderBy(i => i));
                if (!degenerate.Add(key)) continue;
                var crosses = SelfIntersection(polygon, 1e-6) != null || face.Any(h => _segs[h.Segment].Crosses);
                Issues.Add(new ModelFamilyProfileIssue
                {
                    Kind = crosses ? "selfIntersecting" : "zeroArea", At = uv[0].Start, Segments = face.Select(h => h.Segment).Distinct().ToList(),
                    SourceIds = face.SelectMany(h => _segs[h.Segment].Ids()).Distinct().ToList(),
                    Layers = face.Select(h => _segs[h.Segment].Layer).Where(l => l.Length > 0).Distinct().OrderBy(l => l, StringComparer.Ordinal).ToList(),
                    Message = crosses
                        ? $"A closed outline through {Format(Frame.ToWorld(uv[0].Start))} crosses itself and encloses no net area (a bow-tie); it is not a loop. Redraw it without the crossing."
                        : $"A closed outline through {Format(Frame.ToWorld(uv[0].Start))} encloses no area; it is not a loop."
                });
                continue;
            }
            var (min, max) = Bounds(uv);
            var vertices = face.Select(h => h.Forward ? _segs[h.Segment].A : _segs[h.Segment].B).ToList();
            var segments = face.Select(h => h.Segment).ToList();
            candidates.Add(new ModelFamilyProfileLoop
            {
                Uv = uv,
                World = uv.Select(s => s.Map(p => Frame.ToWorld(p))).ToList(),
                Polygon = polygon,
                Fine = FinePolygon(uv),
                Area = area,
                Min = min,
                Max = max,
                Kind = Classify(face),
                Layers = segments.Select(i => _segs[i].Layer).Where(l => l.Length > 0).Distinct().OrderBy(l => l, StringComparer.Ordinal).ToList(),
                SourceIds = segments.SelectMany(i => _segs[i].Ids()).Distinct().OrderBy(id => id).ToList(),
                Approximated = segments.Any(i => _segs[i].Approximated),
                SelfIntersecting = SelfIntersection(polygon, 1e-6) != null || segments.Any(i => _segs[i].Crosses && IntersectsWithin(i, segments)),
                TouchesItself = vertices.Distinct().Count() != vertices.Count,
                ShortSegments = segments.Count(i => _segs[i].Length < Options.MinSegmentMm),
                SegmentIds = segments,
                VertexIds = vertices
            });
        }

        // Order and number.
        var ordered = candidates
            .OrderByDescending(l => Math.Round(l.Area, 3))
            .ThenBy(l => Math.Round(l.Min[0], 3)).ThenBy(l => Math.Round(l.Min[1], 3))
            .ThenBy(l => Math.Round(l.Max[0], 3)).ThenBy(l => Math.Round(l.Max[1], 3))
            .ThenBy(l => l.Uv.Count).ToList();
        for (var i = 0; i < ordered.Count; i++) ordered[i].Index = i;
        Loops.AddRange(ordered);

        // Edges shared between loops; loops whose curves cross.
        var usage = Loops.SelectMany(l => l.SegmentIds.Distinct().Select(s => (Segment: s, Loop: l.Index))).GroupBy(x => x.Segment).ToDictionary(g => g.Key, g => g.Select(x => x.Loop).ToList());
        foreach (var loop in Loops)
            loop.SharesEdges = loop.SegmentIds.Any(s => usage[s].Count > 1);
        foreach (var crossing in Issues.Where(i => i.Kind == "crossing"))
        {
            var first = Loops.Where(l => l.SegmentIds.Contains(crossing.Segments[0])).Select(l => l.Index).ToList();
            var second = Loops.Where(l => l.SegmentIds.Contains(crossing.Segments[1])).Select(l => l.Index).ToList();
            foreach (var a in first)
                foreach (var b in second.Where(b => b != a))
                {
                    if (!Loops[a].Crosses.Contains(b)) Loops[a].Crosses.Add(b);
                    if (!Loops[b].Crosses.Contains(a)) Loops[b].Crosses.Add(a);
                }
        }
        foreach (var loop in Loops) loop.Crosses.Sort();

        Nest();
        foreach (var issue in Issues)
        {
            var related = issue.Vertex != null
                ? Loops.Where(l => l.VertexIds.Contains(issue.Vertex.Value))
                : Loops.Where(l => l.SegmentIds.Intersect(issue.Segments).Any());
            issue.Loops.AddRange(related.Select(l => l.Index));
        }
        foreach (var loop in Loops.Where(l => l.SelfIntersecting || l.TouchesItself))
            Issues.Add(new ModelFamilyProfileIssue
            {
                Kind = "selfIntersecting", At = loop.Uv[0].Start, SourceIds = loop.SourceIds.ToList(), Layers = loop.Layers.ToList(),
                Message = $"Loop {loop.Index} {(loop.SelfIntersecting ? "crosses itself" : "passes through one point twice")}; Revit cannot build it. Redraw the outline so it does not cross or touch itself.",
                Loops = { loop.Index }
            });

        BuildOpenChains();
    }

    private void SortIssues()
    {
        // A stable sort: List.Sort is not, and equal issues must keep their (canonical) order.
        var sorted = Issues.Select((issue, i) => (Issue: issue, Order: i))
            .OrderBy(x => x.Issue.Kind, StringComparer.Ordinal)
            .ThenBy(x => Math.Round((x.Issue.At ?? Vec(0, 0))[0], 3))
            .ThenBy(x => Math.Round((x.Issue.At ?? Vec(0, 0))[1], 3))
            .ThenBy(x => x.Order)
            .Select(x => x.Issue).ToList();
        Issues.Clear();
        Issues.AddRange(sorted);
    }

    /// <summary>Whether segment <paramref name="segment"/> crosses another segment of the same loop.</summary>
    private bool IntersectsWithin(int segment, List<int> loop) =>
        Issues.Any(i => i.Kind == "crossing" && i.Segments.Contains(segment) && i.Segments.All(loop.Contains));

    private static Dictionary<int, List<Seg>> Incidence(IEnumerable<Seg> segs)
    {
        var map = new Dictionary<int, List<Seg>>();
        foreach (var s in segs)
            foreach (var v in s.A == s.B ? new[] { s.A } : new[] { s.A, s.B })
            {
                if (!map.TryGetValue(v, out var list)) map[v] = list = new List<Seg>();
                list.Add(s);
            }
        return map;
    }

    private int[] Degrees()
    {
        var degree = new int[_vertices.Count];
        foreach (var s in Active()) { degree[s.A]++; degree[s.B]++; }
        return degree;
    }

    /// <summary>Removes segments with a loose end, repeatedly, marking them <paramref name="reason"/>.</summary>
    private void Prune(string reason)
    {
        var degree = Degrees();
        var incident = Incidence(Active());
        var queue = new Queue<int>(Enumerable.Range(0, _vertices.Count).Where(v => degree[v] == 1));
        while (queue.Count > 0)
        {
            var v = queue.Dequeue();
            if (degree[v] != 1) continue;
            var s = incident.TryGetValue(v, out var list) ? list.FirstOrDefault(x => x.Removed == null) : null;
            if (s == null) continue;
            s.Removed = reason;
            degree[s.A]--;
            degree[s.B]--;
            var other = s.A == v ? s.B : s.A;
            if (degree[other] == 1) queue.Enqueue(other);
        }
    }

    private HalfEdge Leaving(Seg s, bool forward)
    {
        if (s.Kind == ModelFamilyFormSegmentKind.Line)
        {
            var from = forward ? s.S : s.E;
            var to = forward ? s.E : s.S;
            return new HalfEdge(s.Id, forward, Positive(Math.Atan2(to[1] - from[1], to[0] - from[0])), 0);
        }
        // The tangent of an arc is perpendicular to its radius; it turns left when walked counter-clockwise.
        var point = forward ? s.S : s.E;
        var radial = Math.Atan2(point[1] - s.Center[1], point[0] - s.Center[0]);
        var counterClockwise = forward ? s.Sweep > 0 : s.Sweep < 0;
        var angle = Positive(radial + (counterClockwise ? Math.PI / 2 : -Math.PI / 2));
        return new HalfEdge(s.Id, forward, angle, (counterClockwise ? 1 : -1) / s.Radius);
    }

    private static double Positive(double angle)
    {
        var twoPi = 2 * Math.PI;
        angle %= twoPi;
        if (angle < 0) angle += twoPi;
        return angle >= twoPi - 1e-12 ? 0 : angle;
    }

    /// <summary>
    /// Walks every region of the planar graph with the region on its left: arriving at a vertex, it leaves along the
    /// curve that comes next clockwise from the one it arrived on. Bounded regions come out counter-clockwise.
    /// </summary>
    private List<List<(int Segment, bool Forward)>> TraceFaces()
    {
        var outgoing = new Dictionary<int, List<HalfEdge>>();
        foreach (var s in Active())
        {
            Add(s.A, Leaving(s, true));
            Add(s.B, Leaving(s, false));
        }
        void Add(int vertex, HalfEdge half)
        {
            if (!outgoing.TryGetValue(vertex, out var list)) outgoing[vertex] = list = new List<HalfEdge>();
            list.Add(half);
        }
        foreach (var list in outgoing.Values)
            list.Sort((a, b) =>
            {
                if (Math.Abs(a.Angle - b.Angle) > 1e-9) return a.Angle.CompareTo(b.Angle);
                var c = a.Curvature.CompareTo(b.Curvature);
                return c != 0 ? c : a.Segment.CompareTo(b.Segment);
            });

        (int Segment, bool Forward) Next((int Segment, bool Forward) half)
        {
            var s = _segs[half.Segment];
            var at = half.Forward ? s.B : s.A;
            var list = outgoing[at];
            var twin = list.FindIndex(h => h.Segment == half.Segment && h.Forward == !half.Forward);
            var next = list[(twin - 1 + list.Count) % list.Count];
            return (next.Segment, next.Forward);
        }

        var faces = new List<List<(int Segment, bool Forward)>>();
        var visited = new HashSet<(int, bool)>();
        var total = Active().Count() * 2;
        foreach (var s in Active())
        {
            foreach (var forward in new[] { true, false })
            {
                var start = (s.Id, forward);
                if (visited.Contains(start)) continue;
                var face = new List<(int Segment, bool Forward)>();
                var current = start;
                for (var guard = 0; guard <= total; guard++)
                {
                    if (!visited.Add(current)) break;
                    face.Add(current);
                    current = Next(current);
                }
                faces.Add(face);
            }
        }
        return faces;
    }

    private string Classify(List<(int Segment, bool Forward)> face)
    {
        var segs = face.Select(h => _segs[h.Segment]).ToList();
        if (segs.All(s => s.Kind == ModelFamilyFormSegmentKind.Arc))
        {
            var first = segs[0];
            if (segs.All(s => Distance2D(s.Center, first.Center) <= Tol && Math.Abs(s.Radius - first.Radius) <= Tol) &&
                Math.Abs(segs.Sum(s => Math.Abs(s.Sweep)) - 2 * Math.PI) < 1e-6)
                return "circle";
            return "mixed";
        }
        if (segs.Any(s => s.Kind == ModelFamilyFormSegmentKind.Arc)) return "mixed";
        if (segs.Count == 4)
        {
            var directions = face.Select(h =>
            {
                var s = _segs[h.Segment];
                return Normalize(h.Forward ? Vec(s.E[0] - s.S[0], s.E[1] - s.S[1]) : Vec(s.S[0] - s.E[0], s.S[1] - s.E[1]));
            }).ToList();
            var square = true;
            for (var i = 0; i < 4; i++)
            {
                var a = directions[i]; var b = directions[(i + 1) % 4];
                if (Math.Abs(a[0] * b[0] + a[1] * b[1]) > 1e-6) square = false;
            }
            if (square) return "rectangle";
        }
        return "polygon";
    }

    /// <summary>Each loop's parent: the smallest loop that contains it (every point inside or on its edge, one strictly inside).</summary>
    private void Nest()
    {
        var tolerance = Math.Max(Tol, 1e-6);
        bool OnEdge(double[] p, List<double[]> polygon)
        {
            for (var i = 0; i < polygon.Count; i++)
                if (DistancePointSegment(p, polygon[i], polygon[(i + 1) % polygon.Count]) <= tolerance) return true;
            return false;
        }
        bool Contains(ModelFamilyProfileLoop outer, ModelFamilyProfileLoop inner)
        {
            if (outer.Area <= inner.Area) return false;
            if (inner.Min[0] < outer.Min[0] - tolerance || inner.Min[1] < outer.Min[1] - tolerance ||
                inner.Max[0] > outer.Max[0] + tolerance || inner.Max[1] > outer.Max[1] + tolerance) return false;
            var strictlyInside = false;
            foreach (var p in inner.Polygon)
            {
                if (OnEdge(p, outer.Fine)) continue;
                if (!Inside(p, outer.Fine)) return false;
                strictlyInside = true;
            }
            return strictlyInside;
        }
        foreach (var loop in Loops)
        {
            var parent = Loops.Where(o => o.Index != loop.Index && Contains(o, loop)).OrderBy(o => o.Area).ThenBy(o => o.Index).FirstOrDefault();
            loop.Parent = parent?.Index;
        }
        foreach (var loop in Loops)
        {
            var depth = 0;
            var current = loop.Parent;
            while (current != null && depth <= Loops.Count)
            {
                depth++;
                current = Loops[current.Value].Parent;
            }
            loop.Depth = depth;
            if (loop.Parent != null) Loops[loop.Parent.Value].Children.Add(loop.Index);
        }
    }

    private void BuildOpenChains()
    {
        var open = _segs.Where(s => s.Removed == "open").ToList();
        if (open.Count == 0) return;
        var looseEnds = Enumerable.Range(0, _vertices.Count).Where(v => _initialDegree[v] == 1).ToList();
        // Chains: open segments connected through shared vertices.
        var incident = Incidence(open);
        var chainOf = new Dictionary<int, int>();
        var chains = new List<List<Seg>>();
        foreach (var s in open)
        {
            if (chainOf.ContainsKey(s.Id)) continue;
            var chain = new List<Seg>();
            var stack = new Stack<Seg>();
            stack.Push(s);
            chainOf[s.Id] = chains.Count;
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                chain.Add(current);
                foreach (var next in incident[current.A].Concat(incident[current.B]).Where(o => !chainOf.ContainsKey(o.Id)))
                {
                    chainOf[next.Id] = chains.Count;
                    stack.Push(next);
                }
            }
            chains.Add(chain.OrderBy(c => c.Id).ToList());
        }
        foreach (var chain in chains)
        {
            var vertices = chain.SelectMany(s => new[] { s.A, s.B }).Distinct().ToList();
            var ends = vertices.Where(v => _initialDegree[v] == 1).OrderBy(v => v).ToList();
            var row = new ModelFamilyProfileOpenChain
            {
                Segments = chain.Count,
                Length = chain.Sum(s => s.Length),
                Layers = chain.Select(s => s.Layer).Where(l => l.Length > 0).Distinct().OrderBy(l => l, StringComparer.Ordinal).ToList(),
                SourceIds = chain.SelectMany(s => s.Ids()).Distinct().OrderBy(id => id).ToList()
            };
            // A single curve's own other end is no gap: joining it would collapse the curve, not close anything.
            var own = chain.Count == 1 ? vertices : new List<int>();
            foreach (var end in ends)
            {
                var nearest = looseEnds.Where(v => v != end && !own.Contains(v)).OrderBy(v => Distance2D(_vertices[v], _vertices[end])).ThenBy(v => v).Select(v => (int?)v).FirstOrDefault();
                row.Ends.Add((Copy(_vertices[end]), nearest == null ? null : Distance2D(_vertices[nearest.Value], _vertices[end]), nearest == null ? null : Copy(_vertices[nearest.Value])));
            }
            Open.Add(row);
        }
        Open.Sort((a, b) =>
        {
            var pa = a.Ends.Count > 0 ? a.Ends[0].Point : Vec(0, 0);
            var pb = b.Ends.Count > 0 ? b.Ends[0].Point : Vec(0, 0);
            var c = Math.Round(pa[0], 3).CompareTo(Math.Round(pb[0], 3));
            return c != 0 ? c : Math.Round(pa[1], 3).CompareTo(Math.Round(pb[1], 3));
        });
    }

    private void ReportApproximated()
    {
        var approximated = _segs.Where(s => s.Approximated && s.Removed != "duplicate").ToList();
        if (approximated.Count == 0) return;
        Issues.Add(new ModelFamilyProfileIssue
        {
            Kind = "approximated",
            SourceIds = approximated.SelectMany(s => s.Ids()).Distinct().OrderBy(id => id).ToList(),
            Layers = approximated.Select(s => s.Layer).Where(l => l.Length > 0).Distinct().OrderBy(l => l, StringComparer.Ordinal).ToList(),
            Message = $"Splines or ellipses were replaced by {approximated.Count} straight segments (Revit's display tessellation); loops through them are marked approximated."
        });
        foreach (var issue in Issues.Where(i => i.Kind == "approximated")) issue.Loops.AddRange(Loops.Where(l => l.Approximated).Select(l => l.Index));
    }

    /// <summary>
    /// A loop as a polygon whose chords stay within the join tolerance of its arcs (at most 1024 per arc). The coarse
    /// <see cref="ModelFamilyProfileLoop.Polygon"/> cuts up to 0.5 % of the radius off an arc (5 mm on a 1 m radius), so a
    /// hole near a large arc would fall outside it and lose its parent.
    /// </summary>
    private List<double[]> FinePolygon(List<ModelFamilyFormSegment> uv)
    {
        var points = new List<double[]>();
        foreach (var s in uv)
        {
            var step = Math.PI / 16;
            if (s.Kind == ModelFamilyFormSegmentKind.Arc && Arc2D(s.Start, s.Mid!, s.End) is { } arc && arc.Radius > Tol)
                step = Math.Min(step, Math.Max(2 * Math.Acos(1 - Tol / arc.Radius), Math.Abs(arc.Sweep) / 1024));
            points.AddRange(Discretize(s, step));
        }
        return points;
    }

    private static List<long> Ids(long? id) => id == null ? new List<long>() : new List<long> { id.Value };

    private static List<string> Layers(params string[] layers) =>
        layers.Where(l => !string.IsNullOrEmpty(l)).Distinct().OrderBy(l => l, StringComparer.Ordinal).ToList();

    // ── Queries ──────────────────────────────────────────────────────────────

    /// <summary>The innermost loop whose outline contains a family point (projected onto the plane); null when none does.</summary>
    public ModelFamilyProfileLoop? LoopAt(double[] familyPoint)
    {
        var uv = Frame.ToUv(familyPoint);
        return Loops.Where(l => Inside(uv, l.Fine)).OrderByDescending(l => l.Depth).ThenBy(l => l.Area).ThenBy(l => l.Index).FirstOrDefault();
    }

    /// <summary>The analysis as the read tool returns it; <paramref name="limit"/> caps each list.</summary>
    public JObject ToJson(int limit)
    {
        JArray Capped<T>(IEnumerable<T> items, Func<T, JToken> row) => new(items.Take(limit).Select(row));
        var result = new JObject
        {
            ["counts"] = new JObject
            {
                ["curves"] = CurveCount,
                ["segments"] = SegmentCount,
                ["loops"] = Loops.Count,
                ["usableLoops"] = Loops.Count(l => l.Usable),
                ["open"] = Open.Count,
                ["issues"] = Issues.Count
            },
            ["loops"] = Capped(Loops, l => l.ToJson(Frame)),
            ["open"] = Capped(Open, o => o.ToJson(Frame)),
            ["issues"] = Capped(Issues, i => i.ToJson(Frame))
        };
        if (Loops.Count > limit || Open.Count > limit || Issues.Count > limit) result["truncated"] = true;
        return result;
    }
}
