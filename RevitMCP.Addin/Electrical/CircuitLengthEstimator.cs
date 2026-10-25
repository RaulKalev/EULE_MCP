namespace RevitMCP.Addin.Electrical;

/// <summary>
/// Estimates circuit lengths using geometric approximations from element locations.
/// All coordinates must be in the same unit (e.g. Revit internal feet).
/// No Revit API dependency — safe to unit-test directly.
/// </summary>
public static class CircuitLengthEstimator
{
    // ── Distance primitives ──────────────────────────────────────────────────

    /// <summary>Euclidean (straight-line) 3D distance.</summary>
    public static double StraightLine(double ax, double ay, double az, double bx, double by, double bz)
    {
        double dx = bx - ax, dy = by - ay, dz = bz - az;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>Manhattan (city-block) 3D distance.</summary>
    public static double Manhattan(double ax, double ay, double az, double bx, double by, double bz)
        => Math.Abs(bx - ax) + Math.Abs(by - ay) + Math.Abs(bz - az);

    // ── Methods ───────────────────────────────────────────────────────────────

    /// <summary>The estimation methods, in the casing the tools report.</summary>
    public static readonly IReadOnlyList<string> Methods =
        new[] { "StraightLineMax", "StraightLineSum", "ManhattanMax", "ManhattanSum", "NearestNeighborPath" };

    public const string DefaultMethod = "ManhattanMax";

    /// <summary>
    /// The method in its canonical casing; empty input is <see cref="DefaultMethod"/>. False for an unknown name,
    /// which used to be estimated as ManhattanMax without a word (M6).
    /// </summary>
    public static bool TryNormalizeMethod(string? input, out string method)
    {
        if (string.IsNullOrWhiteSpace(input)) { method = DefaultMethod; return true; }
        method = Methods.FirstOrDefault(m => string.Equals(m, input.Trim(), StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        return method.Length > 0;
    }

    public static string UnknownMethodMessage(string? input)
        => $"Unknown method '{input}'. Valid: {string.Join(", ", Methods)}.";

    // ── Main estimate ─────────────────────────────────────────────────────────

    /// <summary>
    /// Estimates raw circuit length using the specified method.
    /// Returns (rawLength in input units, id of the element with the largest <see cref="ElementDistances"/> value or null).
    /// </summary>
    /// <param name="panel">Panel/origin point (same unit as elements).</param>
    /// <param name="elements">Pairs of element-id + nullable location. Null = missing location.</param>
    /// <param name="method">One of <see cref="Methods"/> (any casing); an unknown method throws.</param>
    public static (double RawLength, long? FarthestId) Estimate(
        (double X, double Y, double Z) panel,
        IReadOnlyList<(long Id, (double X, double Y, double Z)? Loc)> elements,
        string method)
    {
        var distances = ElementDistances(panel, elements, method);
        if (distances.Count == 0)
            return (0.0, null);

        var canonical = Canonical(method);
        double raw = canonical.EndsWith("Sum", StringComparison.Ordinal)
            ? distances.Sum(d => d.Distance)
            : distances.Max(d => d.Distance);   // *Max, and NearestNeighborPath: the last element's path length
        var farthestId = distances.OrderByDescending(d => d.Distance).Select(d => (long?)d.Id).First();
        return (raw, farthestId);
    }

    /// <summary>
    /// Each located element's distance under the method, so a breakdown adds up to the estimate (M6): straight-line
    /// or Manhattan distance from the panel, and for NearestNeighborPath the path length at which the element is
    /// reached (the last one equals the total). In the elements' order, except NearestNeighborPath (visit order).
    /// </summary>
    public static List<(long Id, double Distance)> ElementDistances(
        (double X, double Y, double Z) panel,
        IReadOnlyList<(long Id, (double X, double Y, double Z)? Loc)> elements,
        string method)
    {
        var canonical = Canonical(method);
        var withLoc = elements.Where(e => e.Loc.HasValue).ToList();
        if (canonical == "NearestNeighborPath")
            return NearestNeighborPath(panel, withLoc);

        bool straight = canonical.StartsWith("StraightLine", StringComparison.Ordinal);
        return withLoc.Select(e =>
        {
            var l = e.Loc!.Value;
            return (e.Id, straight
                ? StraightLine(panel.X, panel.Y, panel.Z, l.X, l.Y, l.Z)
                : Manhattan(panel.X, panel.Y, panel.Z, l.X, l.Y, l.Z));
        }).ToList();
    }

    private static string Canonical(string method)
        => TryNormalizeMethod(method, out var canonical) ? canonical : throw new ArgumentException(UnknownMethodMessage(method), nameof(method));

    // ── Private helpers ───────────────────────────────────────────────────────

    private static List<(long Id, double Distance)> NearestNeighborPath(
        (double X, double Y, double Z) start,
        IList<(long Id, (double X, double Y, double Z)? Loc)> elements)
    {
        var remaining = Enumerable.Range(0, elements.Count).ToList();
        var (cx, cy, cz) = start;
        double total = 0;
        var visits = new List<(long Id, double Distance)>(elements.Count);

        while (remaining.Count > 0)
        {
            int nearestListIdx = 0;
            double nearestDist = double.MaxValue;
            (double X, double Y, double Z) nearestPt = default;

            for (int i = 0; i < remaining.Count; i++)
            {
                var loc = elements[remaining[i]].Loc!.Value;
                double d = StraightLine(cx, cy, cz, loc.X, loc.Y, loc.Z);
                if (d < nearestDist)
                {
                    nearestDist = d;
                    nearestListIdx = i;
                    nearestPt = loc;
                }
            }

            total += nearestDist;
            visits.Add((elements[remaining[nearestListIdx]].Id, total));
            (cx, cy, cz) = nearestPt;
            remaining.RemoveAt(nearestListIdx);
        }

        return visits;
    }
}
