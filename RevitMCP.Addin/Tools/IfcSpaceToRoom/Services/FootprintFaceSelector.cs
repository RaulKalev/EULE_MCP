namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>A planar face of a space solid, reduced to what the footprint choice needs.</summary>
public sealed class FaceCandidate
{
    public FaceCandidate(int index, double areaFt2, double normalZ, double elevationFt)
    {
        Index = index;
        AreaFt2 = areaFt2;
        NormalZ = normalZ;
        ElevationFt = elevationFt;
    }

    public int Index { get; }
    public double AreaFt2 { get; }
    /// <summary>Z component of the outward face normal: −1 for a floor face, +1 for a ceiling face.</summary>
    public double NormalZ { get; }
    public double ElevationFt { get; }
}

/// <summary>Which face was chosen as the footprint and what the floor looks like.</summary>
public sealed class FootprintFaceChoice
{
    public int Index { get; set; }
    public double AreaFt2 { get; set; }
    public double ElevationFt { get; set; }
    /// <summary>True when the choice came from downward-facing (floor) faces.</summary>
    public bool FromDownwardFaces { get; set; }
    /// <summary>Sum of the floor faces' areas — the plan area of the whole space.</summary>
    public double TotalFloorAreaFt2 { get; set; }
    /// <summary>Distinct elevations among the significant floor faces.</summary>
    public int FloorLevels { get; set; }
    /// <summary>The chosen face covers clearly less than the whole floor (stepped or split floor).</summary>
    public bool IsStepped { get; set; }
}

/// <summary>
/// Chooses the footprint face of an IFC space solid (#68). The old rule took the lowest horizontal
/// face, so a small face below the real floor (a threshold strip, slab recess or step) produced a
/// ~250 mm sliver. Now: only downward-facing horizontal faces (the floor of a closed solid), the
/// largest one wins, lowest elevation breaks ties; upward faces are a fallback for solids with
/// inverted normals. No Revit API dependency — unit tested in RevitMCP.Tests.
/// </summary>
public static class FootprintFaceSelector
{
    /// <summary>Faces within this vertical distance count as the same floor level (≈ 3 mm).</summary>
    public const double SameLevelToleranceFt = 0.01;
    /// <summary>Floor faces smaller than this share of the largest one are ignored for the level count.</summary>
    public const double SignificantShare = 0.05;
    /// <summary>The chosen face must cover at least this share of the whole floor, else it is "stepped".</summary>
    public const double SteppedThreshold = 0.9;

    public static FootprintFaceChoice? Choose(IReadOnlyList<FaceCandidate> faces, double minNormalZ, double minAreaFt2)
    {
        var horizontal = faces
            .Where(f => Math.Abs(f.NormalZ) >= minNormalZ && f.AreaFt2 >= minAreaFt2)
            .ToList();
        if (horizontal.Count == 0) return null;

        var downward = horizontal.Where(f => f.NormalZ < 0).ToList();
        var pool = downward.Count > 0 ? downward : horizontal;

        var best = pool
            .OrderByDescending(f => f.AreaFt2)
            .ThenBy(f => f.ElevationFt)
            .First();

        var total = downward.Count > 0 ? downward.Sum(f => f.AreaFt2) : best.AreaFt2;
        var significant = pool.Where(f => f.AreaFt2 >= best.AreaFt2 * SignificantShare).ToList();
        var levels = CountLevels(significant.Select(f => f.ElevationFt));

        return new FootprintFaceChoice
        {
            Index = best.Index,
            AreaFt2 = best.AreaFt2,
            ElevationFt = best.ElevationFt,
            FromDownwardFaces = downward.Count > 0,
            TotalFloorAreaFt2 = total,
            FloorLevels = levels,
            IsStepped = downward.Count > 1 && best.AreaFt2 < total * SteppedThreshold
        };
    }

    private static int CountLevels(IEnumerable<double> elevations)
    {
        var sorted = elevations.OrderBy(z => z).ToList();
        if (sorted.Count == 0) return 0;
        var levels = 1;
        for (int i = 1; i < sorted.Count; i++)
            if (sorted[i] - sorted[i - 1] > SameLevelToleranceFt) levels++;
        return levels;
    }

    /// <summary>
    /// Relative difference between the extracted and the declared area, in percent; null when the
    /// declared area is unknown or not positive.
    /// </summary>
    public static double? AreaMismatchPercent(double footprintM2, double? declaredM2)
    {
        if (declaredM2 is not > 0) return null;
        return Math.Abs(footprintM2 - declaredM2.Value) / declaredM2.Value * 100.0;
    }
}
