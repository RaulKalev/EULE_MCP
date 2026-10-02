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
    /// Index of the room containing the point: plan inside the footprint and z between the room
    /// floor and its top (height or 6 m when unknown), with <see cref="VerticalSlackMm"/> slack.
    /// When several stacked rooms qualify the one with the highest floor at or below z wins. -1 if none.
    /// </summary>
    public static int LocateRoom(IReadOnlyList<RoomCandidate> rooms, P2 point, double zMm)
    {
        var best = -1;
        for (int i = 0; i < rooms.Count; i++)
        {
            var r = rooms[i];
            var top = r.FloorZMm + (r.HeightMm ?? 6000);
            if (zMm < r.FloorZMm - VerticalSlackMm || zMm > top + VerticalSlackMm) continue;
            if (!RoomGeometryMath.Contains(r.Polygon, point)) continue;
            if (best < 0 || r.FloorZMm > rooms[best].FloorZMm) best = i;
        }
        return best;
    }

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

        var cellM2 = stepMm * stepMm / 1e6;
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
