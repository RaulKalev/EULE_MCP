namespace RevitMCP.Addin.Checkpoints;

public sealed class ParameterDelta
{
    public string Name { get; set; } = string.Empty;
    public string? Before { get; set; }
    public string? Now { get; set; }
}

public sealed class LocationDelta
{
    /// <summary>Point xyz, or line endpoints [x0..z1], in millimetres.</summary>
    public double[] FromMm { get; set; } = Array.Empty<double>();
    public double[] ToMm { get; set; } = Array.Empty<double>();

    /// <summary>Largest distance any snapshotted point moved, in millimetres.</summary>
    public double DistanceMm { get; set; }
}

/// <summary>How a snapshotted element differs from its state at checkpoint time.</summary>
public sealed class ElementDelta
{
    public long Id { get; set; }

    /// <summary>The element no longer exists.</summary>
    public bool Missing { get; set; }

    public List<ParameterDelta> Parameters { get; set; } = new();
    public LocationDelta? Moved { get; set; }
    public double? RotationBeforeRad { get; set; }
    public double? RotationNowRad { get; set; }

    /// <summary>Facing before and now (CCW from +X), when the element is a family instance that rotated.</summary>
    public double? FacingBeforeRad { get; set; }
    public double? FacingNowRad { get; set; }

    public bool Rotated => RotationBeforeRad != null && RotationNowRad != null;
    public bool HasChanges => Missing || Parameters.Count > 0 || Moved != null || Rotated;
}

/// <summary>
/// Compares an <see cref="ElementSnapshot"/> taken at checkpoint time with one read now using the same
/// formatting (#98). Pure — no Revit API.
/// </summary>
public static class SnapshotDiff
{
    public const double DefaultToleranceMm = 1.0;
    public const double RotationToleranceRad = 1e-6;

    /// <param name="now">The element read now, or null when it no longer exists.</param>
    public static ElementDelta Compare(long id, ElementSnapshot before, ElementSnapshot? now, double toleranceMm = DefaultToleranceMm)
    {
        var delta = new ElementDelta { Id = id };
        if (now == null)
        {
            delta.Missing = true;
            return delta;
        }

        // Union of names: a parameter that gained or lost its value shows up on one side only.
        foreach (var name in before.Parameters.Keys.Union(now.Parameters.Keys, StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal))
        {
            before.Parameters.TryGetValue(name, out var was);
            now.Parameters.TryGetValue(name, out var @is);
            if (!string.Equals(Normalize(was), Normalize(@is), StringComparison.Ordinal))
                delta.Parameters.Add(new ParameterDelta { Name = name, Before = was, Now = @is });
        }

        delta.Moved = CompareLocation(before.LocationMm, now.LocationMm, toleranceMm)
                      ?? CompareLocation(before.LocationLineMm, now.LocationLineMm, toleranceMm);

        if (before.RotationRad is double r0 && now.RotationRad is double r1 && AngleDifference(r0, r1) > RotationToleranceRad)
        {
            delta.RotationBeforeRad = r0;
            delta.RotationNowRad = r1;
            if (before.FacingRad is double f0 && now.FacingRad is double f1)
            {
                delta.FacingBeforeRad = f0;
                delta.FacingNowRad = f1;
            }
        }
        return delta;
    }

    /// <summary>Compares every snapshotted element; <paramref name="readNow"/> returns null for a missing element.</summary>
    public static Dictionary<long, ElementDelta> CompareAll(
        IReadOnlyDictionary<long, ElementSnapshot> snapshot,
        Func<long, ElementSnapshot?> readNow,
        double toleranceMm = DefaultToleranceMm)
    {
        var result = new Dictionary<long, ElementDelta>();
        foreach (var pair in snapshot)
        {
            var delta = Compare(pair.Key, pair.Value, readNow(pair.Key), toleranceMm);
            if (delta.HasChanges) result[pair.Key] = delta;
        }
        return result;
    }

    /// <summary>Largest point distance between two equally long xyz arrays; null when not comparable or within tolerance.</summary>
    public static LocationDelta? CompareLocation(double[]? from, double[]? to, double toleranceMm)
    {
        if (from == null || to == null || from.Length != to.Length || from.Length == 0 || from.Length % 3 != 0) return null;
        var max = 0.0;
        for (var i = 0; i < from.Length; i += 3)
        {
            var dx = to[i] - from[i];
            var dy = to[i + 1] - from[i + 1];
            var dz = to[i + 2] - from[i + 2];
            max = Math.Max(max, Math.Sqrt(dx * dx + dy * dy + dz * dz));
        }
        if (max <= toleranceMm) return null;
        return new LocationDelta
        {
            FromMm = from.Select(Round).ToArray(),
            ToMm = to.Select(Round).ToArray(),
            DistanceMm = Round(max)
        };
    }

    /// <summary>Smallest absolute difference between two angles, in radians.</summary>
    public static double AngleDifference(double a, double b)
    {
        var d = Math.IEEERemainder(b - a, 2 * Math.PI);
        return Math.Abs(d);
    }

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    private static double Round(double value) => Math.Round(value, 1);
}
