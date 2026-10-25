using System.Text.RegularExpressions;

namespace RevitMCP.Addin.RoomDevices;

/// <summary>A room as the audit math sees it: footprint plus vertical extent.</summary>
public sealed class RoomCandidate
{
    public string Number { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public double AreaM2 { get; set; }
    public RoomPolygon Polygon { get; set; } = new();
    public double FloorZMm { get; set; }
    public double? HeightMm { get; set; }
    /// <summary>Elevation of the host level the room stands on; null when unknown.</summary>
    public double? LevelElevationMm { get; set; }
    /// <summary>
    /// Floor of the next storey above the room (see <see cref="RoomAuditMath.NextStoreyAboveMm"/>);
    /// null when there is none or it is unknown.
    /// </summary>
    public double? StoreyTopMm { get; set; }
}

/// <summary>One per-room requirement: rooms matching the filter need min…max devices of a code.</summary>
public sealed class DeviceRule
{
    public string RoomFilter { get; set; } = string.Empty;
    public string ExcludeRoomFilter { get; set; } = string.Empty;
    public string[] RoomNumbers { get; set; } = [];
    public double? MinAreaM2 { get; set; }
    public double? MaxAreaM2 { get; set; }
    public string Code { get; set; } = string.Empty;
    public int? Min { get; set; }
    public int? Max { get; set; }

    public string Describe()
    {
        var who = RoomNumbers.Length > 0 ? "rooms " + string.Join(", ", RoomNumbers)
            : RoomFilter.Length > 0 ? $"rooms matching '{RoomFilter}'" : "every room";
        if (ExcludeRoomFilter.Length > 0) who += $" except '{ExcludeRoomFilter}'";
        if (MinAreaM2 != null) who += $", area ≥ {MinAreaM2} m²";
        if (MaxAreaM2 != null) who += $", area ≤ {MaxAreaM2} m²";
        var range = Min != null && Max != null ? $"{Min}–{Max}" : Min != null ? $"≥ {Min}" : Max != null ? $"≤ {Max}" : "any";
        return $"{who}: {Code} {range}";
    }
}

public sealed class RuleFinding
{
    public int RuleIndex { get; set; }
    public string RoomNumber { get; set; } = string.Empty;
    public string RoomName { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public int Count { get; set; }
    public int? Min { get; set; }
    public int? Max { get; set; }
    /// <summary>"missing" (below min) or "excess" (above max).</summary>
    public string Kind { get; set; } = "missing";
}

/// <summary>A device for coverage: position plus, for cameras, facing and field of view.</summary>
public sealed class CoverageDevice
{
    public long Id { get; set; }
    public P2 Position { get; set; }
    public double RadiusMm { get; set; }
    /// <summary>Unit facing direction; required when <see cref="FovDeg"/> is set.</summary>
    public P2? Facing { get; set; }
    /// <summary>Full horizontal field of view; null = omnidirectional.</summary>
    public double? FovDeg { get; set; }
}

public sealed class UncoveredRegion
{
    public double AreaM2 { get; set; }
    public P2 Center { get; set; }
    public int Samples { get; set; }
}

public sealed class CoverageResult
{
    public int Samples { get; set; }
    public int Uncovered { get; set; }
    public double CoveredFraction => Samples == 0 ? 1 : 1.0 - (double)Uncovered / Samples;
    public double UncoveredAreaM2 { get; set; }
    public List<UncoveredRegion> Regions { get; } = [];
}

/// <summary>
/// Pure logic for the device audit tools: which room a device stands in, rotation deltas,
/// per-room count rules, sampled coverage and wall-offset checks.
/// No Revit API dependency — unit tested in RevitMCP.Tests.
/// </summary>
public static class RoomAuditMath
{
    /// <summary>Vertical slack when matching a device to a room's floor-to-ceiling range.</summary>
    public const double VerticalSlackMm = 300;

    // ── Room lookup ───────────────────────────────────────────────────────────

    /// <summary>
    /// Plan distance within which a point on or just outside a room edge still counts as in the room.
    /// Wall devices placed with offsetFromWallMm = 0 sit exactly on the finished face (#57).
    /// </summary>
    public const double BoundaryToleranceMm = 5;

    /// <summary>
    /// Allowance above the room's own top when the next storey is unknown, as in
    /// revit_get_linked_elements_in_room: Revit's default room height (2438 mm) is below most ceilings,
    /// so ceiling devices sit above it.
    /// </summary>
    public const double CeilingAllowanceMm = 1500;

    /// <summary>A level less than this above a room floor is not the next storey (e.g. a ceiling reference level).</summary>
    public const double MinStoreyHeightMm = 2000;

    /// <summary>Two level elevations closer than this are the same level.</summary>
    public const double LevelMatchToleranceMm = 1;

    /// <summary>
    /// Floor of the next storey above <paramref name="floorZMm"/>: the lowest of
    /// <paramref name="storeyElevationsMm"/> at least <see cref="MinStoreyHeightMm"/> above it; null when none.
    /// </summary>
    public static double? NextStoreyAboveMm(double floorZMm, IEnumerable<double> storeyElevationsMm)
    {
        double? next = null;
        foreach (var e in storeyElevationsMm)
            if (e >= floorZMm + MinStoreyHeightMm && (next == null || e < next)) next = e;
        return next;
    }

    /// <summary>
    /// Highest z that still counts as in the room: up to the next storey's floor when it is known
    /// (so ceiling devices above a default-height room count), else the room top +
    /// <see cref="CeilingAllowanceMm"/>; never below the room top + <see cref="VerticalSlackMm"/>.
    /// The room top is floor + height, or the next storey (6 m when that is unknown too) without a height.
    /// </summary>
    public static double VerticalTopMm(RoomCandidate room)
    {
        var top = room.FloorZMm + (room.HeightMm ?? (room.StoreyTopMm - room.FloorZMm) ?? 6000);
        return Math.Max(top + VerticalSlackMm, room.StoreyTopMm ?? top + CeilingAllowanceMm);
    }

    /// <summary>
    /// Index of the room containing the point: plan inside the footprint (or within
    /// <see cref="BoundaryToleranceMm"/> of its edge) and z from the room floor − <see cref="VerticalSlackMm"/>
    /// up to <see cref="VerticalTopMm"/>. Where several rooms qualify (rooms of two storeys overlap
    /// near a slab), a room on the device's own level (<paramref name="levelElevationMm"/>) wins, then one
    /// that strictly contains the point in plan, then one whose floor is at or below z, then the highest floor.
    /// -1 if none.
    /// </summary>
    public static int LocateRoom(IReadOnlyList<RoomCandidate> rooms, P2 point, double zMm, double? levelElevationMm = null)
    {
        var best = -1;
        (bool Level, bool Strict, bool Above, double Floor) bestKey = default;
        for (int i = 0; i < rooms.Count; i++)
        {
            var r = rooms[i];
            if (zMm < r.FloorZMm - VerticalSlackMm || zMm > VerticalTopMm(r)) continue;

            var strict = RoomGeometryMath.Contains(r.Polygon, point);
            if (!strict && RoomGeometryMath.DistanceToBoundary(r.Polygon, point) > BoundaryToleranceMm) continue;

            var key = (
                Level: levelElevationMm != null && r.LevelElevationMm != null &&
                       Math.Abs(levelElevationMm.Value - r.LevelElevationMm.Value) < LevelMatchToleranceMm,
                Strict: strict,
                Above: zMm >= r.FloorZMm,
                Floor: r.FloorZMm);
            if (best >= 0 && !Better(key, bestKey)) continue;
            best = i;
            bestKey = key;
        }
        return best;

        static bool Better((bool Level, bool Strict, bool Above, double Floor) a, (bool Level, bool Strict, bool Above, double Floor) b) =>
            a.Level != b.Level ? a.Level
            : a.Strict != b.Strict ? a.Strict
            : a.Above != b.Above ? a.Above
            : a.Floor > b.Floor;
    }

    /// <summary>
    /// Whether a device belongs to the storey of <paramref name="rooms"/> (the rooms of one level, for
    /// a levelName filter): its level is one of the rooms' levels, or else its z lies from the lowest
    /// room floor − <see cref="VerticalSlackMm"/> up to (not including) the highest <see cref="VerticalTopMm"/>.
    /// </summary>
    public static bool OnStorey(IReadOnlyList<RoomCandidate> rooms, double zMm, double? levelElevationMm)
    {
        if (rooms.Count == 0) return false;
        if (levelElevationMm != null && rooms.Any(r => r.LevelElevationMm != null &&
                Math.Abs(r.LevelElevationMm.Value - levelElevationMm.Value) < LevelMatchToleranceMm))
            return true;
        var bottom = rooms.Min(r => r.FloorZMm) - VerticalSlackMm;
        var top = rooms.Max(VerticalTopMm);
        return zMm >= bottom && zMm < top;
    }

    /// <summary>Number of devices listed as outside every room; the total is always reported.</summary>
    public const int MaxListedOutsideRooms = 50;

    // ── Rotation ──────────────────────────────────────────────────────────────

    /// <summary>Signed rotation in (-180, 180] that turns <paramref name="currentDeg"/> to <paramref name="targetDeg"/>.</summary>
    public static double DeltaDeg(double currentDeg, double targetDeg)
    {
        var d = RoomGeometryMath.NormalizeDeg(targetDeg - currentDeg);
        return d > 180 ? d - 360 : d;
    }

    /// <summary>Facing angle from <paramref name="from"/> towards <paramref name="toward"/>; null when the points coincide.</summary>
    public static double? FaceTowardDeg(P2 from, P2 toward)
    {
        var d = toward.Minus(from);
        return d.Length < 1e-6 ? null : RoomGeometryMath.AngleDeg(d);
    }

    /// <summary>Smallest absolute difference between two angles, in degrees.</summary>
    public static double AngleDifferenceDeg(double a, double b) => Math.Abs(DeltaDeg(a, b));

    // ── Count rules ───────────────────────────────────────────────────────────

    public static bool RuleMatches(DeviceRule rule, RoomCandidate room)
    {
        if (rule.RoomNumbers.Length > 0 &&
            !rule.RoomNumbers.Any(n => string.Equals(n.Trim(), room.Number.Trim(), StringComparison.OrdinalIgnoreCase)))
            return false;
        if (rule.RoomFilter.Length > 0 && !TextMatches(room.Name, rule.RoomFilter)) return false;
        if (rule.ExcludeRoomFilter.Length > 0 && TextMatches(room.Name, rule.ExcludeRoomFilter)) return false;
        if (rule.MinAreaM2 != null && room.AreaM2 < rule.MinAreaM2) return false;
        if (rule.MaxAreaM2 != null && room.AreaM2 > rule.MaxAreaM2) return false;
        return true;
    }

    /// <summary>Case-insensitive regex match, falling back to a plain substring test for invalid patterns.</summary>
    public static bool TextMatches(string text, string pattern)
    {
        try
        {
            return Regex.IsMatch(text ?? string.Empty, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException)
        {
            return (text ?? string.Empty).IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    /// <summary>
    /// Applies every rule to every room. <paramref name="counts"/> maps room index → code → device count.
    /// Returns the violations plus, per rule, how many rooms it applied to.
    /// </summary>
    public static (List<RuleFinding> Findings, int[] RoomsChecked) EvaluateRules(
        IReadOnlyList<RoomCandidate> rooms,
        IReadOnlyDictionary<int, Dictionary<string, int>> counts,
        IReadOnlyList<DeviceRule> rules)
    {
        var findings = new List<RuleFinding>();
        var checkedRooms = new int[rules.Count];

        for (int r = 0; r < rules.Count; r++)
        {
            var rule = rules[r];
            for (int i = 0; i < rooms.Count; i++)
            {
                var room = rooms[i];
                if (!RuleMatches(rule, room)) continue;
                checkedRooms[r]++;

                var count = counts.TryGetValue(i, out var byCode) && byCode.TryGetValue(rule.Code, out var c) ? c : 0;
                string? kind = rule.Min != null && count < rule.Min ? "missing"
                    : rule.Max != null && count > rule.Max ? "excess"
                    : null;
                if (kind == null) continue;

                findings.Add(new RuleFinding
                {
                    RuleIndex = r,
                    RoomNumber = room.Number,
                    RoomName = room.Name,
                    Code = rule.Code,
                    Count = count,
                    Min = rule.Min,
                    Max = rule.Max,
                    Kind = kind
                });
            }
        }

        return (findings, checkedRooms);
    }

    // ── Coverage ──────────────────────────────────────────────────────────────

    public static bool Covers(CoverageDevice device, P2 point)
    {
        var d = point.Minus(device.Position);
        var distance = d.Length;
        if (distance > device.RadiusMm) return false;
        if (device.FovDeg == null || distance < 1e-6) return true;
        if (device.Facing is not { } facing || facing.Length < 1e-9) return false;
        return AngleDifferenceDeg(RoomGeometryMath.AngleDeg(facing), RoomGeometryMath.AngleDeg(d)) <= device.FovDeg.Value / 2 + 1e-9;
    }

    /// <summary>
    /// The area one inside sample stands for: the room's real area shared equally by its samples. A raster cell
    /// (step²) overstates it where cells straddle the boundary, so a fully uncovered room came out larger than
    /// itself (M18: 56.0 of 54.16 m²). Falls back to the cell area when the polygon has no area.
    /// </summary>
    public static double SampleAreaM2(RoomPolygon polygon, int samples, double stepMm)
    {
        var cell = stepMm * stepMm / 1e6;
        if (samples <= 0) return cell;
        var room = RoomGeometryMath.AreaMm2(polygon) / 1e6;
        return room > 0 ? room / samples : cell;
    }

    /// <summary>
    /// Samples the room on a <paramref name="stepMm"/> raster and reports the share covered by at least
    /// one device, with the uncovered samples grouped into connected regions (area + centre).
    /// </summary>
    public static CoverageResult Coverage(RoomPolygon polygon, IReadOnlyList<CoverageDevice> devices, double stepMm = 500)
    {
        var result = new CoverageResult();
        var bounds = RoomGeometryMath.BoundsOf(polygon.Outer);
        if (stepMm <= 0) return result;

        var nx = (int)Math.Ceiling(bounds.Width / stepMm);
        var ny = (int)Math.Ceiling(bounds.Height / stepMm);
        var uncovered = new bool[Math.Max(0, nx), Math.Max(0, ny)];

        for (int iy = 0; iy < ny; iy++)
        for (int ix = 0; ix < nx; ix++)
        {
            var p = new P2(bounds.MinX + stepMm * (ix + 0.5), bounds.MinY + stepMm * (iy + 0.5));
            if (!RoomGeometryMath.Contains(polygon, p)) continue;
            result.Samples++;
            if (devices.Any(d => Covers(d, p))) continue;
            result.Uncovered++;
            uncovered[ix, iy] = true;
        }

        var cellM2 = SampleAreaM2(polygon, result.Samples, stepMm);
        result.UncoveredAreaM2 = Math.Round(result.Uncovered * cellM2, 2);

        // Group uncovered cells into 4-connected regions.
        var seen = new bool[Math.Max(0, nx), Math.Max(0, ny)];
        for (int iy = 0; iy < ny; iy++)
        for (int ix = 0; ix < nx; ix++)
        {
            if (!uncovered[ix, iy] || seen[ix, iy]) continue;
            var queue = new Queue<(int X, int Y)>();
            queue.Enqueue((ix, iy));
            seen[ix, iy] = true;
            int count = 0;
            double sx = 0, sy = 0;
            while (queue.Count > 0)
            {
                var (cx, cy) = queue.Dequeue();
                count++;
                sx += bounds.MinX + stepMm * (cx + 0.5);
                sy += bounds.MinY + stepMm * (cy + 0.5);
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int x2 = cx + dx, y2 = cy + dy;
                    if (x2 < 0 || y2 < 0 || x2 >= nx || y2 >= ny || seen[x2, y2] || !uncovered[x2, y2]) continue;
                    seen[x2, y2] = true;
                    queue.Enqueue((x2, y2));
                }
            }
            result.Regions.Add(new UncoveredRegion
            {
                Samples = count,
                AreaM2 = Math.Round(count * cellM2, 2),
                Center = new P2(sx / count, sy / count)
            });
        }

        result.Regions.Sort((a, b) => b.AreaM2.CompareTo(a.AreaM2));
        return result;
    }

    // ── Wall offset ───────────────────────────────────────────────────────────

    /// <summary>
    /// Position of a point relative to a wall face: distance along the face and the signed offset
    /// from it (positive into the room, negative inside or behind the wall).
    /// </summary>
    public static (double AlongMm, double OffsetMm) WallOffset(WallFace face, P2 point) =>
        (face.AlongOf(point), point.Minus(face.Start).Dot(face.NormalIntoRoom));

    /// <summary>The face whose segment is nearest to the point, measured in plan.</summary>
    public static WallFace? NearestFaceTo(IReadOnlyList<WallFace> faces, P2 point) => RoomGeometryMath.NearestFace(faces, point);

    // ── Mount alignment ───────────────────────────────────────────────────────

    /// <summary>
    /// Compares a device with what its mount promises. Every value is optional so a check whose input
    /// is unknown (no ceiling, no wall face) is simply skipped. Returns only the deviations beyond
    /// <paramref name="toleranceMm"/>.
    /// <list type="bullet">
    /// <item>wallOffset: signed distance from the wall face vs offsetFromWallMm (wall)</item>
    /// <item>height: height above the level/floor vs heightMm (wall, floor)</item>
    /// <item>ceilingGap: ceiling height minus device height vs offsetFromCeilingMm (ceiling)</item>
    /// </list>
    /// </summary>
    public static List<MountDeviation> CheckMount(
        string mount,
        double toleranceMm,
        double? wallOffsetMm = null,
        double? expectedWallOffsetMm = null,
        double? heightMm = null,
        double? expectedHeightMm = null,
        double? ceilingGapMm = null,
        double? expectedCeilingGapMm = null)
    {
        var result = new List<MountDeviation>();
        void Check(string axis, double? actual, double? expected)
        {
            if (actual == null || expected == null) return;
            var deviation = actual.Value - expected.Value;
            if (Math.Abs(deviation) > toleranceMm)
                result.Add(new MountDeviation { Axis = axis, ActualMm = actual.Value, ExpectedMm = expected.Value, DeviationMm = deviation });
        }

        switch (mount)
        {
            case DeviceMounts.Wall:
                Check("wallOffset", wallOffsetMm, expectedWallOffsetMm);
                Check("height", heightMm, expectedHeightMm);
                break;
            case DeviceMounts.Floor:
                Check("height", heightMm, expectedHeightMm);
                break;
            case DeviceMounts.Ceiling:
                Check("ceilingGap", ceilingGapMm, expectedCeilingGapMm);
                break;
        }
        return result;
    }
}

/// <summary>One way a device is off its mount: actual vs expected, in mm.</summary>
public sealed class MountDeviation
{
    /// <summary>"wallOffset", "height" or "ceilingGap".</summary>
    public string Axis { get; set; } = string.Empty;
    public double ActualMm { get; set; }
    public double ExpectedMm { get; set; }
    /// <summary>Actual minus expected. For wallOffset, negative means inside or behind the wall.</summary>
    public double DeviationMm { get; set; }
}
