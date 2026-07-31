namespace RevitMCP.Addin.Placement;

/// <summary>
/// A minimal 3D vector so the alignment maths stay free of the Revit API and can be unit tested.
/// Converted to and from <c>Autodesk.Revit.DB.XYZ</c> at the tool boundary.
/// </summary>
public readonly struct Vec3
{
    public Vec3(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public double X { get; }
    public double Y { get; }
    public double Z { get; }

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

    public Vec3 Normalized()
    {
        var length = Length;
        return length < AlignmentMath.Tolerance ? new Vec3(0, 0, 0) : new Vec3(X / length, Y / length, Z / length);
    }

    public double Dot(Vec3 other) => X * other.X + Y * other.Y + Z * other.Z;

    public Vec3 Cross(Vec3 other) => new(
        Y * other.Z - Z * other.Y,
        Z * other.X - X * other.Z,
        X * other.Y - Y * other.X);

    public Vec3 Plus(Vec3 other) => new(X + other.X, Y + other.Y, Z + other.Z);

    public Vec3 Minus(Vec3 other) => new(X - other.X, Y - other.Y, Z - other.Z);

    public Vec3 Scaled(double factor) => new(X * factor, Y * factor, Z * factor);

    public Vec3 Negated() => new(-X, -Y, -Z);

    public override string ToString() =>
        $"({X:F4}, {Y:F4}, {Z:F4})";
}

/// <summary>
/// The geometry behind "move this element until it sits against the nearest wall/ceiling/floor":
/// which directions to search, how far an element reaches in a direction, what a hit surface is,
/// and how far to travel to land flush against it.
///
/// Surfaces are classified by the orientation of the hit face, never by category — linked IFC
/// models routinely map walls and slabs onto Generic Models or other unexpected categories.
/// </summary>
public static class AlignmentMath
{
    public const double Tolerance = 1e-9;

    /// <summary>Narrowest and widest tolerance for calling a face a wall, ceiling, or floor.</summary>
    public const double MinAngleToleranceDegrees = 1.0;
    public const double MaxAngleToleranceDegrees = 44.0;

    public const string SurfaceWall = "wall";
    public const string SurfaceCeiling = "ceiling";
    public const string SurfaceFloor = "floor";
    public const string SurfaceNearest = "nearest";
    public const string SurfaceOther = "other";

    /// <summary>Unit +Z, the model's up direction.</summary>
    public static readonly Vec3 Up = new(0, 0, 1);

    public static bool IsKnownSurface(string surface) =>
        surface is SurfaceWall or SurfaceCeiling or SurfaceFloor or SurfaceNearest;

    public static double ClampAngleTolerance(double degrees) =>
        Math.Min(MaxAngleToleranceDegrees, Math.Max(MinAngleToleranceDegrees, degrees));

    /// <summary>
    /// The directions to cast for a requested surface: up for a ceiling, down for a floor, a ring
    /// of horizontal directions for a wall, and all of them for "nearest".
    /// <paramref name="preferred"/> — typically a family instance's facing direction — is placed
    /// first so a device already pointing away from its wall resolves on the first ray.
    /// </summary>
    public static List<Vec3> SearchDirections(string surface, int horizontalSamples, Vec3? preferred)
    {
        var directions = new List<Vec3>();

        switch (surface)
        {
            case SurfaceCeiling:
                directions.Add(Up);
                break;
            case SurfaceFloor:
                directions.Add(Up.Negated());
                break;
            case SurfaceWall:
                AddHorizontal(directions, horizontalSamples, preferred);
                break;
            case SurfaceNearest:
                AddHorizontal(directions, horizontalSamples, preferred);
                directions.Add(Up);
                directions.Add(Up.Negated());
                break;
        }

        return directions;
    }

    /// <summary>Evenly spaced unit directions in the XY plane, starting at +X.</summary>
    public static List<Vec3> HorizontalRing(int samples)
    {
        var count = Math.Max(1, samples);
        var directions = new List<Vec3>(count);
        for (var index = 0; index < count; index++)
        {
            var angle = 2.0 * Math.PI * index / count;
            directions.Add(new Vec3(Math.Cos(angle), Math.Sin(angle), 0));
        }
        return directions;
    }

    /// <summary>
    /// How far an axis-aligned box of the given half extents reaches from its centre along
    /// <paramref name="direction"/>. Exact for an AABB — it is the box's support function.
    /// </summary>
    public static double SupportDistance(Vec3 halfExtents, Vec3 direction)
    {
        var unit = direction.Normalized();
        return Math.Abs(unit.X) * Math.Abs(halfExtents.X)
             + Math.Abs(unit.Y) * Math.Abs(halfExtents.Y)
             + Math.Abs(unit.Z) * Math.Abs(halfExtents.Z);
    }

    /// <summary>
    /// The plane normal through three hit points, oriented back toward the element that cast the
    /// ray. Returns null when the points are collinear or coincident, which means the estimate
    /// cannot be trusted (a curved or fragmented face).
    /// </summary>
    public static Vec3? PlaneNormal(Vec3 a, Vec3 b, Vec3 c, Vec3 towardCaster)
    {
        var normal = b.Minus(a).Cross(c.Minus(a));
        if (normal.Length < 1e-12)
            return null;

        var unit = normal.Normalized();
        return unit.Dot(towardCaster) < 0 ? unit.Negated() : unit;
    }

    /// <summary>
    /// Classifies a surface from its outward normal: up is a floor, down is a ceiling, horizontal
    /// is a wall. Anything in between is reported as "other" rather than forced into a bucket.
    /// </summary>
    public static string ClassifySurface(Vec3 normal, double angleToleranceDegrees)
    {
        var unit = normal.Normalized();
        if (unit.Length < Tolerance)
            return SurfaceOther;

        var tolerance = ClampAngleTolerance(angleToleranceDegrees);
        var angleFromUp = AngleFromUpDegrees(unit);

        if (angleFromUp <= tolerance)
            return SurfaceFloor;
        if (angleFromUp >= 180.0 - tolerance)
            return SurfaceCeiling;
        if (Math.Abs(90.0 - angleFromUp) <= tolerance)
            return SurfaceWall;

        return SurfaceOther;
    }

    /// <summary>Angle between a unit vector and +Z, in degrees.</summary>
    public static double AngleFromUpDegrees(Vec3 normal)
    {
        var unit = normal.Normalized();
        var clamped = Math.Min(1.0, Math.Max(-1.0, unit.Z));
        return Math.Acos(clamped) * 180.0 / Math.PI;
    }

    /// <summary>True when a classified surface satisfies what the caller asked for.</summary>
    public static bool SurfaceSatisfies(string requested, string classified)
    {
        if (requested == SurfaceNearest)
            return classified != SurfaceOther;
        return requested == classified;
    }

    /// <summary>
    /// How far to travel along the search direction to land flush against the surface.
    /// <paramref name="hitDistance"/> is measured from the element's centre to the surface,
    /// <paramref name="reach"/> is how far the element extends that way, and
    /// <paramref name="gap"/> is the clearance to leave (negative embeds it).
    /// A negative result means the element has to come back — it already overshot the surface.
    /// </summary>
    public static double TravelDistance(double hitDistance, double reach, double gap) =>
        hitDistance - reach - gap;

    /// <summary>The clearance an element currently has to the surface, before any move.</summary>
    public static double CurrentGap(double hitDistance, double reach) => hitDistance - reach;

    /// <summary>
    /// The rotation, in radians about the vertical axis, that turns <paramref name="currentFacing"/>
    /// into the outward normal of the surface so the element ends up square to it. Returns null
    /// when either direction has no meaningful horizontal component — a vertical facing direction
    /// cannot be squared to a wall by spinning about Z.
    /// </summary>
    public static double? RotationAboutZ(Vec3 currentFacing, Vec3 surfaceNormal)
    {
        var from = new Vec3(currentFacing.X, currentFacing.Y, 0).Normalized();
        var to = new Vec3(surfaceNormal.X, surfaceNormal.Y, 0).Normalized();
        if (from.Length < 0.1 || to.Length < 0.1)
            return null;

        var dot = Math.Min(1.0, Math.Max(-1.0, from.Dot(to)));
        var angle = Math.Acos(dot);

        // The sign of the Z component of the cross product tells us which way round Z to turn.
        return from.Cross(to).Z < 0 ? -angle : angle;
    }

    private static void AddHorizontal(List<Vec3> directions, int horizontalSamples, Vec3? preferred)
    {
        if (preferred.HasValue)
        {
            var flattened = new Vec3(preferred.Value.X, preferred.Value.Y, 0).Normalized();
            if (flattened.Length > 0.1)
            {
                // A wall-mounted device faces away from its wall, so look behind it first.
                directions.Add(flattened.Negated());
                directions.Add(flattened);
            }
        }

        foreach (var direction in HorizontalRing(horizontalSamples))
            directions.Add(direction);
    }
}
