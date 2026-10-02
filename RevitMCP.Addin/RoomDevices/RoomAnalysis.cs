using System.IO;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using RevitMCP.Addin.Configuration;

namespace RevitMCP.Addin.RoomDevices;

/// <summary>
/// The "devicePlacement" section of the project config. The door conventions exist because Revit
/// does not document which way FamilyInstance.HandOrientation points; verify once with
/// revit_export_view_image and flip the setting if lock sides come out mirrored.
/// </summary>
internal sealed class DevicePlacementSettings
{
    /// <summary>Instance parameter that receives the room number on placement (empty = none).</summary>
    public string RoomParameter { get; set; } = string.Empty;
    /// <summary>True: HandOrientation points from the hinge to the latch side.</summary>
    public bool HandPointsToLatch { get; set; } = true;
    /// <summary>True: a door leaf swings to the side FacingOrientation points to.</summary>
    public bool SwingTowardFacing { get; set; }

    public static DevicePlacementSettings From(JsonObject? config)
    {
        var settings = new DevicePlacementSettings();
        if (config?[DeviceCodeMap.SettingsSectionName] is not JsonObject s) return settings;
        settings.RoomParameter = s["roomParameter"]?.ToString().Trim() ?? string.Empty;
        var hand = s["handOrientationPointsTo"]?.ToString().Trim().ToLowerInvariant();
        if (hand == "hinge") settings.HandPointsToLatch = false;
        var swing = s["swingTowardFacing"];
        if (swing != null && bool.TryParse(swing.ToString(), out var b)) settings.SwingTowardFacing = b;
        return settings;
    }
}

/// <summary>Locates and reads the project config (.rktools/mcp.project.config.json).</summary>
internal static class ProjectConfigLocator
{
    /// <summary>
    /// The project root: the explicit argument, else the nearest folder above the model file that
    /// holds a .rktools folder. Null when neither exists.
    /// </summary>
    public static string? FindProjectRoot(Document doc, string explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot)) return explicitRoot.Trim();

        string? path;
        try { path = doc.PathName; }
        catch { return null; }
        if (string.IsNullOrEmpty(path)) return null;

        try
        {
            var dir = Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(dir))
            {
                if (Directory.Exists(Path.Combine(dir, ".rktools"))) return dir;
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { }
        return null;
    }

    public static (JsonObject? Config, string? Path, string? Error) Read(Document doc, string explicitRoot)
    {
        var root = FindProjectRoot(doc, explicitRoot);
        if (root == null)
            return (null, null, "No project config found: pass projectRoot, or create <projectRoot>\\.rktools\\ " +
                                "above the model file (config_set_project_config does this).");

        var (path, error) = ConfigPathResolver.Resolve(ConfigPathResolver.ScopeProject, root);
        if (path == null) return (null, null, error);
        if (!File.Exists(path)) return (null, path, $"Project config does not exist yet: {path}");

        var (config, readError) = new JsonConfigService().Read(path);
        return (config, path, readError);
    }
}

/// <summary>A door or window placed on one face of a room, with its sides resolved.</summary>
internal sealed class RoomOpening
{
    public OpeningRecord Opening { get; set; } = null!;
    public WallFace? Face { get; set; }
    public FaceOpening? OnFace { get; set; }
    /// <summary>+1: lock towards the face end, -1: towards its start, 0: unknown.</summary>
    public int LockSign { get; set; }
    public string LockSource { get; set; } = "unknown";
    public bool? FacingIntoRoom { get; set; }
    public bool? SwingIntoRoom { get; set; }

    /// <summary>Hinge side seen from inside the room, looking at the door.</summary>
    public string? HingeSide
    {
        get
        {
            if (Face == null || LockSign == 0) return null;
            var hingeDir = Face.Direction.Scaled(-LockSign);
            var viewerRight = Face.NormalIntoRoom.Scaled(-1).RightNormal();
            return hingeDir.Dot(viewerRight) > 0 ? "right" : "left";
        }
    }
}

internal static class RoomAnalysis
{
    public static List<RoomOpening> Openings(RoomSourceService service, RoomRecord room, DevicePlacementSettings settings)
    {
        var result = new List<RoomOpening>();
        foreach (var o in service.OpeningsOf(room))
        {
            var face = RoomGeometryMath.NearestFace(room.Faces, o.Center);
            var entry = new RoomOpening { Opening = o, Face = face };
            if (face != null)
                entry.OnFace = RoomGeometryMath.ProjectOpening(face, o.Id, o.Kind, o.Center, o.WidthMm, maxOffsetMm: 600);

            if (o.Facing is { } facing)
                entry.FacingIntoRoom = RoomGeometryMath.Contains(room.Polygon, o.Center.Plus(facing.Scaled(400)));

            if (o.Kind == "door")
            {
                if (entry.FacingIntoRoom is { } into)
                    entry.SwingIntoRoom = settings.SwingTowardFacing ? into : !into;

                if (face != null && entry.OnFace != null)
                {
                    var sign = o.Hand is { } hand ? RoomGeometryMath.LockSignFromHand(face, hand, settings.HandPointsToLatch) : 0;
                    if (sign != 0)
                    {
                        entry.LockSign = sign;
                        entry.LockSource = "handOrientation";
                    }
                    else
                    {
                        entry.LockSign = RoomGeometryMath.LockSignFromCorners(face, entry.OnFace);
                        entry.LockSource = "cornerHeuristic";
                    }
                }
            }

            result.Add(entry);
        }
        return result;
    }

    public static object Point(P2 p) => new { x = Math.Round(p.X, 1), y = Math.Round(p.Y, 1) };

    public static object Vector(P2 v) => new { x = Math.Round(v.X, 4), y = Math.Round(v.Y, 4) };

    public static object RoomSummary(RoomRecord room) => new
    {
        id = room.SourceId,
        kind = room.Kind,
        number = room.Number,
        name = room.Name,
        level = room.HostLevelName,
        sourceLevel = room.SourceLevelName,
        levelElevationMm = Math.Round(room.HostLevelElevationMm, 1),
        floorElevationMm = Math.Round(room.FloorZMm, 1),
        areaM2 = room.AreaM2
    };

    public static object RoomPayload(
        RoomSourceService service,
        RoomRecord room,
        DevicePlacementSettings settings,
        bool includeDoors,
        bool includeWindows,
        bool includeCeilingHeight)
    {
        var openings = includeDoors || includeWindows ? Openings(service, room, settings) : [];
        double? ceiling = includeCeilingHeight ? service.CeilingHeightMm(room) : null;

        return new
        {
            id = room.SourceId,
            kind = room.Kind,
            number = room.Number,
            name = room.Name,
            level = room.HostLevelName,
            sourceLevel = room.SourceLevelName,
            levelElevationMm = Math.Round(room.HostLevelElevationMm, 1),
            floorElevationMm = Math.Round(room.FloorZMm, 1),
            areaM2 = room.AreaM2,
            heightMm = room.HeightMm,
            ceilingHeightMm = ceiling,
            boundary = new
            {
                outer = room.Polygon.Outer.Select(Point).ToList(),
                holes = room.Polygon.Holes.Select(h => h.Select(Point).ToList()).ToList()
            },
            centroid = Point(RoomGeometryMath.Centroid(room.Polygon.Outer)),
            interiorPoint = Point(room.InteriorPoint),
            doors = includeDoors ? openings.Where(o => o.Opening.Kind == "door").Select(DoorPayload).ToList() : null,
            windows = includeWindows ? openings.Where(o => o.Opening.Kind == "window").Select(WindowPayload).ToList() : null,
            warnings = room.Warnings
        };
    }

    public static object DoorPayload(RoomOpening d) => new
    {
        id = d.Opening.Id,
        typeName = d.Opening.TypeName,
        location = Point(d.Opening.Center),
        widthMm = Math.Round(d.Opening.WidthMm, 0),
        heightMm = d.Opening.HeightMm,
        hostWallId = d.Opening.HostWallId,
        wallIndex = d.Face?.Index,
        alongFromMm = d.OnFace != null ? Math.Round(d.OnFace.FromMm, 0) : (double?)null,
        alongToMm = d.OnFace != null ? Math.Round(d.OnFace.ToMm, 0) : (double?)null,
        facingIntoRoom = d.FacingIntoRoom,
        swingIntoRoom = d.SwingIntoRoom,
        hingeSide = d.HingeSide,
        lockSide = d.LockSign == 0 ? null : d.LockSign > 0 ? "towardWallEnd" : "towardWallStart",
        sideSource = d.LockSource,
        facing = d.Opening.Facing is { } f ? Vector(f) : null,
        hand = d.Opening.Hand is { } h ? Vector(h) : null
    };

    public static object WindowPayload(RoomOpening w) => new
    {
        id = w.Opening.Id,
        typeName = w.Opening.TypeName,
        location = Point(w.Opening.Center),
        widthMm = Math.Round(w.Opening.WidthMm, 0),
        heightMm = w.Opening.HeightMm,
        sillHeightMm = w.Opening.SillHeightMm,
        wallIndex = w.Face?.Index,
        alongFromMm = w.OnFace != null ? Math.Round(w.OnFace.FromMm, 0) : (double?)null,
        alongToMm = w.OnFace != null ? Math.Round(w.OnFace.ToMm, 0) : (double?)null
    };

    /// <summary>Room-side wall faces with bounding element, thickness and openings.</summary>
    public static List<object> WallsPayload(RoomSourceService service, RoomRecord room, DevicePlacementSettings settings, List<string> warnings)
    {
        var openings = Openings(service, room, settings);
        var result = new List<object>();

        foreach (var face in room.Faces)
        {
            var raw = room.RawSegmentFor(face);
            var info = DescribeBoundingElement(service, room, face, raw);

            result.Add(new
            {
                index = face.Index,
                loop = face.LoopIndex == 0 ? "outer" : $"hole{face.LoopIndex}",
                wallId = info.WallId,
                category = info.Category,
                typeName = info.TypeName,
                thicknessMm = info.ThicknessMm,
                isSeparationLine = info.IsSeparationLine,
                isCurved = raw?.IsCurved ?? false,
                axis = info.Axis,
                innerFace = new { start = Point(face.Start), end = Point(face.End) },
                normalIntoRoom = Vector(face.NormalIntoRoom),
                lengthMm = Math.Round(face.LengthMm, 0),
                openings = openings
                    .Where(o => o.Face == face && o.OnFace != null)
                    .Select(o => new
                    {
                        id = o.Opening.Id,
                        type = o.Opening.Kind,
                        fromMm = Math.Round(o.OnFace!.FromMm, 0),
                        toMm = Math.Round(o.OnFace.ToMm, 0)
                    })
                    .ToList()
            });
        }

        if (room.RawSegments.Any(s => s.IsCurved))
            warnings.Add($"Room {room.Number}: curved boundary segments are returned as tessellated straight faces (isCurved=true).");
        return result;
    }

    internal sealed class BoundingInfo
    {
        public long? WallId { get; set; }
        public string? Category { get; set; }
        public string? TypeName { get; set; }
        public double? ThicknessMm { get; set; }
        public bool IsSeparationLine { get; set; }
        public object? Axis { get; set; }
    }

    /// <summary>
    /// Finds what bounds a face. Room sources name the element directly; IfcSpace faces are matched
    /// to the wall (or IFC DirectShape wall) whose bounds contain a point 100 mm behind the face.
    /// </summary>
    public static BoundingInfo DescribeBoundingElement(RoomSourceService service, RoomRecord room, WallFace face, RawBoundarySegment? raw)
    {
        var info = new BoundingInfo();
        var doc = service.Source.Doc;
        Element? element = null;

        if (raw?.ElementId is { } id)
        {
            element = doc.GetElement(new ElementId(id));
        }
        else if (room.Kind == "IfcSpace")
        {
            var behind = face.PointAt(face.LengthMm / 2, -100);
            element = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Walls)
                .WhereElementIsNotElementType()
                .Select(e => (e, b: service.Source.HostBounds(e)))
                .Where(t => t.b != null && t.b.Value.Plan.Contains(behind) &&
                            t.b.Value.MinZMm <= room.FloorZMm + 1000 && t.b.Value.MaxZMm >= room.FloorZMm + 1000)
                .OrderBy(t => t.b!.Value.Plan.Width * t.b.Value.Plan.Height)
                .Select(t => t.e)
                .FirstOrDefault();
        }

        if (element == null)
        {
            info.IsSeparationLine = raw?.ElementId == null && room.Kind == "Room";
            return info;
        }

        info.WallId = element.Id.Value;
        info.Category = element.Category?.Name;
        try { info.TypeName = doc.GetElement(element.GetTypeId())?.Name; } catch { }

        if (element is Wall wall)
        {
            try { info.ThicknessMm = Math.Round(RoomUnits.FtToMm(wall.Width), 0); } catch { }
            if (wall.Location is LocationCurve lc)
            {
                var c = lc.Curve;
                info.Axis = new
                {
                    start = Point(service.Source.ToHostMm(c.GetEndPoint(0))),
                    end = Point(service.Source.ToHostMm(c.GetEndPoint(1)))
                };
            }
        }
        else if (element is ModelCurve)
        {
            info.IsSeparationLine = true;
            info.WallId = null;
        }

        return info;
    }
}
