using System.Globalization;

namespace RevitMCP.Addin.FilledRegions;

/// <summary>The rules a set of loops is checked against.</summary>
public sealed class LoopValidationOptions
{
    /// <summary>Revit's short curve tolerance in mm (Application.ShortCurveTolerance): a shorter piece cannot be a curve.</summary>
    public double ShortCurveMm { get; init; } = 0.8;

    /// <summary>Largest gap between consecutive pieces that still counts as joined.</summary>
    public double JoinToleranceMm { get; init; } = 0.5;

    /// <summary>Distance below which two boundary pieces count as touching.</summary>
    public double TouchToleranceMm { get; init; } = 0.01;

    /// <summary>
    /// false: the first loop is the outline and every other loop has to lie inside it, which is what a direct
    /// coordinates spec promises. true: separate outlines are fine (a room boundary, a set of detail lines).
    /// </summary>
    public bool AllowDisjoint { get; init; }
}

/// <summary>The verdict on a set of loops, with the loops normalized when they are valid.</summary>
public sealed class LoopValidation
{
    public bool IsValid => Code == null;
    public string? Code { get; set; }
    public string? Reason { get; set; }

    /// <summary>Outlines first. Even nesting depth runs counter-clockwise and odd depth (a hole) clockwise, seen in (u, v).</summary>
    public List<RegionLoop> Loops { get; set; } = new();

    /// <summary>Nesting depth per entry of <see cref="Loops"/>: 0 outline, 1 hole, 2 island in a hole, ...</summary>
    public List<int> Depths { get; set; } = new();

    public int OutlineCount => Depths.Count(d => d % 2 == 0);
    public int HoleCount => Depths.Count(d => d % 2 == 1);
    public double AreaMm2 { get; set; }
}

/// <summary>
/// Everything Revit would refuse about a boundary, found before a transaction opens: open loops, self-intersections,
/// loops crossing each other, too-short pieces, collapsed loops and holes outside the outline. Works on the flattened
/// outline of each loop. Pure.
/// </summary>
public static class LoopValidator
{
    private const double MinAreaMm2 = 1.0;

    public static LoopValidation Validate(IReadOnlyList<RegionLoop> loops, LoopValidationOptions options)
    {
        var result = new LoopValidation();
        if (loops.Count == 0) return Fail(result, RegionCodes.LoopEmpty, "No boundary loop was given.");

        var polylines = new List<List<Pt2>>(loops.Count);
        for (var i = 0; i < loops.Count; i++)
        {
            var problem = CheckSingle(loops[i], i, options, out var polyline);
            if (problem != null) return Fail(result, problem.Value.Code, problem.Value.Reason);
            polylines.Add(polyline);
        }

        for (var i = 0; i < polylines.Count; i++)
        for (var j = i + 1; j < polylines.Count; j++)
        {
            if (!Cross(polylines[i], polylines[j], options.TouchToleranceMm, out var near)) continue;
            return Fail(result, RegionCodes.LoopsIntersect,
                $"Loops {i + 1} and {j + 1} cross or touch near {near}. Holes and outlines must be apart.");
        }

        var depths = new int[polylines.Count];
        for (var i = 0; i < polylines.Count; i++)
        for (var j = 0; j < polylines.Count; j++)
            if (i != j && RegionLoopMath.Contains(polylines[j], polylines[i][0])) depths[i]++;

        if (!options.AllowDisjoint)
            for (var i = 1; i < polylines.Count; i++)
                if (!RegionLoopMath.Contains(polylines[0], polylines[i][0]))
                    return Fail(result, RegionCodes.HoleOutsideOuter,
                        $"Loop {i + 1} is not inside loop 1. The first loop is the outline and the others are holes in it.");

        var order = Enumerable.Range(0, loops.Count)
            .OrderBy(i => depths[i])
            .ThenByDescending(i => Math.Abs(RegionLoopMath.SignedArea(polylines[i])))
            .ToList();
        double area = 0;
        foreach (var i in order)
        {
            var signed = RegionLoopMath.SignedArea(polylines[i]);
            var wantCounterClockwise = depths[i] % 2 == 0;
            result.Loops.Add(signed > 0 == wantCounterClockwise ? loops[i] : loops[i].Reversed());
            result.Depths.Add(depths[i]);
            area += wantCounterClockwise ? Math.Abs(signed) : -Math.Abs(signed);
        }
        result.AreaMm2 = area;
        return result;
    }

    private static (string Code, string Reason)? CheckSingle(RegionLoop loop, int index, LoopValidationOptions options, out List<Pt2> polyline)
    {
        polyline = new List<Pt2>();
        var number = index + 1;
        if (loop.Segments.Count == 0) return (RegionCodes.LoopTooFewPoints, $"Loop {number} has no segments.");

        for (var i = 0; i < loop.Segments.Count; i++)
        {
            var segment = loop.Segments[i];
            var next = loop.Segments[(i + 1) % loop.Segments.Count];
            var gap = segment.End.DistanceTo(next.Start);
            if (gap > options.JoinToleranceMm)
                return (RegionCodes.LoopOpen,
                    string.Format(CultureInfo.InvariantCulture, "Loop {0} is open: {1:0.#} mm gap between {2} and {3}.", number, gap, segment.End, next.Start));
        }

        polyline = loop.Polyline();
        if (polyline.Count < 3 && loop.Segments.Count < 3)
            return (RegionCodes.LoopTooFewPoints, $"Loop {number} has {polyline.Count} distinct point(s); a boundary needs at least 3.");

        // Before the length check: a shape seen edge-on has zero-length pieces, and this says what is really wrong.
        if (AllOnOneLine(polyline, options.TouchToleranceMm))
            return (RegionCodes.LoopDegenerate, $"Loop {number} has no area in this view's plane: its points lie on one line, seen in this view.");

        for (var i = 0; i < loop.Segments.Count; i++)
        {
            var segment = loop.Segments[i];
            var length = segment.Mid is { } mid ? segment.Start.DistanceTo(mid) + mid.DistanceTo(segment.End) : segment.Start.DistanceTo(segment.End);
            if (length < options.ShortCurveMm)
                return (RegionCodes.LoopSegmentTooShort,
                    string.Format(CultureInfo.InvariantCulture, "Loop {0}, segment {1} ({2} to {3}) is {4:0.##} mm, shorter than Revit's {5:0.##} mm limit.",
                        number, i + 1, segment.Start, segment.End, length, options.ShortCurveMm));
        }

        if (FindSelfIntersection(polyline, options.TouchToleranceMm, out var near))
            return (RegionCodes.LoopSelfIntersecting, $"Loop {number} crosses or folds back on itself near {near}.");

        if (Math.Abs(RegionLoopMath.SignedArea(polyline)) < MinAreaMm2)
            return (RegionCodes.LoopDegenerate, $"Loop {number} has no area in this view's plane: its points lie on one line, seen in this view.");

        return null;
    }

    private static bool AllOnOneLine(List<Pt2> points, double tolerance)
    {
        var far = points.OrderByDescending(p => p.DistanceTo(points[0])).First();
        var along = far.Minus(points[0]);
        if (along.Length < 1e-9) return true;
        return points.All(p => Math.Abs(along.Cross(p.Minus(points[0]))) / along.Length <= tolerance);
    }

    private static bool FindSelfIntersection(List<Pt2> points, double tolerance, out Pt2 near)
    {
        near = default;
        var n = points.Count;
        for (var i = 0; i < n; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % n];
            var c = points[(i + 2) % n];
            // Neighbours share a point by construction, so only a fold back on the shared line is a fault.
            if (RegionLoopMath.DistanceToSegment(c, a, b) <= tolerance || RegionLoopMath.DistanceToSegment(a, b, c) <= tolerance)
            {
                near = b;
                return true;
            }

            for (var j = i + 2; j < n; j++)
            {
                if (i == 0 && j == n - 1) continue;   // neighbours through the wrap-around
                if (!RegionLoopMath.SegmentsTouch(a, b, points[j], points[(j + 1) % n], tolerance)) continue;
                near = points[j];
                return true;
            }
        }
        return false;
    }

    private static bool Cross(List<Pt2> first, List<Pt2> second, double tolerance, out Pt2 near)
    {
        near = default;
        for (var i = 0; i < first.Count; i++)
        for (var j = 0; j < second.Count; j++)
        {
            if (!RegionLoopMath.SegmentsTouch(first[i], first[(i + 1) % first.Count], second[j], second[(j + 1) % second.Count], tolerance)) continue;
            near = first[i];
            return true;
        }
        return false;
    }

    private static LoopValidation Fail(LoopValidation result, string code, string reason)
    {
        result.Code = code;
        result.Reason = reason;
        return result;
    }
}

/// <summary>What chaining loose pieces gave: closed loops, or why they do not close.</summary>
public sealed class ChainResult
{
    public List<RegionLoop> Loops { get; } = new();
    public string? Code { get; set; }
    public string? Reason { get; set; }
}

/// <summary>
/// Orders loose boundary pieces (detail lines in any order and direction) into closed loops. Ends within the tolerance
/// are snapped together, so Revit gets an exactly continuous loop. Pure.
/// </summary>
public static class LoopChainer
{
    public static ChainResult Chain(IReadOnlyList<LoopSegment> pieces, double joinToleranceMm)
    {
        var result = new ChainResult();
        if (pieces.Count == 0)
        {
            result.Code = RegionCodes.LoopEmpty;
            result.Reason = "No lines to build a boundary from.";
            return result;
        }

        var used = new bool[pieces.Count];
        for (var startIndex = 0; startIndex < pieces.Count; startIndex++)
        {
            if (used[startIndex]) continue;
            used[startIndex] = true;
            var chain = new List<LoopSegment> { pieces[startIndex] };
            var first = pieces[startIndex].Start;
            while (true)
            {
                var end = chain[chain.Count - 1].End;
                var candidates = new List<int>();
                for (var i = 0; i < pieces.Count; i++)
                    if (!used[i] && (pieces[i].Start.DistanceTo(end) <= joinToleranceMm || pieces[i].End.DistanceTo(end) <= joinToleranceMm))
                        candidates.Add(i);

                if (end.DistanceTo(first) <= joinToleranceMm && chain.Count > 1)
                {
                    if (candidates.Count > 0)
                    {
                        result.Code = RegionCodes.LoopBranching;
                        result.Reason = $"More than two lines meet at {end}, so the boundary is ambiguous.";
                        return result;
                    }
                    break;
                }
                if (candidates.Count == 0)
                {
                    result.Code = RegionCodes.LoopOpen;
                    result.Reason = $"The lines do not close: the chain from {first} ends at {end} with nothing to join.";
                    return result;
                }
                if (candidates.Count > 1)
                {
                    result.Code = RegionCodes.LoopBranching;
                    result.Reason = $"More than two lines meet at {end}, so the boundary is ambiguous.";
                    return result;
                }

                var next = pieces[candidates[0]];
                used[candidates[0]] = true;
                chain.Add(next.Start.DistanceTo(end) <= joinToleranceMm ? next : next.Reversed());
            }

            result.Loops.Add(Snap(chain));
        }
        return result;
    }

    private static RegionLoop Snap(List<LoopSegment> chain)
    {
        var snapped = new List<LoopSegment>(chain.Count);
        for (var i = 0; i < chain.Count; i++)
        {
            var start = chain[i].Start;
            var end = i + 1 < chain.Count ? chain[i + 1].Start : chain[0].Start;
            snapped.Add(chain[i].WithEnds(start, end));
        }
        return new RegionLoop(snapped);
    }
}

/// <summary>The padded loops, or why padding cannot be applied.</summary>
public sealed class OffsetResult
{
    public List<RegionLoop> Loops { get; } = new();
    public List<string> Warnings { get; } = new();
    public string? Code { get; set; }
    public string? Reason { get; set; }
}

/// <summary>
/// Grows (positive) or shrinks (negative) a region by moving every boundary with mitred corners: outlines outward and
/// holes into themselves. Takes loops as <see cref="LoopValidator"/> normalized them (outlines counter-clockwise, holes
/// clockwise), so "to the right of the direction of travel" is away from the region in both cases. Arcs are flattened
/// first. A hole that closes up is dropped with a warning; an outline that collapses is an error. Pure.
/// </summary>
public static class PolygonOffset
{
    public static OffsetResult Offset(IReadOnlyList<RegionLoop> loops, IReadOnlyList<int> depths, double distanceMm)
    {
        var result = new OffsetResult();
        var flattened = false;
        for (var index = 0; index < loops.Count; index++)
        {
            flattened |= loops[index].HasArcs;
            var points = loops[index].Polyline();
            var moved = Move(points, distanceMm);
            if (moved == null)
            {
                if (depths[index] % 2 == 1)
                {
                    result.Warnings.Add($"Padding of {distanceMm:0.#} mm closes hole {index + 1}: the hole is left out.");
                    continue;
                }
                result.Code = RegionCodes.PaddingCollapsed;
                result.Reason = $"Padding of {distanceMm:0.#} mm collapses the outline of loop {index + 1}.";
                return result;
            }

            var segments = new List<LoopSegment>(moved.Count);
            for (var i = 0; i < moved.Count; i++) segments.Add(new LoopSegment(moved[i], moved[(i + 1) % moved.Count]));
            result.Loops.Add(new RegionLoop(segments));
        }
        if (flattened) result.Warnings.Add("Padding flattens curved boundary pieces into short straight lines.");
        return result;
    }

    private static List<Pt2>? Move(List<Pt2> points, double distance)
    {
        var n = points.Count;
        var normals = new Pt2[n];
        for (var i = 0; i < n; i++)
        {
            var edge = points[(i + 1) % n].Minus(points[i]);
            var length = edge.Length;
            normals[i] = new Pt2(edge.V / length, -edge.U / length);   // right of the direction of travel
        }

        var moved = new List<Pt2>(n);
        for (var i = 0; i < n; i++)
        {
            var before = normals[(i + n - 1) % n];
            var after = normals[i];
            var denominator = 1 + before.Dot(after);
            if (denominator < 1e-6) return null;   // a hairpin has no mitre
            moved.Add(points[i].Plus(before.Plus(after).Scaled(distance / denominator)));
        }

        for (var i = 0; i < n; i++)
        {
            var oldEdge = points[(i + 1) % n].Minus(points[i]);
            var newEdge = moved[(i + 1) % n].Minus(moved[i]);
            if (oldEdge.Dot(newEdge) <= 0) return null;   // the edge turned around: the shape closed up here
        }
        return moved;
    }
}
