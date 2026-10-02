using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Tools;

namespace RevitMCP.Addin.RoomDevices;

/// <summary>One device the placement tools will create.</summary>
internal sealed class PlannedDevice
{
    public int Index { get; set; }
    public DeviceCode Code { get; set; } = null!;
    public FamilySymbol? Symbol { get; set; }
    public RoomRecord? Room { get; set; }
    public string Strategy { get; set; } = string.Empty;
    public P2 Point { get; set; }
    /// <summary>Absolute insertion height, host internal coordinates, mm.</summary>
    public double ZMm { get; set; }
    /// <summary>Plan direction the device front should face; null keeps the family's own orientation.</summary>
    public P2? Facing { get; set; }
    public int? WallIndex { get; set; }
    public double? AlongMm { get; set; }
    public long? DoorId { get; set; }
    public List<string> Warnings { get; } = [];
    public string? Blocked { get; set; }
    public bool CanPlace => Blocked == null && Symbol != null && Room?.HostLevel != null;

    public double ElevationFromLevelMm => ZMm - (Room?.HostLevelElevationMm ?? 0);

    public object ToPayload(long? createdId = null, string? error = null) => new
    {
        index = Index,
        deviceCode = Code.Code,
        strategy = Strategy,
        roomNumber = Room?.Number,
        roomName = Room?.Name,
        level = Room?.HostLevelName,
        typeId = Symbol?.Id.Value,
        point = RoomAnalysis.Point(Point),
        zMm = Math.Round(ZMm, 1),
        elevationFromLevelMm = Math.Round(ElevationFromLevelMm, 1),
        facingDeg = Facing is { } f ? Math.Round(RoomGeometryMath.NormalizeDeg(RoomGeometryMath.AngleDeg(f) + Code.RotationOffsetDeg), 1) : (double?)null,
        wallIndex = WallIndex,
        alongMm = AlongMm is { } a ? Math.Round(a, 0) : (double?)null,
        doorId = DoorId,
        status = Blocked != null ? "blocked" : error != null ? "failed" : Warnings.Count > 0 ? "warning" : "ok",
        reason = Blocked ?? error,
        warnings = Warnings,
        elementId = createdId
    };
}

internal sealed class PlacementPlan
{
    public List<PlannedDevice> Devices { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<object> RoomNotes { get; } = [];
    public DeviceCodeContext Codes { get; set; } = null!;
    public RoomSourceService Rooms { get; set; } = null!;
}

/// <summary>
/// Plans and executes unhosted device placement against linked (or host) rooms. Preview and apply
/// tools both call the Plan* methods, so a preview shows exactly what apply will create.
/// </summary>
internal static class DevicePlacementService
{
    public const double DuplicateDistanceMm = 200;

    // ── Shared setup ──────────────────────────────────────────────────────────

    private static PlacementPlan? Setup(Document doc, Dictionary<string, object?> args, out string? error)
    {
        _avoidCache = null;
        var codes = DeviceCodeContext.Load(doc, args, out error);
        if (codes == null) return null;
        var rooms = RoomSourceService.FromArguments(doc, args, out error);
        if (rooms == null) return null;
        var plan = new PlacementPlan { Codes = codes, Rooms = rooms };
        plan.Warnings.AddRange(codes.ParseErrors.Select(e => $"Device code map: {e}"));
        return plan;
    }

    private sealed class RoomCache
    {
        private readonly RoomSourceService _service;
        private readonly Dictionary<string, RoomRecord?> _byNumber = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<RoomRecord, List<RoomOpening>> _openings = [];
        private readonly Dictionary<RoomRecord, double?> _ceilings = [];
        private readonly DevicePlacementSettings _settings;

        public RoomCache(RoomSourceService service, DevicePlacementSettings settings)
        {
            _service = service;
            _settings = settings;
        }

        public RoomRecord? Get(string number, string levelName, List<string> warnings)
        {
            var key = number + "|" + levelName;
            if (_byNumber.TryGetValue(key, out var cached)) return cached;
            var found = _service.LoadRooms(new RoomFilter { RoomNumbers = [number], LevelName = levelName }, warnings);
            if (found.Count > 1)
                warnings.Add($"Room number '{number}' matches {found.Count} rooms; the first ({found[0].HostLevelName}) is used — pass levelName.");
            return _byNumber[key] = found.FirstOrDefault();
        }

        public List<RoomOpening> Openings(RoomRecord room) =>
            _openings.TryGetValue(room, out var o) ? o : _openings[room] = RoomAnalysis.Openings(_service, room, _settings);

        public double? Ceiling(RoomRecord room) =>
            _ceilings.TryGetValue(room, out var c) ? c : _ceilings[room] = _service.CeilingHeightMm(room);
    }

    // ── place_at_wall ─────────────────────────────────────────────────────────

    public static PlacementPlan? PlanAtWall(Document doc, Dictionary<string, object?> args, out string? error)
    {
        var plan = Setup(doc, args, out error);
        if (plan == null) return null;

        var items = ParseArray(args, "placements");
        if (items.Count == 0)
        {
            error = "Provide placements=[{deviceCode, roomNumber, wallIndex | wallId | nearestToPoint{x,y}, alongMm | alongFraction | nearDoorId}].";
            return null;
        }

        var cache = new RoomCache(plan.Rooms, plan.Codes.Settings);
        var levelName = ToolArguments.GetString(args, "levelName").Trim();

        foreach (var item in items)
        {
            var codeName = ToolArguments.GetString(item, "deviceCode").Trim();
            var roomNumber = ToolArguments.GetString(item, "roomNumber").Trim();
            var code = plan.Codes.Get(codeName, out var codeError);
            if (code == null)
            {
                plan.Warnings.Add($"placement {plan.Devices.Count}: {codeError}");
                continue;
            }

            var room = cache.Get(roomNumber, ToolArguments.GetString(item, "levelName", levelName).Trim(), plan.Warnings);
            var template = NewDevice(plan, doc, code, room, "wall");
            if (room == null)
            {
                template.Blocked = $"Room '{roomNumber}' was not found in the {plan.Rooms.Source.Label}.";
                plan.Devices.Add(template);
                continue;
            }

            if (!code.IsWall)
                template.Warnings.Add($"Code '{code.Code}' is {code.Mount}-mounted; place_at_wall uses its heightMm or the given heightMm.");

            var openings = cache.Openings(room);
            var face = PickFace(room, openings, item, out var faceError);
            if (face == null)
            {
                template.Blocked = faceError;
                plan.Devices.Add(template);
                continue;
            }

            var along = PickAlong(face, openings, item, code, out var doorId, out var alongError);
            if (along == null)
            {
                template.Blocked = alongError;
                template.WallIndex = face.Index;
                plan.Devices.Add(template);
                continue;
            }

            var height = ToolArguments.GetDouble(item, "heightMm", code.HeightMm ?? double.NaN);
            if (double.IsNaN(height))
            {
                template.Blocked = $"Code '{code.Code}' has no heightMm — pass heightMm.";
                plan.Devices.Add(template);
                continue;
            }

            var count = Math.Max(1, ToolArguments.GetInt(item, "count", 1));
            var spacing = ToolArguments.GetDouble(item, "spacingMm", 0);
            if (count > 1 && spacing <= 0)
                plan.Warnings.Add($"count={count} without spacingMm — all copies would overlap; spacingMm is required.");

            for (int k = 0; k < count; k++)
            {
                var device = k == 0 ? template : NewDevice(plan, doc, code, room, "wall");
                device.WallIndex = face.Index;
                device.AlongMm = along.Value + k * spacing;
                device.DoorId = doorId;
                device.Point = face.PointAt(device.AlongMm.Value, code.OffsetFromWallMm);
                device.ZMm = room.HostLevelElevationMm + height;
                device.Facing = face.NormalIntoRoom;
                CheckWall(device, room, face, openings, height);
                plan.Devices.Add(device);
            }
        }

        FinishChecks(doc, plan);
        return plan;
    }

    private static WallFace? PickFace(RoomRecord room, List<RoomOpening> openings, Dictionary<string, object?> item, out string? error)
    {
        error = null;
        var faces = room.Faces;

        if (item.ContainsKey("wallIndex") && item["wallIndex"] != null)
        {
            var index = ToolArguments.GetInt(item, "wallIndex", -1);
            if (index >= 0 && index < faces.Count) return faces[index];
            error = $"wallIndex {index} is out of range — room {room.Number} has {faces.Count} faces (see revit_get_room_walls).";
            return null;
        }

        var wallId = ToolArguments.GetLong(item, "wallId");
        if (wallId != 0)
        {
            var match = faces.Where(f => room.RawSegmentFor(f)?.ElementId == wallId).OrderByDescending(f => f.LengthMm).FirstOrDefault();
            if (match != null) return match;
            error = $"Wall {wallId} does not bound room {room.Number}. Use wallIndex from revit_get_room_walls.";
            return null;
        }

        var near = ParsePoint(item, "nearestToPoint");
        if (near != null) return RoomGeometryMath.NearestFace(faces, near.Value);

        var doorId = ToolArguments.GetLong(item, "nearDoorId");
        if (doorId != 0)
        {
            var door = openings.FirstOrDefault(o => o.Opening.Id == doorId);
            if (door?.Face != null) return door.Face;
            error = $"Door {doorId} is not in room {room.Number}.";
            return null;
        }

        error = "Choose the wall: wallIndex, wallId, nearestToPoint {x, y} or nearDoorId.";
        return null;
    }

    private static double? PickAlong(WallFace face, List<RoomOpening> openings, Dictionary<string, object?> item, DeviceCode code, out long? doorId, out string? error)
    {
        error = null;
        doorId = null;

        var nearDoor = ToolArguments.GetLong(item, "nearDoorId");
        if (nearDoor != 0)
        {
            var door = openings.FirstOrDefault(o => o.Opening.Id == nearDoor);
            if (door?.OnFace == null || door.Face != face)
            {
                error = $"Door {nearDoor} is not on wall face {face.Index}.";
                return null;
            }
            doorId = nearDoor;
            var side = ToolArguments.GetString(item, "doorSide", code.DoorSide);
            var offset = ToolArguments.GetDouble(item, "offsetMm", code.DoorOffsetMm);
            return RoomGeometryMath.AlongBesideDoor(door.OnFace, door.LockSign == 0 ? 1 : door.LockSign, side, offset);
        }

        if (item.TryGetValue("alongMm", out var a) && a != null)
            return ToolArguments.GetDouble(item, "alongMm");
        if (item.TryGetValue("alongFraction", out var f) && f != null)
            return face.LengthMm * Math.Max(0, Math.Min(1, ToolArguments.GetDouble(item, "alongFraction")));

        error = "Give the position on the wall: alongMm, alongFraction (0…1) or nearDoorId.";
        return null;
    }

    private static void CheckWall(PlannedDevice device, RoomRecord room, WallFace face, List<RoomOpening> openings, double heightMm)
    {
        var onFace = openings.Where(o => o.Face == face && o.OnFace != null).ToList();
        var swings = onFace
            .Where(o => o.Opening.Kind == "door" && o.SwingIntoRoom != false && o.LockSign != 0)
            .Select(o => RoomGeometryMath.SwingStretch(o.OnFace!, o.LockSign, o.Opening.HeightMm ?? 2100))
            .ToList();

        // A door's own lock/hinge-side device is next to the door on purpose; only flag other doors' leaves.
        if (device.DoorId != null) swings = swings.Where(s => s.DoorId != device.DoorId).ToList();

        device.Warnings.AddRange(RoomGeometryMath.CheckWallPosition(
            face, device.AlongMm ?? 0, onFace.Select(o => o.OnFace!).ToList(), heightMm, swings));

        if (!RoomGeometryMath.Contains(room.Polygon, device.Point) && device.Code.OffsetFromWallMm > 0)
            device.Warnings.Add("The point is not inside the room.");
        if (swings.Count == 0 && onFace.Any(o => o.Opening.Kind == "door" && o.SwingIntoRoom == null))
            device.Warnings.Add("Swing direction of a door on this wall is unknown (IFC door); check the leaf clearance.");
    }

    // ── place_in_room ─────────────────────────────────────────────────────────

    public static PlacementPlan? PlanInRoom(Document doc, Dictionary<string, object?> args, out string? error)
    {
        var plan = Setup(doc, args, out error);
        if (plan == null) return null;

        var code = plan.Codes.Get(ToolArguments.GetString(args, "deviceCode"), out error);
        if (code == null) return null;

        var strategy = ToolArguments.GetString(args, "strategy", "center").Trim().ToLowerInvariant();
        if (strategy is not ("center" or "grid" or "neardoor" or "points"))
        {
            error = "strategy must be center | grid | nearDoor | points.";
            return null;
        }

        var filter = RoomSourceService.ParseFilter(args);
        if (filter.RoomNumbers.Length == 0 && filter.NameFilter.Length == 0)
        {
            error = "Choose rooms with roomNumbers or roomFilter (name or regex).";
            return null;
        }

        var rooms = plan.Rooms.LoadRooms(filter, plan.Warnings);
        if (rooms.Count == 0)
        {
            error = $"No rooms matched in the {plan.Rooms.Source.Label}.";
            return null;
        }
        foreach (var missing in filter.RoomNumbers.Where(n => !rooms.Any(r => string.Equals(r.Number, n, StringComparison.OrdinalIgnoreCase))))
            plan.Warnings.Add($"Room '{missing}' was not found.");

        var cache = new RoomCache(plan.Rooms, plan.Codes.Settings);
        if ((strategy is "center" or "grid") && code.IsWall)
            plan.Warnings.Add($"Code '{code.Code}' is wall-mounted; strategy={strategy} places it free in the room — use revit_place_at_wall for wall positions.");

        foreach (var room in rooms)
        {
            switch (strategy)
            {
                case "center":
                    AddFree(plan, doc, code, room, cache, args, "center", room.InteriorPoint);
                    break;

                case "grid":
                {
                    var spacing = ToolArguments.GetDouble(args, "maxSpacingMm", code.MaxSpacingMm ?? 0);
                    if (spacing <= 0)
                    {
                        error = $"strategy=grid needs maxSpacingMm (argument or code '{code.Code}').";
                        return null;
                    }
                    var maxWall = ToolArguments.GetDouble(args, "maxDistFromWallMm", code.MaxDistFromWallMm ?? 0);
                    double? radius = args.TryGetValue("coverageRadiusMm", out var r) && r != null
                        ? ToolArguments.GetDouble(args, "coverageRadiusMm")
                        : null;
                    var grid = RoomGeometryMath.Grid(room.Polygon, spacing, maxWall, coverageRadiusMm: radius);
                    foreach (var p in grid.Points) AddFree(plan, doc, code, room, cache, args, "grid", p);
                    plan.RoomNotes.Add(new
                    {
                        roomNumber = room.Number,
                        points = grid.Points.Count,
                        stepXMm = Math.Round(grid.StepXMm, 0),
                        stepYMm = Math.Round(grid.StepYMm, 0),
                        coverageRadiusMm = Math.Round(grid.CoverageRadiusMm, 0),
                        uncoveredAreaM2 = grid.UncoveredAreaM2
                    });
                    if (grid.SamplesUncovered > 0)
                        plan.Warnings.Add($"Room {room.Number}: about {grid.UncoveredAreaM2} m² is farther than {grid.CoverageRadiusMm:0} mm from every point.");
                    break;
                }

                case "neardoor":
                    AddNearDoors(plan, doc, code, room, cache, args);
                    break;

                case "points":
                {
                    var points = ParseArray(args, "points");
                    if (points.Count == 0)
                    {
                        error = "strategy=points needs points=[{u, v}] (0…1 in the room's bounding box).";
                        return null;
                    }
                    foreach (var p in points)
                    {
                        var u = ToolArguments.GetDouble(p, "u", double.NaN);
                        var v = ToolArguments.GetDouble(p, "v", double.NaN);
                        if (double.IsNaN(u) || double.IsNaN(v))
                        {
                            plan.Warnings.Add("A points entry lacks u or v — skipped.");
                            continue;
                        }
                        AddFree(plan, doc, code, room, cache, args, "points", RoomGeometryMath.FromRelative(room.Bounds, u, v));
                    }
                    break;
                }
            }
        }

        FinishChecks(doc, plan);
        return plan;
    }

    private static void AddFree(PlacementPlan plan, Document doc, DeviceCode code, RoomRecord room, RoomCache cache,
        Dictionary<string, object?> args, string strategy, P2 point)
    {
        var device = NewDevice(plan, doc, code, room, strategy);
        device.Point = point;
        if (!RoomGeometryMath.Contains(room.Polygon, point))
            device.Warnings.Add("The point is outside the room.");
        SetHeight(device, room, cache, args);
        CheckAvoid(plan, device, room);
        plan.Devices.Add(device);
    }

    private static void AddNearDoors(PlacementPlan plan, Document doc, DeviceCode code, RoomRecord room, RoomCache cache, Dictionary<string, object?> args)
    {
        var side = ToolArguments.GetString(args, "side", "inside").Trim().ToLowerInvariant();
        var doorSide = ToolArguments.GetString(args, "doorSide", code.DoorSide);
        var offset = ToolArguments.GetDouble(args, "offsetMm", code.DoorOffsetMm);
        var doors = cache.Openings(room).Where(o => o.Opening.Kind == "door" && o.Face != null && o.OnFace != null).ToList();
        if (doors.Count == 0)
        {
            plan.Warnings.Add($"Room {room.Number} has no doors.");
            return;
        }

        foreach (var door in doors)
        {
            var face = door.Face!;
            var device = NewDevice(plan, doc, code, room, "nearDoor");
            device.DoorId = door.Opening.Id;
            device.WallIndex = face.Index;
            device.AlongMm = RoomGeometryMath.AlongBesideDoor(door.OnFace!, door.LockSign == 0 ? 1 : door.LockSign, doorSide, offset);
            if (door.LockSource == "cornerHeuristic")
                device.Warnings.Add("Lock side guessed from the nearer corner (no door hand data).");

            if (side == "outside")
            {
                var thickness = RoomAnalysis.DescribeBoundingElement(plan.Rooms, room, face, room.RawSegmentFor(face)).ThicknessMm;
                if (thickness == null) device.Warnings.Add("Wall thickness unknown — 200 mm assumed for the outside face.");
                device.Point = face.PointAt(device.AlongMm.Value, -((thickness ?? 200) + code.OffsetFromWallMm));
                device.Facing = face.NormalIntoRoom.Scaled(-1);
            }
            else
            {
                var inset = code.IsWall ? code.OffsetFromWallMm : Math.Max(500, code.OffsetFromWallMm);
                device.Point = face.PointAt(device.AlongMm.Value, inset);
                device.Facing = code.IsWall ? face.NormalIntoRoom : null;
            }

            SetHeight(device, room, cache, args);
            if (code.IsWall && side != "outside")
                CheckWall(device, room, face, cache.Openings(room), device.ElevationFromLevelMm);
            CheckAvoid(plan, device, room);
            plan.Devices.Add(device);
        }
    }

    private static void SetHeight(PlannedDevice device, RoomRecord room, RoomCache cache, Dictionary<string, object?> args)
    {
        var code = device.Code;
        if (code.IsCeiling)
        {
            var ceiling = cache.Ceiling(room);
            if (ceiling == null)
            {
                if (room.HeightMm is { } h)
                {
                    ceiling = h;
                    device.Warnings.Add($"No ceiling found above the room; the room height {h:0} mm is used.");
                }
                else
                {
                    device.Blocked = "No ceiling and no room height — pass heightMm.";
                    return;
                }
            }
            var fromFloor = ToolArguments.GetDouble(args, "heightMm", ceiling.Value - code.OffsetFromCeilingMm);
            device.ZMm = room.FloorZMm + fromFloor;
            return;
        }

        var height = ToolArguments.GetDouble(args, "heightMm", code.HeightMm ?? 0);
        device.ZMm = room.HostLevelElevationMm + height;
    }

    private static void CheckAvoid(PlacementPlan plan, PlannedDevice device, RoomRecord room)
    {
        if (device.Code.AvoidCategories.Count == 0) return;
        var key = "avoid|" + room.SourceId + "|" + string.Join(",", device.Code.AvoidCategories);
        if (!AvoidCache.TryGetValue(key, out var elements))
            AvoidCache[key] = elements = plan.Rooms.ElementsInRoom(room, device.Code.AvoidCategories, plan.Warnings)
                .Select(e => (e.Element.Id.Value, e.Category, e.Plan)).ToList();

        foreach (var (id, category, bounds) in elements)
        {
            if (bounds.Contains(device.Point, device.Code.ClearanceMm))
                device.Warnings.Add($"Within {device.Code.ClearanceMm:0} mm of {category} {id}.");
        }
    }

    [ThreadStatic] private static Dictionary<string, List<(long, string, Bounds2)>>? _avoidCache;
    private static Dictionary<string, List<(long, string, Bounds2)>> AvoidCache => _avoidCache ??= [];

    // ── Shared checks ─────────────────────────────────────────────────────────

    private static PlannedDevice NewDevice(PlacementPlan plan, Document doc, DeviceCode code, RoomRecord? room, string strategy)
    {
        var device = new PlannedDevice
        {
            Index = plan.Devices.Count,
            Code = code,
            Room = room,
            Strategy = strategy,
            Symbol = DeviceCodeContext.FindSymbol(doc, code.Family, code.Type)
        };
        if (device.Symbol == null)
            device.Blocked = $"Family type '{code.Family} : {code.Type}' is not loaded — run revit_ensure_device_types.";
        else if (room != null && room.HostLevel == null)
            device.Blocked = "No host level found for the room.";
        return device;
    }

    /// <summary>Duplicate check against existing host instances of the same type and earlier planned devices.</summary>
    private static void FinishChecks(Document doc, PlacementPlan plan)
    {
        _avoidCache = null;
        var existing = new Dictionary<long, List<(P2 P, double Z)>>();
        var planned = new List<PlannedDevice>();

        foreach (var device in plan.Devices.Where(d => d.Blocked == null && d.Symbol != null))
        {
            var typeId = device.Symbol!.Id.Value;
            if (!existing.TryGetValue(typeId, out var points))
            {
                points = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .Where(i => i.Symbol?.Id.Value == typeId && i.Location is LocationPoint)
                    .Select(i => ((LocationPoint)i.Location).Point)
                    .Select(p => (new P2(RoomUnits.FtToMm(p.X), RoomUnits.FtToMm(p.Y)), RoomUnits.FtToMm(p.Z)))
                    .ToList();
                existing[typeId] = points;
            }

            if (points.Any(e => e.P.DistanceTo(device.Point) < DuplicateDistanceMm && Math.Abs(e.Z - device.ZMm) < 300))
                device.Warnings.Add($"An existing '{device.Code.Code}' device is within {DuplicateDistanceMm:0} mm.");
            if (planned.Any(o => o.Symbol!.Id == device.Symbol.Id && o.Point.DistanceTo(device.Point) < DuplicateDistanceMm && Math.Abs(o.ZMm - device.ZMm) < 300))
                device.Warnings.Add($"Another planned '{device.Code.Code}' device is within {DuplicateDistanceMm:0} mm.");
            planned.Add(device);
        }
    }

    // ── Apply ─────────────────────────────────────────────────────────────────

    /// <summary>Creates one planned device. Must run inside an open transaction.</summary>
    public static FamilyInstance Create(Document doc, PlannedDevice device, DevicePlacementSettings settings, List<string> warnings)
    {
        var symbol = device.Symbol!;
        var level = device.Room!.HostLevel!;
        if (!symbol.IsActive) symbol.Activate();

        var point = new XYZ(RoomUnits.MmToFt(device.Point.X), RoomUnits.MmToFt(device.Point.Y), RoomUnits.MmToFt(device.ZMm));
        FamilyInstance instance;
        try
        {
            instance = doc.Create.NewFamilyInstance(point, symbol, level, StructuralType.NonStructural);
        }
        catch (Exception ex) when (symbol.Family?.FamilyPlacementType == FamilyPlacementType.WorkPlaneBased)
        {
            warnings.Add($"Level placement failed for a work-plane family ({ex.Message}); retried without a level.");
            instance = doc.Create.NewFamilyInstance(point, symbol, StructuralType.NonStructural);
        }

        doc.Regenerate();
        FixElevation(doc, instance, level, device.ZMm, warnings);
        if (device.Facing is { } facing)
            Rotate(doc, instance, facing, device.Code.RotationOffsetDeg);

        if (settings.RoomParameter.Length > 0 && device.Room != null)
        {
            var p = instance.LookupParameter(settings.RoomParameter);
            if (p == null || p.IsReadOnly || p.StorageType != StorageType.String)
                warnings.Add($"Device {instance.Id.Value}: parameter '{settings.RoomParameter}' is missing, read-only or not text — room number not written.");
            else
                p.Set(device.Room.Number);
        }

        return instance;
    }

    private static void FixElevation(Document doc, FamilyInstance instance, Level level, double targetZMm, List<string> warnings)
    {
        if (instance.Location is not LocationPoint lp) return;
        var currentMm = RoomUnits.FtToMm(lp.Point.Z);
        if (Math.Abs(currentMm - targetZMm) < 0.5) return;

        var offsetFt = RoomUnits.MmToFt(targetZMm) - level.ProjectElevation;
        foreach (var bip in new[] { BuiltInParameter.INSTANCE_ELEVATION_PARAM, BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM })
        {
            var p = instance.get_Parameter(bip);
            if (p == null || p.IsReadOnly) continue;
            try
            {
                p.Set(offsetFt);
                doc.Regenerate();
                if (instance.Location is LocationPoint after && Math.Abs(RoomUnits.FtToMm(after.Point.Z) - targetZMm) < 0.5)
                    return;
            }
            catch { }
        }

        if (instance.Location is LocationPoint again)
        {
            var dz = RoomUnits.MmToFt(targetZMm) - again.Point.Z;
            try
            {
                ElementTransformUtils.MoveElement(doc, instance.Id, new XYZ(0, 0, dz));
            }
            catch (Exception ex)
            {
                warnings.Add($"Device {instance.Id.Value}: could not set the height ({ex.Message}).");
            }
        }
    }

    private static void Rotate(Document doc, FamilyInstance instance, P2 facing, double offsetDeg)
    {
        if (instance.Location is not LocationPoint lp) return;
        var current = instance.FacingOrientation;
        if (current == null || (Math.Abs(current.X) < 1e-9 && Math.Abs(current.Y) < 1e-9)) return;

        var targetDeg = RoomGeometryMath.AngleDeg(facing) + offsetDeg;
        var currentDeg = RoomGeometryMath.AngleDeg(new P2(current.X, current.Y));
        var delta = RoomGeometryMath.NormalizeDeg(targetDeg - currentDeg);
        if (delta > 180) delta -= 360;
        if (Math.Abs(delta) < 0.01) return;

        var axis = Line.CreateBound(lp.Point, lp.Point + XYZ.BasisZ);
        ElementTransformUtils.RotateElement(doc, instance.Id, axis, delta * Math.PI / 180.0);
    }

    // ── Argument parsing ──────────────────────────────────────────────────────

    public static List<Dictionary<string, object?>> ParseArray(Dictionary<string, object?> args, string key)
    {
        var result = new List<Dictionary<string, object?>>();
        if (!args.TryGetValue(key, out var raw) || raw == null) return result;

        JArray? array;
        try
        {
            array = raw switch
            {
                JArray a => a,
                string s => ToolArguments.TryParseJArray(s),
                _ => JArray.FromObject(raw)
            };
        }
        catch { return result; }

        foreach (var token in array ?? [])
        {
            // JSON nulls are dropped so "wallIndex": null reads as "not given" instead of failing a numeric read.
            if (token is JObject obj)
                result.Add(obj.Properties()
                    .Where(p => p.Value.Type != JTokenType.Null)
                    .ToDictionary(p => p.Name, p => (object?)p.Value, StringComparer.OrdinalIgnoreCase));
        }
        return result;
    }

    private static P2? ParsePoint(Dictionary<string, object?> item, string key)
    {
        if (!item.TryGetValue(key, out var raw) || raw is not JObject obj) return null;
        var x = obj["x"]?.Value<double?>();
        var y = obj["y"]?.Value<double?>();
        return x != null && y != null ? new P2(x.Value, y.Value) : null;
    }
}
