using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Placement;

/// <summary>The per-placement outcome names shared by the face placement preview and the write tool.</summary>
public static class FacePlacementStatus
{
    /// <summary>A face was found and the family would be placed on it.</summary>
    public const string Ready = "Ready";

    public const string Placed = "Placed";

    /// <summary>No face within reach in the searched direction(s).</summary>
    public const string NoFace = "NoFace";

    /// <summary>Faces were in reach, but none of the kind asked for (a wall when a ceiling was wanted).</summary>
    public const string WrongSurface = "WrongSurface";

    /// <summary>Revit refused to host the family on the face that was found.</summary>
    public const string Failed = "Failed";

    /// <summary>Placed during the transaction, then undone because atomic=true and something failed.</summary>
    public const string RolledBack = "RolledBack";

    /// <summary>Placeable, but never attempted — atomic=true and the batch was rejected up front.</summary>
    public const string NotAttempted = "NotAttempted";
}

/// <summary>One requested placement, as it arrived.</summary>
public sealed class FacePlacementRequest
{
    public int Index { get; init; }

    /// <summary>The point to place near, in mm. The family lands where this point meets the face.</summary>
    public Vec3 PointMm { get; init; }

    /// <summary>wall, ceiling, floor or nearest. Empty when an explicit <see cref="Direction"/> is given.</summary>
    public string MountOn { get; init; } = string.Empty;

    /// <summary>An explicit unit search direction; null when <see cref="MountOn"/> decides the directions.</summary>
    public Vec3? Direction { get; init; }

    /// <summary>Rotation of the family about the face normal, counter-clockwise seen from in front of the face.</summary>
    public double RotationDegrees { get; init; }
}

/// <summary>
/// The Revit-free part of placing face-based families: reading the request, and the vector maths
/// that turns "a point and a face" into the location and orientation Revit needs.
/// </summary>
public static class FacePlacementMath
{
    /// <summary>Upper bound on one request: each placement costs a handful of ray casts.</summary>
    public const int MaxPlacements = 500;

    public const double DefaultMaxDistanceMm = 1500.0;
    public const double MinMaxDistanceMm = 1.0;
    public const double MaxMaxDistanceMm = 50000.0;

    public static double ClampMaxDistance(double requestedMm)
    {
        if (double.IsNaN(requestedMm) || requestedMm <= 0) return DefaultMaxDistanceMm;
        if (requestedMm < MinMaxDistanceMm) return MinMaxDistanceMm;
        if (requestedMm > MaxMaxDistanceMm) return MaxMaxDistanceMm;
        return requestedMm;
    }

    /// <summary>
    /// Reads the <c>placements</c> array. <paramref name="defaultMountOn"/> applies to entries that
    /// name neither a surface nor a direction. Returns null with <paramref name="error"/> set when
    /// the request cannot be acted on.
    /// </summary>
    public static List<FacePlacementRequest>? Parse(object? raw, string defaultMountOn, out string? error)
    {
        error = null;
        var array = ToJArray(raw);
        if (array == null)
        {
            error = "Provide 'placements': a JSON array of {x, y, z, mountOn, rotationDegrees} with coordinates in millimetres.";
            return null;
        }

        if (array.Count == 0)
        {
            error = "'placements' is empty — there is nothing to place.";
            return null;
        }

        if (array.Count > MaxPlacements)
        {
            error = $"'placements' holds {array.Count} entries; the limit is {MaxPlacements} per request. Split the batch.";
            return null;
        }

        var fallback = (defaultMountOn ?? string.Empty).Trim().ToLowerInvariant();
        if (fallback.Length > 0 && !AlignmentMath.IsKnownSurface(fallback))
        {
            error = $"mountOn '{defaultMountOn}' is not one of wall, ceiling, floor, nearest.";
            return null;
        }

        var result = new List<FacePlacementRequest>(array.Count);
        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is not JObject entry)
            {
                error = $"placements[{index}] is not a JSON object.";
                return null;
            }

            var x = Number(entry, "x", "xMm");
            var y = Number(entry, "y", "yMm");
            var z = Number(entry, "z", "zMm");
            if (!x.HasValue || !y.HasValue || !z.HasValue)
            {
                // No default for a missing coordinate: a wall device at z = 0 is a real request, and
                // guessing it would put devices on the floor line.
                error = $"placements[{index}] needs x, y and z in millimetres.";
                return null;
            }

            var dx = Number(entry, "dx", "directionX");
            var dy = Number(entry, "dy", "directionY");
            var dz = Number(entry, "dz", "directionZ");
            Vec3? direction = null;
            if (dx.HasValue || dy.HasValue || dz.HasValue)
            {
                var vector = new Vec3(dx ?? 0, dy ?? 0, dz ?? 0);
                if (vector.Length < 1e-9)
                {
                    error = $"placements[{index}] has a zero direction (dx, dy, dz). Give the direction to look for the face in.";
                    return null;
                }
                direction = vector.Normalized();
            }

            var mountOn = (Text(entry, "mountOn") ?? string.Empty).Trim().ToLowerInvariant();
            if (mountOn.Length > 0 && direction.HasValue)
            {
                error = $"placements[{index}] gives both mountOn and a direction. Use one: mountOn searches for a kind of surface, " +
                        "dx/dy/dz looks along one exact direction.";
                return null;
            }

            if (mountOn.Length == 0 && !direction.HasValue)
                mountOn = fallback;

            if (mountOn.Length == 0 && !direction.HasValue)
            {
                error = $"placements[{index}] does not say what to mount on. Give mountOn (wall, ceiling, floor, nearest) " +
                        "on the entry or for the whole request, or a direction dx/dy/dz.";
                return null;
            }

            if (mountOn.Length > 0 && !AlignmentMath.IsKnownSurface(mountOn))
            {
                error = $"placements[{index}] mountOn '{mountOn}' is not one of wall, ceiling, floor, nearest.";
                return null;
            }

            result.Add(new FacePlacementRequest
            {
                Index = index,
                PointMm = new Vec3(x.Value, y.Value, z.Value),
                MountOn = mountOn,
                Direction = direction,
                RotationDegrees = Number(entry, "rotationDegrees", "rotation") ?? 0
            });
        }

        return result;
    }

    /// <summary>
    /// The outward normal to use for a face. A measured plane (three ray hits) is the most reliable
    /// direction; the face's own geometric normal is used when it agrees with it, because it also
    /// knows which side is the outside even when the point lies exactly on the face.
    /// </summary>
    /// <param name="measured">Plane normal from three ray hits, already turned towards the caster. Null when not measurable.</param>
    /// <param name="geometric">The face's own normal at the hit, in model coordinates. Null when the face could not be read.</param>
    /// <param name="towardCaster">Unit vector from the face back towards the requested point (minus the ray direction).</param>
    public static Vec3 ChooseNormal(Vec3? measured, Vec3? geometric, Vec3 towardCaster)
    {
        if (measured.HasValue)
        {
            var m = measured.Value.Normalized();
            if (geometric.HasValue)
            {
                var g = geometric.Value.Normalized();
                // Same plane: trust the geometry for the sign.
                if (Math.Abs(m.Dot(g)) > 0.95)
                    return m.Dot(g) >= 0 ? m : m.Negated();
            }
            return m;
        }

        if (geometric.HasValue && geometric.Value.Length > 1e-9)
        {
            var g = geometric.Value.Normalized();
            // Only usable when it is roughly the face the ray met head-on.
            if (Math.Abs(g.Dot(towardCaster)) > 0.5)
                return g;
        }

        return towardCaster.Normalized();
    }

    /// <summary>Where <paramref name="point"/> lands on the plane through <paramref name="planePoint"/> with the given normal.</summary>
    public static Vec3 ProjectOntoPlane(Vec3 point, Vec3 planePoint, Vec3 normal)
    {
        var n = normal.Normalized();
        var distance = point.Minus(planePoint).Dot(n);
        return point.Minus(n.Scaled(distance));
    }

    /// <summary>Unsigned distance from <paramref name="point"/> to the plane.</summary>
    public static double DistanceToPlane(Vec3 point, Vec3 planePoint, Vec3 normal) =>
        Math.Abs(point.Minus(planePoint).Dot(normal.Normalized()));

    /// <summary>
    /// The direction of the family's local X axis on the face — the "reference direction" Revit asks
    /// for when hosting on a face. With no rotation:
    /// on a vertical face (a wall) it runs horizontally along the face so the family stands upright
    /// (its local Y is model up); on a horizontal face (a ceiling or floor) it is model X.
    /// <paramref name="rotationDegrees"/> then turns it about the normal, counter-clockwise when the
    /// face is seen from the side its normal points to.
    /// </summary>
    public static Vec3 ReferenceDirection(Vec3 normal, double rotationDegrees)
    {
        var n = normal.Normalized();
        var up = AlignmentMath.Up;

        // Upright on anything that is not (nearly) horizontal: X = up × n makes Y = n × X = up.
        var baseDirection = up.Cross(n);
        if (baseDirection.Length < 1e-6)
        {
            // Horizontal face: model X, taken into the face plane (it already is, for a level face).
            var modelX = new Vec3(1, 0, 0);
            baseDirection = modelX.Minus(n.Scaled(modelX.Dot(n)));
        }
        baseDirection = baseDirection.Normalized();

        if (Math.Abs(rotationDegrees) < 1e-9)
            return baseDirection;

        // Rodrigues' rotation about the normal; the base direction is perpendicular to it.
        var angle = rotationDegrees * Math.PI / 180.0;
        return baseDirection.Scaled(Math.Cos(angle))
            .Plus(n.Cross(baseDirection).Scaled(Math.Sin(angle)))
            .Normalized();
    }

    /// <summary>Whether a batch that produced failures has to be undone. Same rule as the move tools.</summary>
    public static bool ShouldRollBack(bool atomic, int failureCount) => atomic && failureCount > 0;

    private static JArray? ToJArray(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case JArray array:
                return array;
            case string text:
                if (string.IsNullOrWhiteSpace(text)) return null;
                try { return JToken.Parse(text) as JArray; }
                catch { return null; }
            default:
                try { return JArray.FromObject(value); }
                catch { return null; }
        }
    }

    /// <summary>Reads a number under any of the given names, case-insensitively. Null when absent or not a number.</summary>
    private static double? Number(JObject entry, params string[] names)
    {
        foreach (var name in names)
        {
            var token = entry.GetValue(name, StringComparison.OrdinalIgnoreCase);
            if (token == null || token.Type is JTokenType.Null or JTokenType.Undefined) continue;
            try
            {
                var value = token.Value<double>();
                if (!double.IsNaN(value) && !double.IsInfinity(value)) return value;
            }
            catch
            {
                // Not a number: treated as absent, and a missing coordinate is reported by the caller.
            }
        }
        return null;
    }

    private static string? Text(JObject entry, string name)
    {
        var token = entry.GetValue(name, StringComparison.OrdinalIgnoreCase);
        if (token == null || token.Type is JTokenType.Null or JTokenType.Undefined) return null;
        try { return token.Value<string>(); }
        catch { return null; }
    }
}
