using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.RoomDevices;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Checks placed fire alarm devices per room against Table 1 (detector type vs room height),
/// 6.5.2.2 / 6.5.2.3 (point detector coverage, corridors, sloped ceilings) and the acoustic alarm
/// levels (65 dB(A) or ambient + 10, 75 dB(A) where people sleep, max 118 dB(A), one tone everywhere).
/// </summary>
public sealed class CheckFireAlarmTool : IRevitMcpTool
{
    public string Name => "revit_check_fire_alarm";
    public string Description =>
        "Checks fire alarm devices per room. Uses device codes with detectorType (pointSmoke, linearSmoke, aspirating, " +
        "pointHeat, linearHeat, flame, co, sounder; detectorClass, soundLevelDb = dB(A) at 1 m, tone). Per room: " +
        "(1) Table 1 — each detector type present is suitable / conditional / unsuitable for the room height (linked " +
        "ceiling, else room height); (2) rooms without detection (excludeRoomFilter skips e.g. WCs); (3) point detector " +
        "coverage — smoke/CO/ASD radius 6.2 m, heat 4.5 m, scaled by ceilingSlopeDeg (+1 %/°, max 25 %); (4) sounders — " +
        "free-field level from every sounder (soundScope room|level) vs 65 dB(A) or ambientNoiseDb + 10 (roomNoise=[{roomFilter, " +
        "ambientNoiseDb}] per room), 75 dB(A) in rooms matching sleepingRoomFilter, max 118 dB(A); (5) one alarm tone for " +
        "all sounder codes. Rooms: roomNumbers / roomFilter / levelName, source/linkInstanceId. stepMm (default 500).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(RoomToolSupport.Fail(request, "No active document."));
        var args = request.Arguments;
        var warnings = new List<string>();

        var audit = AuditContext.Load(doc, args, warnings, out var error);
        if (audit == null) return Task.FromResult(RoomToolSupport.Fail(request, error!, warnings));

        var fireCodes = audit.Codes.Codes.Values.Where(c => c.DetectorType != null).ToList();
        if (fireCodes.Count == 0)
            return Task.FromResult(RoomToolSupport.Fail(request,
                "No device code has a detectorType — add detectorType (and soundLevelDb/tone for sounders) with revit_set_device_codes.", warnings));

        var slope = ToolArguments.GetDouble(args, "ceilingSlopeDeg", 0);
        var step = Math.Max(100, ToolArguments.GetDouble(args, "stepMm", 500));
        var exclude = ToolArguments.GetString(args, "excludeRoomFilter").Trim();
        var sleepingFilter = ToolArguments.GetString(args, "sleepingRoomFilter").Trim();
        double? ambient = DeviceAdjustSupport.Has(args, "ambientNoiseDb") ? ToolArguments.GetDouble(args, "ambientNoiseDb") : null;
        var roomNoise = DevicePlacementService.ParseArray(args, "roomNoise")
            .Select(r => (Filter: ToolArguments.GetString(r, "roomFilter").Trim(), Db: ToolArguments.GetDouble(r, "ambientNoiseDb", double.NaN)))
            .Where(r => r.Filter.Length > 0 && !double.IsNaN(r.Db))
            .ToList();
        var soundScope = ToolArguments.GetString(args, "soundScope", "room").Trim().ToLowerInvariant();

        var fireDevices = audit.Devices.Where(d => d.Code?.DetectorType != null).ToList();
        var detectors = fireDevices.Where(d => d.Code!.IsFireDetector).ToList();
        var sounders = fireDevices.Where(d => d.Code!.IsSounder).ToList();

        // (5) One alarm tone everywhere.
        var globalFindings = new List<object>();
        var sounderCodes = fireCodes.Where(c => c.IsSounder).ToList();
        var tones = sounderCodes.Where(c => c.Tone != null).Select(c => c.Tone!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (tones.Count > 1)
            globalFindings.Add(new
            {
                kind = "differentTones",
                message = "The fire alarm signal must be the same in every part of the building: sounder codes use tones " + string.Join(", ", tones) + "."
            });
        foreach (var c in sounderCodes.Where(c => c.Tone == null))
            warnings.Add($"Sounder code '{c.Code}' has no 'tone' — the single-signal rule cannot be checked for it.");

        var rooms = new List<object>();
        int roomsWithIssues = 0;
        foreach (var i in audit.SelectedRooms(args))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var room = audit.Index.Rooms[i];
            var findings = new List<object>();
            var height = audit.Index.CeilingHeightMm(i) ?? room.HeightMm;
            var excluded = exclude.Length > 0 && RoomAuditMath.TextMatches(room.Name, exclude);

            // (1) Table 1 per detector code present in the room.
            var inRoom = detectors.Where(d => d.RoomIndex == i).ToList();
            var byCode = inRoom.GroupBy(d => d.Code!.Code, StringComparer.OrdinalIgnoreCase).ToList();
            var detectorSummary = new List<object>();
            foreach (var g in byCode)
            {
                var code = g.First().Code!;
                HeightSuitability? suit = height != null ? FireAlarmRules.CheckHeight(code.DetectorType!, height.Value, code.DetectorClass) : null;
                detectorSummary.Add(new
                {
                    code = code.Code,
                    detectorType = code.DetectorType,
                    count = g.Count(),
                    heightSuitability = suit?.Status ?? "unknown",
                    band = suit?.Band,
                    note = suit?.Note
                });
                if (suit is { Status: not "suitable" })
                    findings.Add(new { kind = "height" + Capitalize(suit.Status), code = code.Code, message = $"{code.DetectorType} is {suit.Status} for {height / 1000:0.0#} m ({suit.Band ?? "> 45 m"}). {suit.Note}".Trim() });
            }

            // (2) Detection present.
            if (!excluded && inRoom.Count == 0)
                findings.Add(new { kind = "noDetection", message = "No fire detector in the room." });

            // (3) Point detector coverage by the 6.5.2.x radius.
            object? coverage = null;
            var pointDetectors = inRoom
                .Select(d => (Device: d, Spacing: FireAlarmRules.Spacing(d.Code!.DetectorType!, slope)))
                .Where(t => t.Spacing != null)
                .ToList();
            if (pointDetectors.Count > 0)
            {
                var result = RoomAuditMath.Coverage(room.Polygon,
                    pointDetectors.Select(t => new CoverageDevice { Id = t.Device.Id, Position = t.Device.Point, RadiusMm = t.Spacing!.RadiusMm }).ToList(),
                    step);
                coverage = new
                {
                    coveredPercent = Math.Round(result.CoveredFraction * 100, 1),
                    uncoveredAreaM2 = result.UncoveredAreaM2,
                    uncoveredRegions = result.Regions.Take(5).Select(r => new { areaM2 = r.AreaM2, center = RoomAnalysis.Point(r.Center) }).ToList()
                };
                if (result.Uncovered > 0)
                    findings.Add(new { kind = "detectionGap", message = $"{result.UncoveredAreaM2} m² is outside every point detector's radius (smoke 6.2 m / heat 4.5 m{(slope > 0 ? $", ×{FireAlarmRules.SlopeFactor(slope):0.##} for slope" : "")})." });

                var corridor = FireAlarmRules.Corridor(room.Polygon, 1, 1);
                if (corridor.IsCorridor)
                    findings.AddRange(CorridorFindings(pointDetectors.Select(t => (t.Device.Point, t.Spacing!)).ToList(), room));
            }
            else if (inRoom.Count > 0)
            {
                warnings.Add($"Room {room.Number}: only linear/flame detectors — their coverage is not modelled; Table 1 is checked.");
            }

            // (4) Sounders.
            object? sound = null;
            var relevantSounders = sounders.Where(s => soundScope == "level"
                ? Math.Abs(s.LevelElevationMm - room.HostLevelElevationMm) < 1
                : s.RoomIndex == i).ToList();
            if (sounderCodes.Count > 0 && !excluded)
            {
                double? roomAmbient = roomNoise.Where(r => RoomAuditMath.TextMatches(room.Name, r.Filter)).Select(r => (double?)r.Db).FirstOrDefault();
                var sleeping = sleepingFilter.Length > 0 && RoomAuditMath.TextMatches(room.Name, sleepingFilter);
                var required = FireAlarmRules.RequiredAlarmDb(roomAmbient ?? ambient, sleeping);
                var result = FireAlarmRules.SoundCoverage(room.Polygon,
                    relevantSounders.Select(s => (s.Point, s.Code!.SoundLevelDb ?? 0)).ToList(), required, step);
                sound = new
                {
                    sounders = relevantSounders.Count,
                    requiredDb = required,
                    sleeping,
                    minLevelDb = result.MinLevelDb,
                    maxLevelDb = result.MaxLevelDb,
                    belowRequiredAreaM2 = result.BelowAreaM2,
                    quietRegions = result.QuietRegions.Take(5).Select(r => new { areaM2 = r.AreaM2, center = RoomAnalysis.Point(r.Center) }).ToList()
                };
                if (result.BelowRequired > 0)
                    findings.Add(new { kind = "tooQuiet", message = relevantSounders.Count == 0 ? $"No sounder reaches the room (needs {required:0} dB(A))." : $"{result.BelowAreaM2} m² below {required:0} dB(A) (min {result.MinLevelDb:0.#} dB(A))." });
                if (result.AboveMaximum > 0)
                    findings.Add(new { kind = "tooLoud", message = $"Above {FireAlarmRules.MaximumAlarmDb:0} dB(A) somewhere in the room (max {result.MaxLevelDb:0.#} dB(A))." });
            }

            if (findings.Count > 0) roomsWithIssues++;
            rooms.Add(new
            {
                roomNumber = room.Number,
                roomName = room.Name,
                level = room.HostLevelName,
                heightMm = height,
                excluded,
                detectors = detectorSummary,
                detectorCoverage = coverage,
                sound,
                findings
            });
        }

        if (sounderCodes.Count > 0)
            warnings.Add("Sound levels are free-field estimates from each sounder's rated dB(A) at 1 m (−6 dB per doubling of distance); " +
                         "walls, doors and room acoustics are not modelled. Verify with a class 2 (EN 61672-1) meter, slow response, A-weighting.");

        sw.Stop();
        return Task.FromResult(RoomToolSupport.Ok(request, sw,
            $"{roomsWithIssues} of {rooms.Count} room(s) have fire alarm findings; {detectors.Count} detector(s), {sounders.Count} sounder(s) checked.",
            new
            {
                source = audit.Index.Service.Source.Label,
                ceilingSlopeDeg = slope,
                global = globalFindings,
                rooms
            },
            warnings));
    }

    /// <summary>Corridor (≤ 2 m) rule: centreline spacing and end-wall distance of the point detectors.</summary>
    private static IEnumerable<object> CorridorFindings(List<(P2 Point, DetectorSpacing Spacing)> detectors, RoomRecord room)
    {
        var spacing = detectors.Min(d => d.Spacing.CorridorSpacingMm);
        var end = detectors.Min(d => d.Spacing.CorridorEndDistanceMm);
        var layout = FireAlarmRules.Corridor(room.Polygon, spacing, end);
        if (!layout.IsCorridor || layout.Points.Count == 0) yield break;

        // Project the detectors onto the corridor axis (the line through the layout points).
        var dir = layout.Points.Count > 1 ? layout.Points[^1].Minus(layout.Points[0]).Normalized() : new P2(1, 0);
        var half = layout.LengthMm / 2;
        var center = layout.Points.Count > 1 ? layout.Points[0].Plus(layout.Points[^1]).Scaled(0.5) : layout.Points[0];
        var along = detectors.Select(d => d.Point.Minus(center).Dot(dir) + half).OrderBy(x => x).ToList();

        if (along[0] > end + 1 || layout.LengthMm - along[^1] > end + 1)
            yield return new { kind = "corridorEnd", message = $"Corridor: a detector is more than {end:0} mm from an end wall." };
        for (int k = 1; k < along.Count; k++)
        {
            if (along[k] - along[k - 1] > spacing + 1)
            {
                yield return new { kind = "corridorSpacing", message = $"Corridor: detectors {along[k] - along[k - 1]:0} mm apart (max {spacing:0} mm)." };
                break;
            }
        }
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
}
