using static RevitMCP.Addin.ModelFamilies.ModelFamilyFormGeometry;

namespace RevitMCP.Addin.ModelFamilies;

/// <summary>
/// One source curve of a profile analysis (#177): a line or an arc through three points, in family millimetres, with
/// the CAD layer (or line style) it was drawn on and the element it came from. Splines and ellipses arrive as several
/// lines marked <see cref="Approximated"/>.
/// </summary>
public sealed class ModelFamilyProfileCurve
{
    public ModelFamilyFormSegmentKind Kind { get; init; }
    public double[] Start { get; init; } = Vec(0, 0);
    /// <summary>A point on an arc between start and end; null for a line.</summary>
    public double[]? Mid { get; init; }
    public double[] End { get; init; } = Vec(0, 0);
    public string Layer { get; init; } = string.Empty;
    /// <summary>The element the curve came from: the import, or the family curve; null when unknown.</summary>
    public long? SourceId { get; init; }
    /// <summary>True for a piece of a tessellated spline, ellipse or elliptical arc.</summary>
    public bool Approximated { get; init; }

    public static ModelFamilyProfileCurve Line(double[] start, double[] end, string layer = "", long? sourceId = null, bool approximated = false) =>
        new() { Kind = ModelFamilyFormSegmentKind.Line, Start = Copy(start), End = Copy(end), Layer = layer, SourceId = sourceId, Approximated = approximated };

    public static ModelFamilyProfileCurve Arc(double[] start, double[] mid, double[] end, string layer = "", long? sourceId = null) =>
        new() { Kind = ModelFamilyFormSegmentKind.Arc, Start = Copy(start), Mid = Copy(mid), End = Copy(end), Layer = layer, SourceId = sourceId };

    /// <summary>
    /// A full circle as two half arcs (Revit cannot make a closed curve from one arc): centre, radius and the circle's
    /// own x and y directions (unit vectors in its plane).
    /// </summary>
    public static List<ModelFamilyProfileCurve> Circle(double[] center, double[] xDirection, double[] yDirection, double radius,
        string layer = "", long? sourceId = null)
    {
        var east = Add(center, Scale(xDirection, radius));
        var north = Add(center, Scale(yDirection, radius));
        var west = Sub(center, Scale(xDirection, radius));
        var south = Sub(center, Scale(yDirection, radius));
        return new List<ModelFamilyProfileCurve> { Arc(east, north, west, layer, sourceId), Arc(west, south, east, layer, sourceId) };
    }

    /// <summary>
    /// Points of a polyline (a CAD polyline, or a tessellated spline or ellipse) as lines. Points closer than
    /// <paramref name="minSpacing"/> to the last kept one are dropped, so a fine tessellation does not make segments
    /// shorter than Revit accepts; the last point is always kept.
    /// </summary>
    public static List<ModelFamilyProfileCurve> Polyline(IReadOnlyList<double[]> points, string layer = "", long? sourceId = null,
        bool approximated = false, double minSpacing = 0)
    {
        var result = new List<ModelFamilyProfileCurve>();
        if (points.Count < 2) return result;
        var kept = new List<double[]> { points[0] };
        for (var i = 1; i < points.Count - 1; i++)
            if (Distance(points[i], kept[kept.Count - 1]) >= minSpacing && Distance(points[i], points[points.Count - 1]) >= minSpacing)
                kept.Add(points[i]);
        kept.Add(points[points.Count - 1]);
        for (var i = 1; i < kept.Count; i++)
            result.Add(Line(kept[i - 1], kept[i], layer, sourceId, approximated));
        return result;
    }

    /// <summary>Every point the curve is given with (start, mid, end), for plane fitting and the off-plane check.</summary>
    public IEnumerable<double[]> Points()
    {
        yield return Start;
        if (Mid != null) yield return Mid;
        yield return End;
    }
}

/// <summary>
/// The plane a profile is analysed on (#177), as plain doubles. The axes follow the family's axes so that a loop's
/// width and height mean the same on every call: on a horizontal plane x is family X and y family Y; on a plane facing
/// front or back x is X and y is Z; on a plane facing left or right x is Y and y is Z.
/// </summary>
public static class ModelFamilyProfilePlane
{
    /// <summary>A normal within this angle (radians, about 0.006°) of a family axis is snapped onto it.</summary>
    public const double SnapRadians = 1e-4;

    /// <summary>The analysis frame of a plane: origin is the plane's point nearest the family origin; axes by the rule above.</summary>
    public static ModelFamilyFormFrame FrameFor(double[] pointOnPlane, double[] normal)
    {
        var n = Normalize(Copy(normal));
        var ax = Math.Abs(n[0]); var ay = Math.Abs(n[1]); var az = Math.Abs(n[2]);
        double[] x, y;
        if (az >= ax && az >= ay) { x = Vec(1, 0, 0); y = Vec(0, 1, 0); }
        else if (ay >= ax) { x = Vec(1, 0, 0); y = Vec(0, 0, 1); }
        else { x = Vec(0, 1, 0); y = Vec(0, 0, 1); }
        var xOnPlane = Normalize(Sub(x, Scale(n, Dot(x, n))));
        var yOnPlane = Sub(Sub(y, Scale(n, Dot(y, n))), Scale(xOnPlane, Dot(y, xOnPlane)));
        var origin = Scale(n, Dot(Copy(pointOnPlane), n));
        return new ModelFamilyFormFrame(origin, xOnPlane, Normalize(yOnPlane));
    }

    /// <summary>
    /// The plane through a set of points: three far-apart points span it (the lowest point, the one farthest from it,
    /// and the one farthest from the line through both). A normal within <see cref="SnapRadians"/> of a family axis is
    /// snapped onto it and points to the positive side of its largest component. <paramref name="spread"/> is the
    /// distance between the two points furthest apart along the normal. Null when the points lie on one line.
    /// </summary>
    public static (double[] Origin, double[] Normal)? Fit(IReadOnlyList<double[]> points, out double spread)
    {
        spread = 0;
        if (points.Count < 3) return null;
        var p0 = points.OrderBy(p => p[0]).ThenBy(p => p[1]).ThenBy(p => p[2]).First();
        var p1 = points.OrderByDescending(p => Distance(p, p0)).First();
        var axis = Normalize(Sub(p1, p0));
        if (Length(axis) < 1e-12) return null;
        double OffLine(double[] p)
        {
            var d = Sub(p, p0);
            return Length(Sub(d, Scale(axis, Dot(d, axis))));
        }
        var p2 = points.OrderByDescending(OffLine).First();
        if (OffLine(p2) < 1e-6) return null;
        var normal = Normalize(Cross(Sub(p1, p0), Sub(p2, p0)));
        normal = Snap(normal);
        var offsets = points.Select(p => Dot(p, normal)).ToList();
        var min = offsets.Min();
        var max = offsets.Max();
        spread = max - min;
        return (Scale(normal, (min + max) / 2), normal);
    }

    /// <summary>A unit normal snapped to a family axis when it is within <see cref="SnapRadians"/>, largest component positive.</summary>
    public static double[] Snap(double[] normal)
    {
        var n = Normalize(Copy(normal));
        var largest = 0;
        for (var i = 1; i < 3; i++) if (Math.Abs(n[i]) > Math.Abs(n[largest])) largest = i;
        if (n[largest] < 0) n = Scale(n, -1);
        if (Math.Acos(Math.Min(1, Math.Abs(n[largest]))) < SnapRadians)
        {
            n = Vec(0, 0, 0);
            n[largest] = 1;
        }
        return n;
    }

    /// <summary>Whether two unit normals are parallel (either sense) within <see cref="SnapRadians"/>.</summary>
    public static bool Parallel(double[] a, double[] b) => Math.Abs(Dot(Normalize(Copy(a)), Normalize(Copy(b)))) > Math.Cos(SnapRadians);
}
