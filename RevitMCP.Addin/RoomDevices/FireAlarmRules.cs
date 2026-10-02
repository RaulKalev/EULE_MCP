using RevitMCP.Addin.CadManagement;

namespace RevitMCP.Addin.RoomDevices;

/// <summary>Detector and alarm device kinds the fire alarm rules know.</summary>
public static class FireDeviceTypes
{
    public const string PointSmoke = "pointSmoke";     // EN 54-7
    public const string LinearSmoke = "linearSmoke";   // EN 54-12
    public const string Aspirating = "aspirating";     // ASD, classes A, B, C (sampling holes)
    public const string PointHeat = "pointHeat";       // EN 54-5, classes A1, A2, B, C, D, E, F, G
    public const string LinearHeat = "linearHeat";     // EN 54-22, classes A1, A2
    public const string Flame = "flame";               // EN 54-10, classes 1, 2, 3
    public const string CarbonMonoxide = "co";         // placed by the smoke detector rules
    public const string Sounder = "sounder";           // acoustic alarm device

    public static readonly string[] All = [PointSmoke, LinearSmoke, Aspirating, PointHeat, LinearHeat, Flame, CarbonMonoxide, Sounder];

    public static string? Normalize(string? value)
    {
        var v = (value ?? string.Empty).Trim();
        return All.FirstOrDefault(t => string.Equals(t, v, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsDetector(string? type) => type != null && type != Sounder;
}

/// <summary>Result of the Table 1 room-height check.</summary>
public sealed class HeightSuitability
{
    /// <summary>suitable | conditional | unsuitable</summary>
    public string Status { get; set; } = "suitable";
    /// <summary>The height band of Table 1 the room falls in, e.g. "≤ 7.5 m"; null above 45 m.</summary>
    public string? Band { get; set; }
    public string Note { get; set; } = string.Empty;
    public bool IsUnsuitable => Status == "unsuitable";
}

/// <summary>Point-detector spacing of 6.5.2.2 / 6.5.2.3, already scaled for a sloped ceiling.</summary>
public sealed class DetectorSpacing
{
    public double RadiusMm { get; set; }
    public double MaxSpacingMm { get; set; }
    public double MaxWallDistanceMm { get; set; }
    public double CorridorSpacingMm { get; set; }
    public double CorridorEndDistanceMm { get; set; }
    public double SlopeFactor { get; set; } = 1;
}

/// <summary>Corridor layout along the centreline of a narrow room.</summary>
public sealed class CorridorLayout
{
    public bool IsCorridor { get; set; }
    public double WidthMm { get; set; }
    public double LengthMm { get; set; }
    public List<P2> Points { get; } = [];
}

public sealed class SoundResult
{
    public int Samples { get; set; }
    public int BelowRequired { get; set; }
    public int AboveMaximum { get; set; }
    public double RequiredDb { get; set; }
    public double? MinLevelDb { get; set; }
    public double? MaxLevelDb { get; set; }
    public double BelowAreaM2 { get; set; }
    public List<UncoveredRegion> QuietRegions { get; } = [];
}

/// <summary>
/// Fire alarm placement rules: Table 1 (detector type vs room height), point-detector spacing
/// (6.5.2.2 heat, 6.5.2.3 smoke/CO/ASD holes, corridors ≤ 2 m, sloped ceilings) and the acoustic
/// alarm levels (65 dB(A) / ambient + 10, 75 dB(A) for sleeping, max 118 dB(A)).
/// No Revit API dependency — unit tested in RevitMCP.Tests.
/// </summary>
public static class FireAlarmRules
{
    public const double CorridorMaxWidthMm = 2000;
    public const double MinimumAlarmDb = 65;
    public const double SleepingAlarmDb = 75;
    public const double MaximumAlarmDb = 118;
    public const double AmbientMarginDb = 10;

    // ── Table 1 ───────────────────────────────────────────────────────────────

    private static readonly double[] BandLimitsM = [6, 7.5, 9, 12, 16, 25, 45];

    private const string S = "suitable", C = "conditional", U = "unsuitable";
    private const string DependsOnUse = "Suitability depends on the use and environment (e.g. fast fire growth, smoke spread).";

    /// <summary>Table 1 by device type, one cell per band: ≤6, ≤7.5, ≤9, ≤12, ≤16, ≤25, ≤45 m.</summary>
    private static readonly Dictionary<string, (string Status, string Note)[]> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        [FireDeviceTypes.PointSmoke] = [(S, ""), (S, ""), (S, ""), (S, ""), (C, DependsOnUse), (U, ""), (U, "")],
        [FireDeviceTypes.CarbonMonoxide] = [(S, ""), (S, ""), (S, ""), (S, ""), (C, DependsOnUse), (U, ""), (U, "")],
        [FireDeviceTypes.LinearSmoke] = [(S, ""), (S, ""), (S, ""), (S, ""), (S, ""),
            (C, DependsOnUse + " Needs an acceptable detection-effectiveness certificate (d)."),
            (C, DependsOnUse + " Needs an acceptable detection-effectiveness certificate (d); with storage-related problems a physical fire test is recommended (f).")],
        [FireDeviceTypes.Aspirating] = [(S, ""), (S, ""), (S, ""), (S, ""),
            (C, "At least 5 class C sampling holes (e: max 35 % obscuration sensitivity, full protection at the chosen model's maximum spacing)."),
            (C, "At least 15 class C sampling holes (e)."),
            (C, "At least 15 class B sampling holes (e).")],
        [FireDeviceTypes.PointHeat] = [(S, "Classes B–G only for object protection (b)."), (C, "Only class A1 (a: also R or S class)."),
            (U, ""), (U, ""), (U, ""), (U, ""), (U, "")],
        [FireDeviceTypes.LinearHeat] = [(S, ""), (S, ""), (C, "Only class A1."), (U, ""), (U, ""), (U, ""), (U, "")],
        [FireDeviceTypes.Flame] = [(S, "Depends on the detector class and mounting position (c)."), (S, "Depends on class and position (c)."),
            (S, "Depends on class and position (c)."), (S, "Depends on class and position (c)."), (S, "Depends on class and position (c)."),
            (S, "Depends on class and position (c)."), (S, "Depends on class and position (c).")]
    };

    public static string BandLabel(int band) => band < BandLimitsM.Length
        ? "≤ " + BandLimitsM[band].ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " m"
        : "> 45 m";

    /// <summary>
    /// Table 1: is this detector type suitable for a room of <paramref name="roomHeightMm"/>?
    /// <paramref name="detectorClass"/> refines the class-limited cells (point heat ≤ 7.5 m and linear
    /// heat ≤ 9 m need class A1; point heat classes B–G are for object protection only).
    /// </summary>
    public static HeightSuitability CheckHeight(string type, double roomHeightMm, string? detectorClass = null)
    {
        if (type == FireDeviceTypes.Sounder)
            return new HeightSuitability { Status = S, Note = "Not a detector." };
        if (!Table.TryGetValue(type, out var cells))
            return new HeightSuitability { Status = C, Note = $"Unknown detector type '{type}'." };

        var heightM = roomHeightMm / 1000.0;
        var band = Array.FindIndex(BandLimitsM, limit => heightM <= limit + 1e-9);
        if (band < 0)
            return new HeightSuitability { Status = U, Band = null, Note = "Room higher than 45 m — outside Table 1." };

        var (status, note) = cells[band];
        var result = new HeightSuitability { Status = status, Band = BandLabel(band), Note = note };
        var cls = (detectorClass ?? string.Empty).Trim().ToUpperInvariant();

        // Class-limited cells: a known class turns "only A1" into a firm yes or no.
        if (cls.Length > 0)
        {
            if (type == FireDeviceTypes.PointHeat && band == 1)
                result = IsA1(cls) ? Ok(result, "Class A1 (or A1R/A1S) meets 'only class A1'.") : No(result, $"Class {cls} — only class A1 is allowed up to 7.5 m.");
            else if (type == FireDeviceTypes.LinearHeat && band == 2)
                result = cls == "A1" ? Ok(result, "Class A1 meets 'only class A1'.") : No(result, $"Class {cls} — only class A1 is allowed up to 9 m.");
            else if (type == FireDeviceTypes.PointHeat && band == 0 && !cls.StartsWith("A"))
                result = new HeightSuitability { Status = C, Band = result.Band, Note = $"Class {cls} is suitable only for object protection (b)." };
            else if (type == FireDeviceTypes.Aspirating && band == 6 && cls is not ("A" or "B"))
                result = No(result, $"Class {cls} — at least 15 class B holes are needed up to 45 m.");
            else if (type == FireDeviceTypes.Aspirating && band is 4 or 5 && cls == "C")
                result.Note = band == 4 ? "Class C: at least 5 sampling holes." : "Class C: at least 15 sampling holes.";
        }

        return result;
    }

    private static bool IsA1(string cls) => cls is "A1" or "A1R" or "A1S";

    private static HeightSuitability Ok(HeightSuitability r, string note) => new() { Status = S, Band = r.Band, Note = note };
    private static HeightSuitability No(HeightSuitability r, string note) => new() { Status = U, Band = r.Band, Note = note };

    // ── Spacing (6.5.2.2 / 6.5.2.3) ───────────────────────────────────────────

    /// <summary>Horizontal-distance factor for a sloped ceiling: +1 % per degree, at most +25 %.</summary>
    public static double SlopeFactor(double slopeDeg) => 1 + Math.Min(0.25, Math.Max(0, slopeDeg) * 0.01);

    /// <summary>
    /// Point-detector spacing for smoke/CO/ASD holes (radius 6.2 m, grid 8.8 m, wall 4.4 m, corridor
    /// 12.4 m / end 6.2 m) and heat (4.5 m, 6.4 m, 3.2 m, corridor 9.0 m / end 4.5 m). Null for types
    /// without point spacing (linear, flame, sounder).
    /// </summary>
    public static DetectorSpacing? Spacing(string type, double ceilingSlopeDeg = 0)
    {
        var f = SlopeFactor(ceilingSlopeDeg);
        DetectorSpacing? s = type switch
        {
            FireDeviceTypes.PointSmoke or FireDeviceTypes.CarbonMonoxide or FireDeviceTypes.Aspirating => new DetectorSpacing
            {
                RadiusMm = 6200, MaxSpacingMm = 8800, MaxWallDistanceMm = 4400, CorridorSpacingMm = 12400, CorridorEndDistanceMm = 6200
            },
            FireDeviceTypes.PointHeat => new DetectorSpacing
            {
                RadiusMm = 4500, MaxSpacingMm = 6400, MaxWallDistanceMm = 3200, CorridorSpacingMm = 9000, CorridorEndDistanceMm = 4500
            },
            _ => null
        };
        if (s == null) return null;

        s.SlopeFactor = f;
        s.RadiusMm *= f;
        s.MaxSpacingMm *= f;
        s.MaxWallDistanceMm *= f;
        s.CorridorSpacingMm *= f;
        s.CorridorEndDistanceMm *= f;
        return s;
    }

    // ── Corridors ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Treats the room as a corridor when its minimum-area bounding box is at most 2 m wide, and lays
    /// detectors along the box's centreline: n = ⌈length / spacing⌉ evenly spaced points, so neither
    /// the spacing nor the end-wall distance (half a step) exceeds the corridor limits.
    /// </summary>
    public static CorridorLayout Corridor(RoomPolygon polygon, double spacingMm, double endDistanceMm, double maxWidthMm = CorridorMaxWidthMm)
    {
        var layout = new CorridorLayout();
        var box = CadShapeMath.MinAreaBox(polygon.Outer.Select(p => (p.X, p.Y)).ToList());
        if (box == null) return layout;

        var (cx, cy, longSide, shortSide, angleDeg, _) = box.Value;
        layout.WidthMm = shortSide;
        layout.LengthMm = longSide;
        layout.IsCorridor = shortSide <= maxWidthMm + 1e-6 && longSide > shortSide;
        if (!layout.IsCorridor) return layout;

        var step = Math.Min(spacingMm, 2 * endDistanceMm);
        var n = Math.Max(1, (int)Math.Ceiling(longSide / step - 1e-9));
        var pitch = longSide / n;
        var dir = new P2(Math.Cos(angleDeg * Math.PI / 180), Math.Sin(angleDeg * Math.PI / 180));
        var start = new P2(cx, cy).Minus(dir.Scaled(longSide / 2));
        for (int i = 0; i < n; i++)
            layout.Points.Add(start.Plus(dir.Scaled(pitch * (i + 0.5))));
        return layout;
    }

    // ── Sounders ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Minimum alarm level for a room: 75 dB(A) where people sleep, otherwise 65 dB(A) or 10 dB(A)
    /// above the ambient noise lasting over 30 s, whichever is higher.
    /// </summary>
    public static double RequiredAlarmDb(double? ambientNoiseDb, bool sleeping)
    {
        var level = Math.Max(MinimumAlarmDb, (ambientNoiseDb ?? 0) + AmbientMarginDb);
        return sleeping ? Math.Max(SleepingAlarmDb, level) : level;
    }

    /// <summary>Free-field level at <paramref name="distanceM"/> from a sounder rated <paramref name="levelAt1mDb"/> at 1 m (−6 dB per doubling).</summary>
    public static double LevelAt(double levelAt1mDb, double distanceM) =>
        levelAt1mDb - 20 * Math.Log10(Math.Max(1.0, distanceM));

    /// <summary>Energetic sum of sound levels.</summary>
    public static double SumDb(IEnumerable<double> levels)
    {
        var energy = levels.Sum(l => Math.Pow(10, l / 10));
        return energy <= 0 ? double.NegativeInfinity : 10 * Math.Log10(energy);
    }

    /// <summary>
    /// Samples the room and checks every point against the required level and the 118 dB(A) cap,
    /// using the free-field level of each sounder (rated dB(A) at 1 m) summed energetically.
    /// Points within 1 m of a sounder take its 1 m rating.
    /// </summary>
    public static SoundResult SoundCoverage(
        RoomPolygon polygon, IReadOnlyList<(P2 Position, double LevelAt1mDb)> sounders, double requiredDb, double stepMm = 500)
    {
        var result = new SoundResult { RequiredDb = requiredDb };
        var bounds = RoomGeometryMath.BoundsOf(polygon.Outer);
        var quiet = new List<CoverageDevice>();
        var samples = RoomGeometryMath.SamplePoints(polygon, bounds, stepMm);
        double min = double.MaxValue, max = double.MinValue;

        foreach (var p in samples)
        {
            result.Samples++;
            var level = sounders.Count == 0
                ? double.NegativeInfinity
                : SumDb(sounders.Select(s => LevelAt(s.LevelAt1mDb, s.Position.DistanceTo(p) / 1000)));
            min = Math.Min(min, level);
            max = Math.Max(max, level);
            if (level < requiredDb) result.BelowRequired++;
            if (level > MaximumAlarmDb) result.AboveMaximum++;
        }

        if (result.Samples > 0)
        {
            result.MinLevelDb = double.IsNegativeInfinity(min) ? null : Math.Round(min, 1);
            result.MaxLevelDb = double.IsNegativeInfinity(max) ? null : Math.Round(max, 1);
        }
        result.BelowAreaM2 = Math.Round(result.BelowRequired * stepMm * stepMm / 1e6, 2);

        // Group the quiet samples with the coverage clustering: a "device" covers every loud-enough point.
        if (result.BelowRequired > 0)
        {
            var loud = samples
                .Where(p => sounders.Count > 0 &&
                            SumDb(sounders.Select(s => LevelAt(s.LevelAt1mDb, s.Position.DistanceTo(p) / 1000))) >= requiredDb)
                .Select(p => new CoverageDevice { Position = p, RadiusMm = stepMm * 0.5 })
                .ToList();
            foreach (var region in RoomAuditMath.Coverage(polygon, loud, stepMm).Regions)
                result.QuietRegions.Add(region);
        }

        return result;
    }
}
