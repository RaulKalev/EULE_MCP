using RevitMCP.Addin.Documentation.Views;

namespace RevitMCP.Addin.FilledRegions;

/// <summary>A boundary piece in host model mm, before it is taken into a view's plane.</summary>
public sealed class ModelSegment
{
    public ModelSegment(Vec3 start, Vec3 end, Vec3? mid = null)
    {
        Start = start;
        End = end;
        Mid = mid;
    }

    public Vec3 Start { get; }
    public Vec3 End { get; }
    public Vec3? Mid { get; }
}

/// <summary>A shape taken into a view's plane: loops ready for <see cref="LoopValidator"/>, or a status code.</summary>
public sealed class ShapeResult
{
    public List<RegionLoop> Loops { get; } = new();
    public List<string> Warnings { get; } = new();
    public string? Code { get; set; }
    public string? Reason { get; set; }

    /// <summary>The first loop must be the outline and the others holes in it (direct coordinates).</summary>
    public bool StrictNesting { get; set; }

    public static ShapeResult Fail(string code, string reason) => new() { Code = code, Reason = reason };
}

/// <summary>
/// Takes shapes into a view's drawing plane, the same way for every view kind: a model point is projected along the view
/// direction, which in a plan drops z and in a section keeps x/y along the section line and z as height. Boundaries read
/// from the model (rooms, detail lines) have to lie in a plane parallel to the view, otherwise projecting them would draw
/// something else than the source; they are refused with boundary_not_in_view_plane. Pure.
/// </summary>
public static class RegionShapeBuilder
{
    /// <summary>How far a boundary may stray from one plane parallel to the view before it counts as not parallel.</summary>
    public const double PlaneToleranceMm = 1.0;

    public static ShapeResult FromLoops(RegionShapeSpec shape, ViewFrame frame, double joinToleranceMm)
    {
        var result = new ShapeResult { StrictNesting = true };
        var depths = new List<double>();
        for (var loopIndex = 0; loopIndex < shape.Loops.Count; loopIndex++)
        {
            var points = shape.Loops[loopIndex];
            if (points.Count < 3)
                return ShapeResult.Fail(RegionCodes.LoopTooFewPoints, $"Loop {loopIndex + 1} has {points.Count} point(s); a boundary needs at least 3.");

            var projected = new List<(Pt2 Point, Pt2? Mid)>(points.Count);
            foreach (var point in points)
            {
                if (!TryProject(point, shape.ViewSpace, frame, depths, out var uv, out var problem)) return problem!;
                Pt2? mid = null;
                if (point.ArcMid != null)
                {
                    if (!TryProject(point.ArcMid, shape.ViewSpace, frame, depths, out var midUv, out problem)) return problem!;
                    mid = midUv;
                }
                projected.Add((uv, mid));
            }

            var closesItself = projected[0].Point.DistanceTo(projected[projected.Count - 1].Point) <= joinToleranceMm;
            if (!closesItself && !shape.CloseLoops)
                return ShapeResult.Fail(RegionCodes.LoopOpen,
                    $"Loop {loopIndex + 1} is open: it ends at {projected[projected.Count - 1].Point}, not at its first point {projected[0].Point}. " +
                    "Repeat the first point at the end, or pass closeLoops=true.");

            var count = closesItself ? projected.Count - 1 : projected.Count;
            if (count < 3 && !projected.Take(count).Any(p => p.Mid != null))
                return ShapeResult.Fail(RegionCodes.LoopTooFewPoints, $"Loop {loopIndex + 1} has {count} distinct point(s); a boundary needs at least 3.");

            var segments = new List<LoopSegment>(count);
            for (var i = 0; i < count; i++)
                segments.Add(new LoopSegment(projected[i].Point, projected[(i + 1) % count].Point, projected[i].Mid));
            result.Loops.Add(new RegionLoop(segments));
        }

        if (depths.Count > 0 && depths.Max() - depths.Min() > PlaneToleranceMm)
            result.Warnings.Add($"The points are {depths.Max() - depths.Min():0.#} mm apart in the view direction; they were projected onto the view's plane.");
        return result;
    }

    public static ShapeResult FromRectangle(RegionShapeSpec shape, ViewFrame frame)
    {
        var depths = new List<double>();
        double minU, minV, maxU, maxV;
        if (shape.Center != null)
        {
            if (!TryProject(shape.Center, shape.ViewSpace, frame, depths, out var center, out var problem)) return problem!;
            var halfWidth = (shape.WidthMm ?? 0) / 2;
            var halfHeight = (shape.HeightMm ?? 0) / 2;
            minU = center.U - halfWidth;
            maxU = center.U + halfWidth;
            minV = center.V - halfHeight;
            maxV = center.V + halfHeight;
        }
        else
        {
            if (shape.From == null || shape.To == null)
                return ShapeResult.Fail(RegionCodes.InvalidSpec, "rectangle needs {from, to} or {center, widthMm, heightMm}.");
            if (!TryProject(shape.From, shape.ViewSpace, frame, depths, out var from, out var problem)) return problem!;
            if (!TryProject(shape.To, shape.ViewSpace, frame, depths, out var to, out problem)) return problem!;
            minU = Math.Min(from.U, to.U);
            maxU = Math.Max(from.U, to.U);
            minV = Math.Min(from.V, to.V);
            maxV = Math.Max(from.V, to.V);
        }

        var result = new ShapeResult();
        result.Loops.Add(Rectangle(minU, minV, maxU, maxV));
        return result;
    }

    /// <summary>The (u, v) bounds of the corners of a box or any point set: a rectangle along the view's own axes.</summary>
    public static ShapeResult FromCorners(IReadOnlyList<Vec3> corners, ViewFrame frame)
    {
        if (corners.Count == 0) return ShapeResult.Fail(RegionCodes.NoBoundingBox, "The element has no bounding box.");
        var uv = corners.Select(frame.ToUv).ToList();
        var result = new ShapeResult();
        result.Loops.Add(Rectangle(uv.Min(p => p.U), uv.Min(p => p.V), uv.Max(p => p.U), uv.Max(p => p.V)));
        return result;
    }

    /// <summary>Boundaries read loop by loop from the model (a room): each piece list is one closed loop, already in order.</summary>
    public static ShapeResult FromModelLoops(IReadOnlyList<IReadOnlyList<ModelSegment>> loops, ViewFrame frame, string sourceLabel)
    {
        var problem = CheckInPlane(loops.SelectMany(l => l), frame, sourceLabel);
        if (problem != null) return problem;

        var result = new ShapeResult();
        foreach (var loop in loops)
            result.Loops.Add(new RegionLoop(loop.Select(s => Project(s, frame))));
        return result;
    }

    /// <summary>Loose pieces (detail lines) in any order and direction: chained into closed loops first.</summary>
    public static ShapeResult FromLoosePieces(IReadOnlyList<ModelSegment> pieces, ViewFrame frame, double joinToleranceMm, string sourceLabel)
    {
        var problem = CheckInPlane(pieces, frame, sourceLabel);
        if (problem != null) return problem;

        var chained = LoopChainer.Chain(pieces.Select(s => Project(s, frame)).ToList(), joinToleranceMm);
        if (chained.Code != null) return ShapeResult.Fail(chained.Code, chained.Reason!);

        var result = new ShapeResult();
        result.Loops.AddRange(chained.Loops);
        return result;
    }

    private static ShapeResult? CheckInPlane(IEnumerable<ModelSegment> segments, ViewFrame frame, string sourceLabel)
    {
        var depths = new List<double>();
        foreach (var segment in segments)
        {
            depths.Add(frame.Depth(segment.Start));
            depths.Add(frame.Depth(segment.End));
            if (segment.Mid is { } mid) depths.Add(frame.Depth(mid));
        }
        if (depths.Count == 0) return ShapeResult.Fail(RegionCodes.LoopEmpty, $"{sourceLabel} has no boundary.");
        var spread = depths.Max() - depths.Min();
        if (spread <= PlaneToleranceMm) return null;
        return ShapeResult.Fail(RegionCodes.BoundaryNotInViewPlane,
            $"{sourceLabel} does not lie in a plane parallel to this view: its points are {spread:0} mm apart along the view direction. " +
            "Draw it in a plan-like view, or give the shape as loops or a rectangle for this view.");
    }

    private static LoopSegment Project(ModelSegment segment, ViewFrame frame)
    {
        Pt2? mid = segment.Mid is { } m ? frame.ToUv(m) : null;
        return new LoopSegment(frame.ToUv(segment.Start), frame.ToUv(segment.End), mid);
    }

    private static RegionLoop Rectangle(double minU, double minV, double maxU, double maxV)
    {
        var a = new Pt2(minU, minV);
        var b = new Pt2(maxU, minV);
        var c = new Pt2(maxU, maxV);
        var d = new Pt2(minU, maxV);
        return new RegionLoop(new[] { new LoopSegment(a, b), new LoopSegment(b, c), new LoopSegment(c, d), new LoopSegment(d, a) });
    }

    private static bool TryProject(ShapePoint point, bool viewSpace, ViewFrame frame, List<double> depths, out Pt2 uv, out ShapeResult? problem)
    {
        problem = null;
        uv = default;
        if (viewSpace)
        {
            uv = new Pt2(point.U ?? 0, point.V ?? 0);
            return true;
        }
        if (point.Z == null && !frame.IsHorizontal)
        {
            problem = ShapeResult.Fail(RegionCodes.CoordinateZRequired,
                $"Point ({point.X:0.#}, {point.Y:0.#}) has no z, and this view is not plan-like, so the height matters. " +
                "Give z, or use space=\"view\" with {u, v} along the view's right and up directions.");
            return false;
        }
        var model = new Vec3(point.X ?? 0, point.Y ?? 0, point.Z ?? 0);
        uv = frame.ToUv(model);
        if (point.Z != null || !frame.IsHorizontal) depths.Add(frame.Depth(model));
        return true;
    }
}

/// <summary>A shape run through validation and padding: the one path every region takes, whatever its source.</summary>
public sealed class FinishedShape
{
    public LoopValidation Validation { get; init; } = new();
    public List<string> Warnings { get; } = new();
    public bool IsValid => Validation.IsValid;
}

/// <summary>Validates a built shape, applies padding, and validates the padded result. Pure.</summary>
public static class RegionShapeFinisher
{
    public static FinishedShape Finish(ShapeResult shape, double paddingMm, double shortCurveMm)
    {
        if (shape.Code != null)
        {
            var failed = new FinishedShape { Validation = new LoopValidation { Code = shape.Code, Reason = shape.Reason } };
            failed.Warnings.AddRange(shape.Warnings);
            return failed;
        }

        var options = new LoopValidationOptions { ShortCurveMm = shortCurveMm, AllowDisjoint = !shape.StrictNesting };
        var validation = LoopValidator.Validate(shape.Loops, options);
        var warnings = new List<string>(shape.Warnings);

        if (validation.IsValid && Math.Abs(paddingMm) > 1e-9)
        {
            var offset = PolygonOffset.Offset(validation.Loops, validation.Depths, paddingMm);
            warnings.AddRange(offset.Warnings);
            validation = offset.Code != null
                ? new LoopValidation { Code = offset.Code, Reason = offset.Reason }
                : LoopValidator.Validate(offset.Loops, options);
        }

        var finished = new FinishedShape { Validation = validation };
        finished.Warnings.AddRange(warnings);
        return finished;
    }
}
