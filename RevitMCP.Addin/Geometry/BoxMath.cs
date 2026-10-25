using RevitMCP.Addin.Coordination.Clash.Geometry;
using RevitMCP.Addin.Placement;

namespace RevitMCP.Addin.Geometry;

/// <summary>An axis-aligned box: <see cref="Min"/> is never above <see cref="Max"/> on any axis (see <see cref="BoxMath.FromCorners"/>).</summary>
public readonly struct BoxMm
{
    public BoxMm(Vec3 min, Vec3 max)
    {
        Min = min;
        Max = max;
    }

    public Vec3 Min { get; }
    public Vec3 Max { get; }

    public Vec3 Size => Max.Minus(Min);
    public Vec3 Center => Min.Plus(Max).Scaled(0.5);
    public Vec3 HalfExtents => Size.Scaled(0.5);

    public override string ToString() => $"[{Min} .. {Max}]";
}

/// <summary>
/// Pure box maths for the geometry queries (#107): boxes from a point and radius, padding, point-to-box distance,
/// and the two box tests the collectors use (intersects, inside). Units are whatever the caller uses consistently —
/// the tools pass mm. No Revit API; unit tested.
/// </summary>
public static class BoxMath
{
    /// <summary>The box spanned by two corners given in any order.</summary>
    public static BoxMm FromCorners(Vec3 a, Vec3 b) => new(
        new Vec3(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)),
        new Vec3(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)));

    /// <summary>The smallest box holding every point (e.g. the eight transformed corners of a box).</summary>
    public static BoxMm Enclosing(IEnumerable<Vec3> points)
    {
        var any = false;
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (var p in points)
        {
            any = true;
            minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); minZ = Math.Min(minZ, p.Z);
            maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); maxZ = Math.Max(maxZ, p.Z);
        }
        if (!any) throw new ArgumentException("At least one point is needed.", nameof(points));
        return new BoxMm(new Vec3(minX, minY, minZ), new Vec3(maxX, maxY, maxZ));
    }

    /// <summary>The eight corners of a box.</summary>
    public static IReadOnlyList<Vec3> Corners(BoxMm box)
    {
        var corners = new List<Vec3>(8);
        foreach (var x in new[] { box.Min.X, box.Max.X })
        foreach (var y in new[] { box.Min.Y, box.Max.Y })
        foreach (var z in new[] { box.Min.Z, box.Max.Z })
            corners.Add(new Vec3(x, y, z));
        return corners;
    }

    /// <summary>The cube of side 2·<paramref name="radius"/> centred on <paramref name="center"/>: the prefilter of a radius search.</summary>
    public static BoxMm FromPointRadius(Vec3 center, double radius)
    {
        var r = Math.Abs(radius);
        return new BoxMm(
            new Vec3(center.X - r, center.Y - r, center.Z - r),
            new Vec3(center.X + r, center.Y + r, center.Z + r));
    }

    /// <summary>
    /// The box grown by <paramref name="padding"/> on every side; a negative padding shrinks it, but never past its
    /// centre (an axis shrunk to nothing collapses onto the centre rather than turning inside out).
    /// </summary>
    public static BoxMm Pad(BoxMm box, double padding)
    {
        var (mnX, mnY, mnZ, mxX, mxY, mxZ) = ClashBoundingBoxMath.Expand(
            box.Min.X, box.Min.Y, box.Min.Z, box.Max.X, box.Max.Y, box.Max.Z, padding);
        var c = box.Center;
        if (mnX > mxX) mnX = mxX = c.X;
        if (mnY > mxY) mnY = mxY = c.Y;
        if (mnZ > mxZ) mnZ = mxZ = c.Z;
        return new BoxMm(new Vec3(mnX, mnY, mnZ), new Vec3(mxX, mxY, mxZ));
    }

    /// <summary>Distance from a point to the nearest point of a box; 0 when the point is inside or on it.</summary>
    public static double DistancePointToBox(Vec3 point, BoxMm box) =>
        ClashBoundingBoxMath.DistancePointToBox(
            point.X, point.Y, point.Z, box.Min.X, box.Min.Y, box.Min.Z, box.Max.X, box.Max.Y, box.Max.Z);

    /// <summary>Distance between two points.</summary>
    public static double Distance(Vec3 a, Vec3 b) => a.Minus(b).Length;

    /// <summary>True when the boxes share at least one point (touching counts), as Revit's BoundingBoxIntersectsFilter.</summary>
    public static bool Intersects(BoxMm a, BoxMm b) =>
        ClashBoundingBoxMath.Overlaps(
            a.Min.X, a.Min.Y, a.Min.Z, a.Max.X, a.Max.Y, a.Max.Z,
            b.Min.X, b.Min.Y, b.Min.Z, b.Max.X, b.Max.Y, b.Max.Z);

    /// <summary>True when <paramref name="inner"/> lies wholly inside <paramref name="outer"/> (its faces may touch), as BoundingBoxIsInsideFilter.</summary>
    public static bool Inside(BoxMm inner, BoxMm outer, double tolerance = 1e-9) =>
        inner.Min.X >= outer.Min.X - tolerance && inner.Min.Y >= outer.Min.Y - tolerance && inner.Min.Z >= outer.Min.Z - tolerance &&
        inner.Max.X <= outer.Max.X + tolerance && inner.Max.Y <= outer.Max.Y + tolerance && inner.Max.Z <= outer.Max.Z + tolerance;

    /// <summary>The test a collector mode asks for: <c>inside</c> or (default) <c>intersects</c>.</summary>
    public static bool Matches(string mode, BoxMm element, BoxMm query) =>
        string.Equals(mode, ModeInside, StringComparison.OrdinalIgnoreCase) ? Inside(element, query) : Intersects(element, query);

    public const string ModeIntersects = "intersects";
    public const string ModeInside = "inside";

    /// <summary>True for a known collector mode.</summary>
    public static bool IsKnownMode(string mode) =>
        string.Equals(mode, ModeIntersects, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mode, ModeInside, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the box has a non-negative extent on every axis and finite coordinates.</summary>
    public static bool IsValid(BoxMm box) =>
        IsFinite(box.Min) && IsFinite(box.Max) &&
        box.Min.X <= box.Max.X && box.Min.Y <= box.Max.Y && box.Min.Z <= box.Max.Z;

    private static bool IsFinite(Vec3 v) =>
        !double.IsNaN(v.X) && !double.IsInfinity(v.X) &&
        !double.IsNaN(v.Y) && !double.IsInfinity(v.Y) &&
        !double.IsNaN(v.Z) && !double.IsInfinity(v.Z);
}
