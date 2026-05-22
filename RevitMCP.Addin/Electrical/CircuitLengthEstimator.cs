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

    // ── Main estimate ─────────────────────────────────────────────────────────

    /// <summary>
    /// Estimates raw circuit length using the specified method.
    /// Returns (rawLength in input units, farthest element id or null).
    /// </summary>
    /// <param name="panel">Panel/origin point (same unit as elements).</param>
    /// <param name="elements">Pairs of element-id + nullable location. Null = missing location.</param>
    /// <param name="method">One of: StraightLineMax, StraightLineSum, ManhattanMax, ManhattanSum, NearestNeighborPath.</param>
    public static (double RawLength, long? FarthestId) Estimate(
        (double X, double Y, double Z) panel,
        IReadOnlyList<(long Id, (double X, double Y, double Z)? Loc)> elements,
        string method)
    {
        var withLoc = elements.Where(e => e.Loc.HasValue).ToList();
        if (withLoc.Count == 0)
            return (0.0, null);

        double raw = method switch
        {
            "StraightLineMax"     => withLoc.Max(e => StraightLine(panel.X, panel.Y, panel.Z, e.Loc!.Value.X, e.Loc.Value.Y, e.Loc.Value.Z)),
            "StraightLineSum"     => withLoc.Sum(e => StraightLine(panel.X, panel.Y, panel.Z, e.Loc!.Value.X, e.Loc.Value.Y, e.Loc.Value.Z)),
            "ManhattanMax"        => withLoc.Max(e => Manhattan(panel.X, panel.Y, panel.Z, e.Loc!.Value.X, e.Loc.Value.Y, e.Loc.Value.Z)),
            "ManhattanSum"        => withLoc.Sum(e => Manhattan(panel.X, panel.Y, panel.Z, e.Loc!.Value.X, e.Loc.Value.Y, e.Loc.Value.Z)),
            "NearestNeighborPath" => NearestNeighborPath(panel, withLoc),
            _                     => withLoc.Max(e => Manhattan(panel.X, panel.Y, panel.Z, e.Loc!.Value.X, e.Loc.Value.Y, e.Loc.Value.Z))
        };

        // Farthest element by Manhattan distance (consistent across all methods for DTO)
        var farthestId = withLoc
            .OrderByDescending(e => Manhattan(panel.X, panel.Y, panel.Z, e.Loc!.Value.X, e.Loc.Value.Y, e.Loc.Value.Z))
            .Select(e => (long?)e.Id)
            .FirstOrDefault();

        return (raw, farthestId);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static double NearestNeighborPath(
        (double X, double Y, double Z) start,
        IList<(long Id, (double X, double Y, double Z)? Loc)> elements)
    {
        var remaining = Enumerable.Range(0, elements.Count).ToList();
        var (cx, cy, cz) = start;
        double total = 0;

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
            (cx, cy, cz) = nearestPt;
            remaining.RemoveAt(nearestListIdx);
        }

        return total;
    }
}
