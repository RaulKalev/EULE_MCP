using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.RoomDevices;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

// ── 1.4 Linked elements in a room ───────────────────────────────────────────────

public sealed class GetLinkedElementsInRoomTool : IRevitMcpTool
{
    public string Name => "revit_get_linked_elements_in_room";
    public string Description =>
        "Lists elements of the given categories (e.g. Lighting Fixtures, Air Terminals, Ceilings, Furniture) that stand " +
        "in a room, in host internal mm: id, model, category, typeName, location {x,y,z} (when the element has a point) " +
        "and bbox. Rooms come from source/linkInstanceId (roomNumber or roomNumbers, levelName); elements from " +
        "elementLinkInstanceIds[] (other links, e.g. EK/KVJ) and/or includeHost — default: the room source model. " +
        "An element is in the room when its point (or plan centre) is inside the footprint and it spans the room's storey.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(RoomToolSupport.Fail(request, "No active document."));
        var args = request.Arguments;
        var warnings = new List<string>();

        var categories = ToolArguments.GetStringArray(args, "categories");
        if (categories.Length == 0) return Task.FromResult(RoomToolSupport.Fail(request, "Provide categories[]."));

        var roomService = RoomSourceService.FromArguments(doc, args, out var error);
        if (roomService == null) return Task.FromResult(RoomToolSupport.Fail(request, error!));
        var filter = RoomSourceService.ParseFilter(args);
        if (filter.RoomNumbers.Length == 0 && filter.NameFilter.Length == 0)
            return Task.FromResult(RoomToolSupport.Fail(request, "Provide roomNumber, roomNumbers or roomFilter."));
        var rooms = roomService.LoadRooms(filter, warnings, 200);
        if (rooms.Count == 0) return Task.FromResult(RoomToolSupport.Fail(request, $"No rooms matched in the {roomService.Source.Label}.", warnings));

        // Element models: explicit links and/or the host; default = the room source model.
        var sources = new List<RoomSourceService>();
        foreach (var id in ToolArguments.GetLongArray(args, "elementLinkInstanceIds"))
        {
            var s = RoomSourceService.ResolveSource(doc, "link", id, out var linkError);
            if (s == null) warnings.Add(linkError!);
            else sources.Add(new RoomSourceService(s));
        }
        if (ToolArguments.GetBool(args, "includeHost"))
            sources.Add(new RoomSourceService(new RoomSource { HostDoc = doc, Doc = doc }));
        if (sources.Count == 0) sources.Add(roomService);

        var limit = Math.Max(1, ToolArguments.GetInt(args, "limitPerRoom", 500));
        var payload = new List<object>();
        var total = 0;
        foreach (var room in rooms)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var elements = new List<object>();
            foreach (var source in sources)
            {
                foreach (var e in source.ElementsInRoom(room, categories, warnings))
                {
                    if (elements.Count >= limit) break;
                    elements.Add(new
                    {
                        id = e.Element.Id.Value,
                        model = source.Source.Label,
                        linkInstanceId = source.Source.Link?.Id.Value,
                        category = e.Category,
                        typeName = e.TypeName,
                        location = e.Location is { } l ? new { x = Math.Round(l.X, 1), y = Math.Round(l.Y, 1), z = Math.Round(e.LocationZ ?? 0, 1) } : null,
                        bbox = new
                        {
                            minX = Math.Round(e.Plan.MinX, 1), minY = Math.Round(e.Plan.MinY, 1), minZ = Math.Round(e.MinZ, 1),
                            maxX = Math.Round(e.Plan.MaxX, 1), maxY = Math.Round(e.Plan.MaxY, 1), maxZ = Math.Round(e.MaxZ, 1)
                        }
                    });
                }
            }
            total += elements.Count;
            payload.Add(new { room = RoomAnalysis.RoomSummary(room), count = elements.Count, elements });
        }

        sw.Stop();
        return Task.FromResult(RoomToolSupport.Ok(request, sw,
            $"{total} element(s) in {rooms.Count} room(s).", new { rooms = payload }, warnings.Distinct().ToList()));
    }
}

// ── Shared audit setup ──────────────────────────────────────────────────────────

internal sealed class AuditContext
{
    public DeviceCodeContext Codes { get; set; } = null!;
    public RoomIndex Index { get; set; } = null!;
    public List<DeviceRecord> Devices { get; set; } = [];

    public static AuditContext? Load(Document doc, Dictionary<string, object?> args, List<string> warnings, out string? error)
    {
        var codes = DeviceCodeContext.Load(doc, args, out error);
        if (codes == null) return null;
        warnings.AddRange(codes.ParseErrors.Select(e => $"Device code map: {e}"));

        var service = RoomSourceService.FromArguments(doc, args, out error);
        if (service == null) return null;

        var filter = RoomSourceService.ParseFilter(args);
        var index = RoomIndex.Build(service, new RoomFilter { LevelName = filter.LevelName }, warnings);
        if (index.Rooms.Count == 0)
        {
            error = $"No rooms found in the {service.Source.Label}.";
            return null;
        }

        var devices = DeviceAudit.Inventory(doc, codes, DeviceAudit.Codes(args), [], warnings);
        foreach (var d in devices) index.Locate(d);
        return new AuditContext { Codes = codes, Index = index, Devices = devices };
    }

    /// <summary>Indexes of the rooms the caller asked about (roomNumbers / roomFilter), or all rooms.</summary>
    public List<int> SelectedRooms(Dictionary<string, object?> args)
    {
        var filter = RoomSourceService.ParseFilter(args);
        var result = new List<int>();
        for (int i = 0; i < Index.Rooms.Count; i++)
        {
            var r = Index.Rooms[i];
            if (filter.RoomNumbers.Length > 0 && !filter.RoomNumbers.Any(n => string.Equals(n.Trim(), r.Number.Trim(), StringComparison.OrdinalIgnoreCase))) continue;
            if (filter.NameFilter.Length > 0 && !RoomAuditMath.TextMatches(r.Name, filter.NameFilter)) continue;
            result.Add(i);
        }
        return result;
    }
}

internal static class MountChecks
{
    /// <summary>
    /// Runs the mount check for one device in its room. Returns the deviations and a suggested target
    /// point (host mm) that would satisfy the code, or null when nothing can be suggested.
    /// </summary>
    public static (List<MountDeviation> Deviations, (P2 Point, double Z)? Target, string? Note) Check(
        AuditContext audit, DeviceRecord device, double toleranceMm, bool includeWallOffset = true)
    {
        var code = device.Code!;
        var room = audit.Index.Rooms[device.RoomIndex];
        var levelZ = room.HostLevelElevationMm;

        switch (code.Mount)
        {
            case DeviceMounts.Wall:
            {
                var face = RoomAuditMath.NearestFaceTo(room.Faces, device.Point);
                double? offset = null;
                P2 targetPoint = device.Point;
                if (face != null && includeWallOffset)
                {
                    var (along, off) = RoomAuditMath.WallOffset(face, device.Point);
                    offset = off;
                    targetPoint = face.PointAt(Math.Max(0, Math.Min(face.LengthMm, along)), code.OffsetFromWallMm);
                }
                var deviations = RoomAuditMath.CheckMount(DeviceMounts.Wall, toleranceMm,
                    wallOffsetMm: offset, expectedWallOffsetMm: includeWallOffset ? code.OffsetFromWallMm : null,
                    heightMm: device.ZMm - levelZ, expectedHeightMm: code.HeightMm);
                var targetZ = code.HeightMm is { } h ? levelZ + h : device.ZMm;
                return (deviations, (targetPoint, targetZ), face == null ? "No wall face found." : null);
            }

            case DeviceMounts.Floor:
            {
                var deviations = RoomAuditMath.CheckMount(DeviceMounts.Floor, toleranceMm,
                    heightMm: device.ZMm - levelZ, expectedHeightMm: code.HeightMm ?? 0);
                return (deviations, (device.Point, levelZ + (code.HeightMm ?? 0)), null);
            }

            case DeviceMounts.Ceiling:
            {
                var ceiling = audit.Index.CeilingHeightMm(device.RoomIndex);
                if (ceiling == null) return ([], null, "No linked ceiling above the room — ceiling gap not checked.");
                var ceilingZ = room.FloorZMm + ceiling.Value;
                var deviations = RoomAuditMath.CheckMount(DeviceMounts.Ceiling, toleranceMm,
                    ceilingGapMm: ceilingZ - device.ZMm, expectedCeilingGapMm: code.OffsetFromCeilingMm);
                return (deviations, (device.Point, ceilingZ - code.OffsetFromCeilingMm), null);
            }
        }
        return ([], null, null);
    }

    public static object DeviationPayload(MountDeviation d) => new
    {
        axis = d.Axis,
        actualMm = Math.Round(d.ActualMm, 1),
        expectedMm = Math.Round(d.ExpectedMm, 1),
        deviationMm = Math.Round(d.DeviationMm, 1)
    };
}

// ── 4.2 Devices per room ────────────────────────────────────────────────────────

public sealed class CheckDevicesPerRoomTool : IRevitMcpTool
{
    public string Name => "revit_check_devices_per_room";
    public string Description =>
        "Checks device counts per room against rules and reports shortfalls/excess, devices outside every room, and " +
        "devices whose height is off their code by more than heightToleranceMm (default 50; wall/floor: height above " +
        "level, ceiling: gap below the linked ceiling). rules=[{code, min?, max?, roomFilter? (name regex), " +
        "excludeRoomFilter?, roomNumbers?, minAreaM2?, maxAreaM2?}] — e.g. every room needs ATS_SA ≥ 1 except WCs under " +
        "4 m²: {code:'ATS_SA', min:1, minAreaM2:4} plus {code:'ATS_SA', min:1, excludeRoomFilter:'WC'}. " +
        "Rooms from source/linkInstanceId (levelName); devices from the device code map (codes[] limits the codes).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(RoomToolSupport.Fail(request, "No active document."));
        var args = request.Arguments;
        var warnings = new List<string>();

        var rules = DevicePlacementService.ParseArray(args, "rules").Select(r => new DeviceRule
        {
            Code = (ToolArguments.GetString(r, "code") is { Length: > 0 } c ? c : ToolArguments.GetString(r, "deviceCode")).Trim(),
            Min = DeviceAdjustSupport.Has(r, "min") ? ToolArguments.GetInt(r, "min") : null,
            Max = DeviceAdjustSupport.Has(r, "max") ? ToolArguments.GetInt(r, "max") : null,
            RoomFilter = ToolArguments.GetString(r, "roomFilter").Trim(),
            ExcludeRoomFilter = ToolArguments.GetString(r, "excludeRoomFilter").Trim(),
            RoomNumbers = ToolArguments.GetStringArray(r, "roomNumbers"),
            MinAreaM2 = DeviceAdjustSupport.Has(r, "minAreaM2") ? ToolArguments.GetDouble(r, "minAreaM2") : null,
            MaxAreaM2 = DeviceAdjustSupport.Has(r, "maxAreaM2") ? ToolArguments.GetDouble(r, "maxAreaM2") : null
        }).ToList();
        if (rules.Count == 0 || rules.Any(r => r.Code.Length == 0))
            return Task.FromResult(RoomToolSupport.Fail(request, "Provide rules=[{code, min?, max?, roomFilter?, …}] — every rule needs a code."));

        var audit = AuditContext.Load(doc, args, warnings, out var error);
        if (audit == null) return Task.FromResult(RoomToolSupport.Fail(request, error!, warnings));
        foreach (var unknown in rules.Select(r => r.Code).Distinct(StringComparer.OrdinalIgnoreCase).Where(c => !audit.Codes.Codes.ContainsKey(c)))
            warnings.Add($"Rule code '{unknown}' is not in the device code map; it always counts 0.");

        var counts = new Dictionary<int, Dictionary<string, int>>();
        foreach (var d in audit.Devices.Where(d => d.RoomIndex >= 0 && d.Code != null))
        {
            if (!counts.TryGetValue(d.RoomIndex, out var byCode))
                counts[d.RoomIndex] = byCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            byCode.TryGetValue(d.Code!.Code, out var n);
            byCode[d.Code.Code] = n + 1;
        }

        var (findings, checkedRooms) = RoomAuditMath.EvaluateRules(audit.Index.Candidates, counts, rules);

        var tolerance = ToolArguments.GetDouble(args, "heightToleranceMm", 50);
        var heightIssues = new List<object>();
        foreach (var d in audit.Devices.Where(d => d.RoomIndex >= 0 && d.Code != null))
        {
            var (deviations, _, _) = MountChecks.Check(audit, d, tolerance, includeWallOffset: false);
            if (deviations.Count == 0) continue;
            heightIssues.Add(new
            {
                elementId = d.Id,
                deviceCode = d.Code!.Code,
                roomNumber = audit.Index.Rooms[d.RoomIndex].Number,
                point = d.PointPayload(),
                deviations = deviations.Select(MountChecks.DeviationPayload).ToList()
            });
        }

        var outside = audit.Devices.Where(d => d.RoomIndex < 0).Select(d => new
        {
            elementId = d.Id,
            deviceCode = d.Code?.Code,
            point = d.PointPayload()
        }).ToList();

        sw.Stop();
        return Task.FromResult(RoomToolSupport.Ok(request, sw,
            $"{findings.Count} rule violation(s), {outside.Count} device(s) outside rooms, {heightIssues.Count} height issue(s) " +
            $"— {audit.Devices.Count} device(s) in {audit.Index.Rooms.Count} room(s).",
            new
            {
                source = audit.Index.Service.Source.Label,
                devicesChecked = audit.Devices.Count,
                rules = rules.Select((r, i) => new
                {
                    index = i,
                    rule = r.Describe(),
                    roomsChecked = checkedRooms[i],
                    violations = findings.Count(f => f.RuleIndex == i)
                }).ToList(),
                findings = findings.Select(f => new
                {
                    rule = f.RuleIndex,
                    roomNumber = f.RoomNumber,
                    roomName = f.RoomName,
                    code = f.Code,
                    count = f.Count,
                    min = f.Min,
                    max = f.Max,
                    kind = f.Kind
                }).ToList(),
                devicesOutsideRooms = outside,
                heightDeviations = heightIssues
            },
            warnings));
    }
}

// ── 4.3 Device alignment (wall, ceiling, floor) ─────────────────────────────────

public sealed class CheckDeviceAlignmentTool : IRevitMcpTool
{
    public string Name => "revit_check_device_alignment";
    public string Description =>
        "Checks that unhosted devices still sit where their mount says — needed after an AR model update, because they do " +
        "not move with the linked walls/ceilings. wall: signed distance from the nearest room wall face vs offsetFromWallMm " +
        "(negative = inside the wall) and height above level vs heightMm; ceiling: gap below the linked ceiling vs " +
        "offsetFromCeilingMm; floor: height above level vs heightMm. Devices outside every room are listed too. " +
        "toleranceMm (default 20). Returns suggestedMoves ready for revit_preview_move_elements / revit_move_elements " +
        "(targets + expected current coordinates); revit_align_elements is the alternative for snapping to surfaces. " +
        "Rooms from source/linkInstanceId (levelName); devices from the device code map (codes[] limits the codes).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(RoomToolSupport.Fail(request, "No active document."));
        var args = request.Arguments;
        var warnings = new List<string>();

        var audit = AuditContext.Load(doc, args, warnings, out var error);
        if (audit == null) return Task.FromResult(RoomToolSupport.Fail(request, error!, warnings));

        var tolerance = ToolArguments.GetDouble(args, "toleranceMm", 20);
        var issues = new List<object>();
        var moves = new List<object>();
        var notes = new Dictionary<string, int>();
        int ok = 0;

        foreach (var d in audit.Devices.Where(d => d.Code != null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (d.RoomIndex < 0)
            {
                issues.Add(new
                {
                    elementId = d.Id,
                    deviceCode = d.Code!.Code,
                    mount = d.Code.Mount,
                    roomNumber = (string?)null,
                    point = d.PointPayload(),
                    problem = d.Code.IsWall
                        ? "Not inside any room — possibly inside a wall that moved."
                        : "Not inside any room.",
                    deviations = new List<object>()
                });
                continue;
            }

            var (deviations, target, note) = MountChecks.Check(audit, d, tolerance);
            if (note != null)
            {
                notes.TryGetValue(note, out var n);
                notes[note] = n + 1;
            }
            if (deviations.Count == 0)
            {
                ok++;
                continue;
            }

            var room = audit.Index.Rooms[d.RoomIndex];
            issues.Add(new
            {
                elementId = d.Id,
                deviceCode = d.Code!.Code,
                mount = d.Code.Mount,
                roomNumber = room.Number,
                point = d.PointPayload(),
                problem = deviations.Any(x => x.Axis == "wallOffset" && x.ActualMm < 0) ? "Inside or behind the wall." : "Off its mount.",
                deviations = deviations.Select(MountChecks.DeviationPayload).ToList<object>()
            });

            if (target is { } t)
            {
                moves.Add(new
                {
                    elementId = d.Id,
                    targetXmm = Math.Round(t.Point.X, 1),
                    targetYmm = Math.Round(t.Point.Y, 1),
                    targetZmm = Math.Round(t.Z, 1),
                    expectedXmm = Math.Round(d.Point.X, 1),
                    expectedYmm = Math.Round(d.Point.Y, 1),
                    expectedZmm = Math.Round(d.ZMm, 1)
                });
            }
        }

        warnings.AddRange(notes.Select(n => $"{n.Key} ({n.Value} device(s))"));
        sw.Stop();
        return Task.FromResult(RoomToolSupport.Ok(request, sw,
            $"{issues.Count} device(s) off their mount, {ok} aligned (tolerance {tolerance:0.#} mm).",
            new
            {
                source = audit.Index.Service.Source.Label,
                devicesChecked = audit.Devices.Count(d => d.Code != null),
                aligned = ok,
                issues,
                suggestedMoves = moves
            },
            warnings));
    }
}

// ── 4.4 Coverage ────────────────────────────────────────────────────────────────

public sealed class CheckCoverageTool : IRevitMcpTool
{
    public string Name => "revit_check_coverage";
    public string Description =>
        "Checks per-room coverage of devices on a sample grid (stepMm, default 500). codes[] (required): smoke detectors " +
        "and APs use a radius (radiusMm, else the code's coverageRadiusMm, else the fire rule radius of its " +
        "detectorType — smoke/CO/ASD 6.2 m, heat 4.5 m, × the ceilingSlopeDeg factor — else 7500); cameras " +
        "(codes with fovDeg) use a view sector of fovDeg and rangeM around their facing direction. scope: room (default " +
        "for detectors/cameras — only devices in the same room count) | level (default for codes with coverageRadiusMm and " +
        "no fovDeg, e.g. APs — every device on the storey counts). Returns covered %, uncovered m² and uncovered regions " +
        "(area + centre point) per room. Rooms: roomNumbers / roomFilter / levelName, source/linkInstanceId.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(RoomToolSupport.Fail(request, "No active document."));
        var args = request.Arguments;
        var warnings = new List<string>();

        if (DeviceAudit.Codes(args).Length == 0)
            return Task.FromResult(RoomToolSupport.Fail(request, "Provide codes[] (or deviceCode) — the device codes to check coverage for."));

        var audit = AuditContext.Load(doc, args, warnings, out var error);
        if (audit == null) return Task.FromResult(RoomToolSupport.Fail(request, error!, warnings));

        var step = Math.Max(100, ToolArguments.GetDouble(args, "stepMm", 500));
        var radiusArg = DeviceAdjustSupport.Has(args, "radiusMm") ? ToolArguments.GetDouble(args, "radiusMm") : (double?)null;
        var scopeArg = ToolArguments.GetString(args, "scope").Trim().ToLowerInvariant();
        var maxRegions = Math.Max(1, ToolArguments.GetInt(args, "maxRegionsPerRoom", 5));
        var slope = ToolArguments.GetDouble(args, "ceilingSlopeDeg", 0);

        var coverage = audit.Devices.Where(d => d.Code != null).Select(d => (Device: d, Coverage: new CoverageDevice
        {
            Id = d.Id,
            Position = d.Point,
            Facing = d.Facing,
            FovDeg = d.Code!.FovDeg,
            RadiusMm = d.Code.FovDeg != null
                ? (d.Code.RangeM ?? 15) * 1000
                : radiusArg ?? d.Code.CoverageRadiusMm
                  ?? (d.Code.IsFireDetector ? FireAlarmRules.Spacing(d.Code.DetectorType!, slope)?.RadiusMm : null)
                  ?? 7500
        })).ToList();

        var cameraWithoutRange = coverage.Where(c => c.Device.Code!.FovDeg != null && c.Device.Code.RangeM == null).Select(c => c.Device.Code!.Code).Distinct().ToList();
        if (cameraWithoutRange.Count > 0)
            warnings.Add($"Codes {string.Join(", ", cameraWithoutRange)} have fovDeg but no rangeM; 15 m is assumed.");

        var rooms = new List<object>();
        double totalUncovered = 0;
        var uncoveredRooms = 0;
        foreach (var i in audit.SelectedRooms(args))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var room = audit.Index.Rooms[i];
            var relevant = coverage.Where(c =>
            {
                var scope = scopeArg.Length > 0 ? scopeArg
                    : c.Device.Code!.FovDeg == null && c.Device.Code.CoverageRadiusMm != null ? "level" : "room";
                if (scope == "room") return c.Device.RoomIndex == i;
                return Math.Abs(c.Device.LevelElevationMm - room.HostLevelElevationMm) < 1 &&
                       c.Device.Point.DistanceTo(room.InteriorPoint) < c.Coverage.RadiusMm + Math.Max(room.Bounds.Width, room.Bounds.Height);
            }).Select(c => c.Coverage).ToList();

            var result = RoomAuditMath.Coverage(room.Polygon, relevant, step);
            totalUncovered += result.UncoveredAreaM2;
            if (result.Uncovered > 0) uncoveredRooms++;
            rooms.Add(new
            {
                roomNumber = room.Number,
                roomName = room.Name,
                level = room.HostLevelName,
                areaM2 = room.AreaM2,
                devices = relevant.Count,
                coveredPercent = Math.Round(result.CoveredFraction * 100, 1),
                uncoveredAreaM2 = result.UncoveredAreaM2,
                uncoveredRegions = result.Regions.Take(maxRegions).Select(r => new
                {
                    areaM2 = r.AreaM2,
                    center = RoomAnalysis.Point(r.Center)
                }).ToList()
            });
        }

        sw.Stop();
        return Task.FromResult(RoomToolSupport.Ok(request, sw,
            $"{uncoveredRooms} of {rooms.Count} room(s) not fully covered; {Math.Round(totalUncovered, 1)} m² uncovered in total.",
            new
            {
                source = audit.Index.Service.Source.Label,
                stepMm = step,
                rooms
            },
            warnings));
    }
}
