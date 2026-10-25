using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.RoomDevices;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// revit_get_element_rooms: the Revit Room each element belongs to. Wall-mounted devices sit on a room's
/// boundary, where Revit's own answer is unreliable, so a point a little in front of the device is tested too
/// and both answers are returned when they differ.
/// </summary>
public sealed class GetElementRoomsTool : IRevitMcpTool
{
    public string Name => "revit_get_element_rooms";
    public string Description =>
        "Finds the Room of elements (host model): FamilyInstance.Room, else the room at the element's point on plan, else the room at a " +
        "point offsetMm in front of it (its facing direction), which settles wall-mounted devices on a boundary. The room in front is " +
        "returned as well, with conflict=true when it is another room. Elements from " +
        "elementIds, categories, a level and/or the selection. Read-only.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public const string MethodRoom = "FamilyInstance.Room";
    public const string MethodPoint = "GetRoomAtPoint";
    public const string MethodOffset = "GetRoomAtPoint+offset";
    public const string MethodOpposite = "opposite";
    public const string MethodNone = "none";

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var documentTarget = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (documentTarget == null) return Task.FromResult(documentFailure!);
        var doc = documentTarget.Document;
        var uidoc = ToolDocument.UiDocumentOf(uiapp, documentTarget);
        if (ToolArguments.GetBool(request.Arguments, "useSelection") && uidoc == null)
            return Task.FromResult(ToolDocument.ActiveRequired(request, "useSelection", documentTarget.Document.Title));

        var args = request.Arguments;
        var ids = ToolArguments.GetLongArray(args, "elementIds");
        var categories = ToolArguments.GetStringArray(args, "categories").Where(c => !string.IsNullOrWhiteSpace(c)).ToArray();
        var useSelection = ToolArguments.GetBool(args, "useSelection");
        var offsetFt = RoomUnits.MmToFt(ToolArguments.GetDouble(args, "offsetMm", 200));
        var limit = Math.Max(1, ToolArguments.GetInt(args, "limit", 5000));

        if (!ElementRoomSupport.TryResolveLevel(doc, args, out var level, out var levelError))
            return Task.FromResult(ElementRoomSupport.Fail(request, levelError!));
        if (ids.Length == 0 && categories.Length == 0 && !useSelection)
            return Task.FromResult(ElementRoomSupport.Fail(request,
                "Give a source: elementIds, categories (e.g. 'Electrical Fixtures', 'Lighting Fixtures') or useSelection=true. A level only narrows them."));
        if (!ElementRoomSupport.TryResolvePhase(doc, ToolArguments.GetString(args, "phaseName"), out var phase, out var phaseError))
            return Task.FromResult(ElementRoomSupport.Fail(request, phaseError!));

        var warnings = new List<string>();
        var elements = new Dictionary<long, Element>();
        foreach (var id in ids)
        {
            if (doc.GetElement(new ElementId(id)) is { } element) elements[id] = element;
            else warnings.Add($"Element {id} was not found.");
        }
        if (useSelection)
            foreach (var id in uidoc!.Selection.GetElementIds())
                if (doc.GetElement(id) is { } element) elements[id.Value] = element;
        foreach (var name in categories)
        {
            var category = doc.Settings.Categories.Cast<Category>()
                .FirstOrDefault(c => string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (category == null)
            {
                warnings.Add($"Category '{name}' was not found in this document.");
                continue;
            }
            foreach (var element in new FilteredElementCollector(doc).OfCategoryId(category.Id).WhereElementIsNotElementType())
                elements[element.Id.Value] = element;
        }

        var scoped = elements.Values
            .Where(e => level == null || ElementRoomSupport.LevelOf(doc, e)?.Id == level.Id)
            .OrderBy(e => e.Id.Value)
            .ToList();
        if (scoped.Count > limit)
        {
            warnings.Add($"{scoped.Count} elements matched; only the first {limit} were read (raise limit, or narrow by level or category).");
            scoped = scoped.Take(limit).ToList();
        }

        var results = new List<ElementRoomResult>(scoped.Count);
        foreach (var element in scoped)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(Resolve(doc, element, phase!, offsetFt));
        }

        ElementRoomSupport.Page(args, results.Count, out var page, out var pageSize, out var skip);
        var pageItems = results.Skip(skip).Take(pageSize).ToList();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{results.Count} element(s): {results.Count(r => r.Room != null)} with a room, {results.Count(r => r.Room == null)} without, " +
                      $"{results.Count(r => r.Conflict)} where the room in front differs.",
            Data = new
            {
                phase = phase!.Name,
                offsetMm = Math.Round(RoomUnits.FtToMm(offsetFt)),
                summary = new
                {
                    total = results.Count,
                    found = results.Count(r => r.Room != null),
                    withoutRoom = results.Count(r => r.Room == null),
                    conflicts = results.Count(r => r.Conflict),
                    byMethod = results.GroupBy(r => r.Method).ToDictionary(g => g.Key, g => g.Count())
                },
                page,
                pageSize,
                returned = pageItems.Count,
                hasMore = skip + pageItems.Count < results.Count,
                elements = pageItems.Select(r => r.ToDto(doc))
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private sealed class ElementRoomResult
    {
        public Element Element = null!;
        public XYZ? Location;
        public XYZ? Facing;
        public Room? Room;
        public Room? RoomAtOffset;
        public Room? RoomBehind;
        public string Method = MethodNone;
        public string? Reason;

        /// <summary>The element's own room and the room in front of it are two different rooms.</summary>
        public bool Conflict => Room != null && RoomAtOffset != null && Room.Id != RoomAtOffset.Id;

        public object ToDto(Document doc)
        {
            var instance = Element as FamilyInstance;
            return new
            {
                elementId = Element.Id.Value,
                category = Element.Category?.Name ?? string.Empty,
                family = instance?.Symbol?.FamilyName ?? string.Empty,
                type = instance?.Symbol?.Name ?? Element.Name,
                levelName = ElementRoomSupport.LevelOf(doc, Element)?.Name,
                locationMm = Location == null ? null : ElementRoomSupport.PointMm(Location),
                facingOrientation = Facing == null ? null : new { x = Math.Round(Facing.X, 4), y = Math.Round(Facing.Y, 4), z = Math.Round(Facing.Z, 4) },
                room = ElementRoomSupport.DescribeRoom(Room),
                roomAtOffset = ElementRoomSupport.DescribeRoom(RoomAtOffset),
                roomBehind = ElementRoomSupport.DescribeRoom(RoomBehind),
                method = Method,
                conflict = Conflict,
                reason = Reason
            };
        }
    }

    private static ElementRoomResult Resolve(Document doc, Element element, Phase phase, double offsetFt)
    {
        var result = new ElementRoomResult { Element = element };
        if (element is not FamilyInstance instance)
        {
            result.Reason = "Not a family instance.";
            return result;
        }
        if (instance.Location is not LocationPoint location)
        {
            result.Reason = "Has no location point (line-based or sketch-based element).";
            return result;
        }
        result.Location = location.Point;

        Room? own = null;
        try { own = instance.get_Room(phase); }
        catch { /* not room-bounded in this phase */ }

        var facing = FacingOf(instance);
        result.Facing = facing;

        // Rooms are tested a foot above the element's level, inside the room's height whatever the device's own height
        var level = ElementRoomSupport.LevelOf(doc, element);
        double z = (level?.ProjectElevation ?? location.Point.Z) + 1.0;
        Room? At(XYZ direction)
        {
            var p = location.Point + direction * offsetFt;
            try { return doc.GetRoomAtPoint(new XYZ(p.X, p.Y, z), phase); }
            catch { return null; }
        }

        Room? front = facing == null ? null : At(facing);
        result.RoomAtOffset = front;
        // Families differ in which way "facing" points: out of the wall into the room, or into the wall. The
        // caller needs both sides to tell (in a model where the back is always a room, the front is the neighbour).
        result.RoomBehind = facing == null ? null : At(facing.Negate());

        if (own != null)
        {
            result.Room = own;
            result.Method = MethodRoom;
        }
        else if (At(XYZ.Zero) is { } atPoint)
        {
            // FamilyInstance.Room is empty for a device above or below the room's height (a ceiling light at the
            // level, a box in the slab), yet its point is plainly inside a room on plan
            result.Room = atPoint;
            result.Method = MethodPoint;
        }
        else if (front != null)
        {
            result.Room = front;
            result.Method = MethodOffset;
        }
        else if (facing != null && At(facing.Negate()) is { } behind)
        {
            result.Room = behind;
            result.RoomAtOffset = behind;
            result.Method = MethodOpposite;
        }
        else
        {
            result.Reason = facing == null
                ? "No room at the element, and it has no facing direction to look along."
                : "No room at the element, in front of it or behind it (outside, on a facade, or the rooms are not placed).";
        }
        return result;
    }

    /// <summary>The horizontal direction the element faces; from the hand direction when the facing one is vertical or empty.</summary>
    private static XYZ? FacingOf(FamilyInstance instance)
    {
        try
        {
            var facing = Flat(instance.FacingOrientation);
            if (facing != null) return facing;
            var hand = Flat(instance.HandOrientation);
            return hand == null ? null : Flat(hand.CrossProduct(XYZ.BasisZ));
        }
        catch
        {
            return null;
        }
    }

    private static XYZ? Flat(XYZ? vector)
    {
        if (vector == null) return null;
        var flat = new XYZ(vector.X, vector.Y, 0);
        return flat.GetLength() < 1e-6 ? null : flat.Normalize();
    }
}

/// <summary>revit_get_room_boundaries: the real outline of rooms, loop by loop.</summary>
public sealed class GetRoomBoundariesTool : IRevitMcpTool
{
    public string Name => "revit_get_room_boundaries";
    public string Description =>
        "Returns the boundary loops of host-model Rooms in mm (first loop = outer boundary, the rest = holes; arcs tessellated), with " +
        "number, name, level, area and location point. boundaryLocation: Finish (default), Center, CoreBoundary or CoreCenter. " +
        "Unplaced or unenclosed rooms are listed separately. Lighter than revit_get_room_geometry (no doors, windows or ceilings). Read-only.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var documentTarget = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (documentTarget == null) return Task.FromResult(documentFailure!);
        var doc = documentTarget.Document;

        var args = request.Arguments;
        var roomIds = ToolArguments.GetLongArray(args, "roomIds");
        var includeArea = ToolArguments.GetBool(args, "includeArea", true);
        var limit = Math.Max(1, ToolArguments.GetInt(args, "limit", 1000));
        if (!ElementRoomSupport.TryResolveLevel(doc, args, out var level, out var levelError))
            return Task.FromResult(ElementRoomSupport.Fail(request, levelError!));

        var locationName = ToolArguments.GetString(args, "boundaryLocation", "Finish").Trim();
        if (locationName.Length == 0) locationName = "Finish";
        if (!Enum.TryParse<SpatialElementBoundaryLocation>(locationName, ignoreCase: true, out var boundaryLocation)
            || !Enum.IsDefined(typeof(SpatialElementBoundaryLocation), boundaryLocation))
            return Task.FromResult(ElementRoomSupport.Fail(request, "boundaryLocation must be Finish, Center, CoreBoundary or CoreCenter."));

        var warnings = new List<string>();
        List<Room> rooms;
        if (roomIds.Length > 0)
        {
            rooms = new List<Room>();
            foreach (var id in roomIds)
            {
                if (doc.GetElement(new ElementId(id)) is Room room) rooms.Add(room);
                else warnings.Add($"Element {id} is not a room.");
            }
        }
        else
        {
            rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                .OfType<Room>().ToList();
        }
        if (level != null) rooms = rooms.Where(r => r.LevelId == level.Id).ToList();
        rooms = rooms.OrderBy(r => r.Level?.ProjectElevation ?? 0).ThenBy(r => r.Number, StringComparer.OrdinalIgnoreCase).ToList();
        if (rooms.Count > limit)
        {
            warnings.Add($"{rooms.Count} rooms matched; only the first {limit} were read (raise limit, or narrow by level).");
            rooms = rooms.Take(limit).ToList();
        }

        var options = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = boundaryLocation };
        var placed = new List<object>();
        var unplaced = new List<object>();
        foreach (var room in rooms)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var loops = room.Area > 0 ? Loops(room, options) : new List<List<P2>>();
            if (loops.Count == 0)
            {
                unplaced.Add(new
                {
                    roomId = room.Id.Value,
                    number = room.Number,
                    name = ElementRoomSupport.RoomName(room),
                    levelName = room.Level?.Name,
                    reason = room.Location == null ? "Not placed." : "Not enclosed (no area or no boundary)."
                });
                continue;
            }

            var point = (room.Location as LocationPoint)?.Point;
            placed.Add(new
            {
                roomId = room.Id.Value,
                number = room.Number,
                name = ElementRoomSupport.RoomName(room),
                levelName = room.Level?.Name,
                areaM2 = includeArea ? Math.Round(UnitUtils.ConvertFromInternalUnits(room.Area, UnitTypeId.SquareMeters), 2) : (double?)null,
                locationMm = point == null ? null : new { x = Math.Round(RoomUnits.FtToMm(point.X), 1), y = Math.Round(RoomUnits.FtToMm(point.Y), 1) },
                loops = loops.Select(loop => loop.Select(p => new { x = p.X, y = p.Y }))
            });
        }

        ElementRoomSupport.Page(args, placed.Count, out var page, out var pageSize, out var skip);
        var pageItems = placed.Skip(skip).Take(pageSize).ToList();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{placed.Count} room(s) with a boundary, {unplaced.Count} unplaced or not enclosed.",
            Data = new
            {
                boundaryLocation = boundaryLocation.ToString(),
                total = placed.Count,
                page,
                pageSize,
                returned = pageItems.Count,
                hasMore = skip + pageItems.Count < placed.Count,
                rooms = pageItems,
                unplaced
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    /// <summary>Boundary loops in mm, the outer boundary (largest area) first; arcs tessellated.</summary>
    private static List<List<P2>> Loops(Room room, SpatialElementBoundaryOptions options)
    {
        IList<IList<BoundarySegment>>? raw;
        try { raw = room.GetBoundarySegments(options); }
        catch { return new List<List<P2>>(); }
        if (raw == null) return new List<List<P2>>();

        var loops = new List<List<P2>>();
        foreach (var segments in raw)
        {
            var loop = new List<P2>();
            foreach (var segment in segments)
            {
                var curve = segment.GetCurve();
                if (curve == null) continue;
                var points = curve is Line ? new List<XYZ> { curve.GetEndPoint(0), curve.GetEndPoint(1) } : curve.Tessellate();
                foreach (var point in points)
                {
                    var p = new P2(Math.Round(RoomUnits.FtToMm(point.X), 1), Math.Round(RoomUnits.FtToMm(point.Y), 1));
                    // Each segment starts where the last one ended
                    if (loop.Count == 0 || loop[loop.Count - 1].DistanceTo(p) > 0.5) loop.Add(p);
                }
            }
            if (loop.Count > 1 && loop[0].DistanceTo(loop[loop.Count - 1]) <= 0.5) loop.RemoveAt(loop.Count - 1);
            if (loop.Count >= 3) loops.Add(loop);
        }
        return loops.OrderByDescending(loop => Math.Abs(RoomGeometryMath.SignedArea(loop))).ToList();
    }
}

internal static class ElementRoomSupport
{
    public const int DefaultPageSize = 100;
    public const int MaxPageSize = 500;

    public static McpToolResult Fail(McpToolRequest request, string message)
        => new() { RequestId = request.RequestId, Success = false, Status = "validation_failed", Message = message };

    /// <summary>Zero-based page and a page size clamped to 1..500 (100 when not given), as in revit_get_elements_info.</summary>
    public static void Page(Dictionary<string, object?> args, int total, out int page, out int pageSize, out int skip)
    {
        page = Math.Max(0, ToolArguments.GetInt(args, "page"));
        pageSize = ToolArguments.GetInt(args, "pageSize", -1);
        pageSize = pageSize <= 0 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);
        skip = (int)Math.Min((long)page * pageSize, total);
    }

    /// <summary>The level named by levelId or levelName; null (and true) when neither is given.</summary>
    public static bool TryResolveLevel(Document doc, Dictionary<string, object?> args, out Level? level, out string? error)
    {
        level = null;
        error = null;
        var id = ToolArguments.GetLong(args, "levelId");
        var name = ToolArguments.GetString(args, "levelName").Trim();
        if (id > 0)
        {
            level = doc.GetElement(new ElementId(id)) as Level;
            if (level == null) error = $"Element {id} is not a level.";
        }
        else if (name.Length > 0)
        {
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            level = levels.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
            if (level == null) error = $"No level named '{name}'. Levels: {string.Join(", ", levels.OrderBy(l => l.ProjectElevation).Select(l => l.Name))}.";
        }
        return error == null;
    }

    /// <summary>The phase by name, or the document's last phase.</summary>
    public static bool TryResolvePhase(Document doc, string name, out Phase? phase, out string? error)
    {
        error = null;
        var phases = doc.Phases.Cast<Phase>().ToList();
        phase = name.Trim().Length == 0
            ? phases.LastOrDefault()
            : phases.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (phase == null)
            error = phases.Count == 0 ? "The document has no phases." : $"No phase named '{name}'. Phases: {string.Join(", ", phases.Select(p => p.Name))}.";
        return error == null;
    }

    public static Level? LevelOf(Document doc, Element element)
    {
        var id = element.LevelId;
        if (id == null || id == ElementId.InvalidElementId)
        {
            var parameter = element.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)
                            ?? element.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM)
                            ?? element.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM);
            id = parameter != null && parameter.StorageType == StorageType.ElementId ? parameter.AsElementId() : ElementId.InvalidElementId;
        }
        return id == null || id == ElementId.InvalidElementId ? null : doc.GetElement(id) as Level;
    }

    public static object PointMm(XYZ point) => new
    {
        x = Math.Round(RoomUnits.FtToMm(point.X), 1),
        y = Math.Round(RoomUnits.FtToMm(point.Y), 1),
        z = Math.Round(RoomUnits.FtToMm(point.Z), 1)
    };

    public static string RoomName(Room room)
        => room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? room.Name;

    public static object? DescribeRoom(Room? room)
        => room == null ? null : new { id = room.Id.Value, number = room.Number, name = RoomName(room), levelName = room.Level?.Name };
}
