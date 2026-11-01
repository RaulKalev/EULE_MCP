using RevitMCP.Addin.Documentation.Views;

namespace RevitMCP.Addin.FilledRegions;

/// <summary>A point in a view's plane, in millimetres along the view's right (U) and up (V) directions. No Revit types.</summary>
public readonly struct Pt2
{
    public Pt2(double u, double v)
    {
        U = u;
        V = v;
    }

    public double U { get; }
    public double V { get; }

    public Pt2 Plus(Pt2 o) => new(U + o.U, V + o.V);
    public Pt2 Minus(Pt2 o) => new(U - o.U, V - o.V);
    public Pt2 Scaled(double k) => new(U * k, V * k);
    public double Dot(Pt2 o) => U * o.U + V * o.V;
    public double Cross(Pt2 o) => U * o.V - V * o.U;
    public double Length => Math.Sqrt(U * U + V * V);
    public double DistanceTo(Pt2 o) => Minus(o).Length;

    public override string ToString() =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, "({0:0.#}, {1:0.#})", U, V);
}

/// <summary>One boundary piece in the view plane: a straight line, or an arc through <see cref="Mid"/>.</summary>
public sealed class LoopSegment
{
    public LoopSegment(Pt2 start, Pt2 end, Pt2? mid = null)
    {
        Start = start;
        End = end;
        Mid = mid;
    }

    public Pt2 Start { get; }
    public Pt2 End { get; }
    public Pt2? Mid { get; }
    public bool IsArc => Mid != null;

    public LoopSegment Reversed() => new(End, Start, Mid);

    /// <summary>The same piece with its ends moved onto exact shared points (a chain's snap), keeping the arc point.</summary>
    public LoopSegment WithEnds(Pt2 start, Pt2 end) => new(start, end, Mid);
}

/// <summary>One closed boundary in the view plane.</summary>
public sealed class RegionLoop
{
    public RegionLoop(IEnumerable<LoopSegment> segments) => Segments = segments.ToList();

    public List<LoopSegment> Segments { get; }

    /// <summary>The vertices of the loop with arcs flattened, without repeating the first point at the end.</summary>
    public List<Pt2> Polyline()
    {
        var points = new List<Pt2>();
        foreach (var segment in Segments)
        {
            points.Add(segment.Start);
            if (segment.Mid is { } mid)
                points.AddRange(RegionLoopMath.ArcInteriorPoints(segment.Start, mid, segment.End));
        }

        // Pieces shorter than a micrometre are rounding noise, not geometry.
        var clean = new List<Pt2>(points.Count);
        foreach (var point in points)
            if (clean.Count == 0 || clean[clean.Count - 1].DistanceTo(point) > 1e-6) clean.Add(point);
        if (clean.Count > 1 && clean[0].DistanceTo(clean[clean.Count - 1]) <= 1e-6) clean.RemoveAt(clean.Count - 1);
        return clean;
    }

    public RegionLoop Reversed()
    {
        var reversed = new List<LoopSegment>(Segments.Count);
        for (var i = Segments.Count - 1; i >= 0; i--) reversed.Add(Segments[i].Reversed());
        return new RegionLoop(reversed);
    }

    public bool HasArcs => Segments.Any(s => s.IsArc);
}

/// <summary>Plane geometry on flat loops. Pure.</summary>
public static class RegionLoopMath
{
    /// <summary>Largest angle between two flattened points of an arc (7.5 degrees).</summary>
    private const double ArcStepRadians = Math.PI / 24;

    /// <summary>The points strictly between the ends of the arc from start through mid to end; none when the three are collinear.</summary>
    public static List<Pt2> ArcInteriorPoints(Pt2 start, Pt2 mid, Pt2 end)
    {
        var result = new List<Pt2>();
        var ax = mid.U - start.U;
        var ay = mid.V - start.V;
        var bx = end.U - start.U;
        var by = end.V - start.V;
        var d = 2 * (ax * by - ay * bx);
        if (Math.Abs(d) < 1e-9)
        {
            result.Add(mid);
            return result;
        }

        var aSq = ax * ax + ay * ay;
        var bSq = bx * bx + by * by;
        var cu = (by * aSq - ay * bSq) / d;
        var cv = (ax * bSq - bx * aSq) / d;
        var center = new Pt2(start.U + cu, start.V + cv);
        var radius = center.DistanceTo(start);

        var a0 = Math.Atan2(start.V - center.V, start.U - center.U);
        var aMid = Math.Atan2(mid.V - center.V, mid.U - center.U);
        var a1 = Math.Atan2(end.V - center.V, end.U - center.U);
        var ccwToMid = NormalizePositive(aMid - a0);
        var ccwToEnd = NormalizePositive(a1 - a0);
        var sweep = ccwToMid <= ccwToEnd ? ccwToEnd : ccwToEnd - 2 * Math.PI;
        var steps = Math.Max(4, (int)Math.Ceiling(Math.Abs(sweep) / ArcStepRadians));
        for (var i = 1; i < steps; i++)
        {
            var angle = a0 + sweep * i / steps;
            result.Add(new Pt2(center.U + radius * Math.Cos(angle), center.V + radius * Math.Sin(angle)));
        }
        return result;
    }

    private static double NormalizePositive(double angle)
    {
        while (angle < 0) angle += 2 * Math.PI;
        while (angle >= 2 * Math.PI) angle -= 2 * Math.PI;
        return angle;
    }

    /// <summary>Positive when the points run counter-clockwise (seen with U to the right and V up).</summary>
    public static double SignedArea(IReadOnlyList<Pt2> points)
    {
        double sum = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            sum += a.U * b.V - b.U * a.V;
        }
        return sum / 2;
    }

    /// <summary>Even-odd point in polygon. A point on the edge can go either way; callers test points known to be off it.</summary>
    public static bool Contains(IReadOnlyList<Pt2> polygon, Pt2 p)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];
            if ((a.V > p.V) != (b.V > p.V) && p.U < (b.U - a.U) * (p.V - a.V) / (b.V - a.V) + a.U)
                inside = !inside;
        }
        return inside;
    }

    public static double DistanceToSegment(Pt2 p, Pt2 a, Pt2 b)
    {
        var ab = b.Minus(a);
        var lengthSq = ab.Dot(ab);
        if (lengthSq < 1e-18) return p.DistanceTo(a);
        var t = Math.Max(0, Math.Min(1, p.Minus(a).Dot(ab) / lengthSq));
        return p.DistanceTo(a.Plus(ab.Scaled(t)));
    }

    /// <summary>True when segment ab crosses or comes within tolerance of segment cd.</summary>
    public static bool SegmentsTouch(Pt2 a, Pt2 b, Pt2 c, Pt2 d, double tolerance)
    {
        var ab = b.Minus(a);
        var cd = d.Minus(c);
        var d1 = ab.Cross(c.Minus(a));
        var d2 = ab.Cross(d.Minus(a));
        var d3 = cd.Cross(a.Minus(c));
        var d4 = cd.Cross(b.Minus(c));
        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
            return true;
        return DistanceToSegment(c, a, b) <= tolerance || DistanceToSegment(d, a, b) <= tolerance ||
               DistanceToSegment(a, c, d) <= tolerance || DistanceToSegment(b, c, d) <= tolerance;
    }
}

/// <summary>
/// A view's drawing plane as plain vectors (View.Origin, RightDirection, UpDirection, ViewDirection, in mm). The (u, v)
/// of a model point are its dot products with the right and up directions, so u/v do not depend on where the view's
/// origin happens to be: in a plan that is not rotated u = x and v = y, in a section v = the elevation.
/// </summary>
public sealed class ViewFrame
{
    public ViewFrame(Vec3 origin, Vec3 right, Vec3 up, Vec3 normal)
    {
        Origin = origin;
        Right = right;
        Up = up;
        Normal = normal;
    }

    public Vec3 Origin { get; }
    public Vec3 Right { get; }
    public Vec3 Up { get; }

    /// <summary>The view direction: perpendicular to the drawing plane.</summary>
    public Vec3 Normal { get; }

    /// <summary>A plan-like view (floor, ceiling, area, drafting, sheet): the plane is horizontal.</summary>
    public bool IsHorizontal => Math.Abs(Normal.Z) > 0.999;

    public Pt2 ToUv(Vec3 model) => new(model.Dot(Right), model.Dot(Up));

    /// <summary>Distance of a model point from the drawing plane.</summary>
    public double Depth(Vec3 model) => model.Minus(Origin).Dot(Normal);

    /// <summary>The point on the drawing plane with these (u, v).</summary>
    public Vec3 ToModel(Pt2 uv) => Normal.Scaled(Origin.Dot(Normal)).Plus(Right.Scaled(uv.U)).Plus(Up.Scaled(uv.V));
}
