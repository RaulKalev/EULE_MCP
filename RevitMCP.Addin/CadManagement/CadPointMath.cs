namespace RevitMCP.Addin.CadManagement;

/// <summary>
/// One candidate placement location read out of a CAD import, in millimetres, in host coordinates.
/// </summary>
public sealed class CadPoint
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }

    /// <summary>The CAD layer the geometry sits on.</summary>
    public string Layer { get; set; } = string.Empty;

    /// <summary>What produced the point: <c>block</c>, <c>point</c>, or <c>circle</c>.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Plan rotation carried over from a block reference, in degrees. Zero for other sources.</summary>
    public double RotationDegrees { get; set; }

    /// <summary>How many raw points were merged into this one.</summary>
    public int MergedCount { get; set; } = 1;

    /// <summary>A block's size, when the caller asked for extents. Null for other sources.</summary>
    public CadBlockExtents? Extents { get; set; }
}

/// <summary>
/// A block reference's size in host mm (#143): the axis-aligned box around its line work, and its width and depth along
/// the block's own axes, so a rotated unit keeps its real size.
/// </summary>
public sealed class CadBlockExtents
{
    public double MinX { get; set; }
    public double MinY { get; set; }
    public double MaxX { get; set; }
    public double MaxY { get; set; }
    public double Width { get; set; }
    public double Depth { get; set; }

    public double CenterX => (MinX + MaxX) / 2;
    public double CenterY => (MinY + MaxY) / 2;

    /// <summary>Whether two blocks cover the same box. Blocks of an exported DWG can all be inserted at one point.</summary>
    public bool SamePlace(CadBlockExtents other, double toleranceMm) =>
        Math.Abs(MinX - other.MinX) <= toleranceMm && Math.Abs(MinY - other.MinY) <= toleranceMm &&
        Math.Abs(MaxX - other.MaxX) <= toleranceMm && Math.Abs(MaxY - other.MaxY) <= toleranceMm;

    /// <summary>
    /// From the line work's points in host mm. <paramref name="axisX"/> and <paramref name="axisY"/> are the block's X
    /// axis in plan; they need not be unit length. Null without points.
    /// </summary>
    public static CadBlockExtents? FromPoints(IReadOnlyCollection<(double X, double Y)> points, double originX, double originY, double axisX, double axisY)
    {
        if (points.Count == 0) return null;
        var length = Math.Sqrt(axisX * axisX + axisY * axisY);
        var ux = length < 1e-12 ? 1 : axisX / length;
        var uy = length < 1e-12 ? 0 : axisY / length;

        var along = points.Select(p => (p.X - originX) * ux + (p.Y - originY) * uy).ToList();
        var across = points.Select(p => -(p.X - originX) * uy + (p.Y - originY) * ux).ToList();
        return new CadBlockExtents
        {
            MinX = points.Min(p => p.X),
            MinY = points.Min(p => p.Y),
            MaxX = points.Max(p => p.X),
            MaxY = points.Max(p => p.Y),
            Width = along.Max() - along.Min(),
            Depth = across.Max() - across.Min()
        };
    }
}

/// <summary>
/// The Revit-free half of reading placement locations out of a DWG: merging duplicate marks,
/// reading a block's rotation, and working out what elevation to place at.
/// </summary>
public static class CadPointMath
{
    public const string SourceBlock = "block";
    public const string SourcePoint = "point";
    public const string SourceCircle = "circle";

    public const string ElevationFromDwg = "dwg";
    public const string ElevationFromLevel = "level";
    public const string ElevationExplicit = "explicit";

    /// <summary>Sources that rank ahead of others when duplicates are merged: a block carries rotation.</summary>
    private static int SourceRank(string source) => source switch
    {
        SourceBlock => 0,
        SourcePoint => 1,
        SourceCircle => 2,
        _ => 3
    };

    public static bool IsKnownSource(string source) =>
        source is SourceBlock or SourcePoint or SourceCircle;

    public static bool IsKnownElevationMode(string mode) =>
        mode is ElevationFromDwg or ElevationFromLevel or ElevationExplicit;

    /// <summary>
    /// Collapses marks that sit on top of each other into one placement point. A symbol drawn as a
    /// block containing a circle otherwise yields two points at the same spot, and placing twice
    /// there is never what anyone wants. Merging happens within a layer only; the surviving point
    /// keeps the richest source, so a block's rotation is not lost to a circle.
    /// </summary>
    public static List<CadPoint> Merge(IReadOnlyList<CadPoint> points, double toleranceMm)
    {
        if (points.Count == 0)
            return new List<CadPoint>();

        if (toleranceMm <= 0)
            return points.ToList();

        // Grid hashing keeps this linear: only the 27 cells around a point can hold a match.
        var cells = new Dictionary<(string Layer, long X, long Y, long Z), List<CadPoint>>();
        var merged = new List<CadPoint>();

        foreach (var point in points)
        {
            var key = CellKey(point, toleranceMm);
            var match = FindNeighbour(cells, point, key, toleranceMm);

            if (match == null)
            {
                var kept = Copy(point);
                merged.Add(kept);
                if (!cells.TryGetValue(key, out var bucket))
                {
                    bucket = new List<CadPoint>();
                    cells[key] = bucket;
                }
                bucket.Add(kept);
                continue;
            }

            match.MergedCount++;
            if (SourceRank(point.Source) < SourceRank(match.Source))
            {
                // The better source wins the position and the rotation too.
                match.Source = point.Source;
                match.RotationDegrees = point.RotationDegrees;
                match.Extents = point.Extents;
                match.X = point.X;
                match.Y = point.Y;
                match.Z = point.Z;
            }
        }

        return merged;
    }

    /// <summary>Plan rotation of a block reference, from its X axis, normalised to [0, 360).</summary>
    public static double RotationDegreesFromBasis(double basisX, double basisY)
    {
        if (Math.Abs(basisX) < 1e-12 && Math.Abs(basisY) < 1e-12)
            return 0.0;

        var degrees = Math.Atan2(basisY, basisX) * 180.0 / Math.PI;
        return Normalize(degrees);
    }

    /// <summary>Wraps an angle into [0, 360).</summary>
    public static double Normalize(double degrees)
    {
        var wrapped = degrees % 360.0;
        if (wrapped < 0) wrapped += 360.0;
        // -0 and 360 both mean 0; keep the output canonical.
        return Math.Abs(wrapped - 360.0) < 1e-9 ? 0.0 : wrapped;
    }

    /// <summary>
    /// Works out the elevation to place at. There is no default on purpose: a DWG that carries no
    /// height would otherwise dump everything on the level's floor without anybody noticing.
    /// </summary>
    public static bool TryResolveElevation(
        string mode,
        double dwgZmm,
        double? levelElevationMm,
        double offsetMm,
        double explicitElevationMm,
        out double elevationMm,
        out string? error)
    {
        elevationMm = 0;
        error = null;

        switch (mode)
        {
            case ElevationFromDwg:
                elevationMm = dwgZmm;
                return true;

            case ElevationFromLevel:
                if (levelElevationMm == null)
                {
                    error = "elevationMode=level needs a levelName that exists in the model.";
                    return false;
                }
                elevationMm = levelElevationMm.Value + offsetMm;
                return true;

            case ElevationExplicit:
                elevationMm = explicitElevationMm;
                return true;

            default:
                error = $"Unknown elevationMode '{mode}'. Valid: dwg, level, explicit.";
                return false;
        }
    }

    /// <summary>
    /// True when every point sits at effectively the same height — the signature of a flat 2D
    /// drawing, and the cue that the caller has to be asked for a mounting height.
    /// </summary>
    public static bool IsFlat(IReadOnlyList<CadPoint> points, double toleranceMm = 1.0)
    {
        if (points.Count == 0)
            return true;

        var min = points.Min(p => p.Z);
        var max = points.Max(p => p.Z);
        return max - min <= toleranceMm;
    }

    /// <summary>
    /// Flags candidates that already have an instance standing on them, so re-running after a
    /// tweak tops up the model instead of stacking a second copy on every location. Compared in
    /// plan only: the elevation is supplied separately, so a socket at 1100 still counts as the
    /// same location as the mark it came from at 0.
    /// </summary>
    public static List<bool> MarkExisting(
        IReadOnlyList<CadPoint> candidates,
        IReadOnlyList<CadPoint> existing,
        double toleranceMm)
    {
        var flags = new List<bool>(candidates.Count);
        for (var index = 0; index < candidates.Count; index++)
            flags.Add(false);

        if (existing.Count == 0 || toleranceMm <= 0)
            return flags;

        var cells = new Dictionary<(long X, long Y), List<CadPoint>>();
        foreach (var point in existing)
        {
            var key = PlanCellKey(point, toleranceMm);
            if (!cells.TryGetValue(key, out var bucket))
            {
                bucket = new List<CadPoint>();
                cells[key] = bucket;
            }
            bucket.Add(point);
        }

        var toleranceSquared = toleranceMm * toleranceMm;
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var key = PlanCellKey(candidate, toleranceMm);

            for (var dx = -1; dx <= 1 && !flags[index]; dx++)
            for (var dy = -1; dy <= 1 && !flags[index]; dy++)
            {
                if (!cells.TryGetValue((key.X + dx, key.Y + dy), out var bucket))
                    continue;

                foreach (var other in bucket)
                {
                    var ex = candidate.X - other.X;
                    var ey = candidate.Y - other.Y;
                    if (ex * ex + ey * ey <= toleranceSquared)
                    {
                        flags[index] = true;
                        break;
                    }
                }
            }
        }

        return flags;
    }

    private static (long X, long Y) PlanCellKey(CadPoint point, double cellSize) =>
        ((long)Math.Floor(point.X / cellSize), (long)Math.Floor(point.Y / cellSize));

    private static CadPoint? FindNeighbour(
        Dictionary<(string, long, long, long), List<CadPoint>> cells,
        CadPoint point,
        (string Layer, long X, long Y, long Z) key,
        double toleranceMm)
    {
        var toleranceSquared = toleranceMm * toleranceMm;

        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dz = -1; dz <= 1; dz++)
        {
            var neighbourKey = (key.Layer, key.X + dx, key.Y + dy, key.Z + dz);
            if (!cells.TryGetValue(neighbourKey, out var bucket))
                continue;

            foreach (var candidate in bucket)
            {
                if (DistanceSquared(candidate, point) <= toleranceSquared && SameBlockPlace(candidate, point, toleranceMm))
                    return candidate;
            }
        }

        return null;
    }

    /// <summary>Two measured blocks at one insertion point are one mark only if they also cover the same box.</summary>
    private static bool SameBlockPlace(CadPoint a, CadPoint b, double toleranceMm) =>
        a.Extents == null || b.Extents == null || a.Extents.SamePlace(b.Extents, toleranceMm);

    private static double DistanceSquared(CadPoint a, CadPoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    private static (string Layer, long X, long Y, long Z) CellKey(CadPoint point, double cellSize) =>
        (point.Layer,
         (long)Math.Floor(point.X / cellSize),
         (long)Math.Floor(point.Y / cellSize),
         (long)Math.Floor(point.Z / cellSize));

    private static CadPoint Copy(CadPoint point) => new()
    {
        X = point.X,
        Y = point.Y,
        Z = point.Z,
        Layer = point.Layer,
        Source = point.Source,
        RotationDegrees = point.RotationDegrees,
        Extents = point.Extents,
        MergedCount = 1
    };
}
