using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using RevitMCP.Addin.Geometry;
using RevitMCP.Addin.Query;

namespace RevitMCP.Addin.ViewElements;

/// <summary>
/// Fills the fields a view read adds to each element (#136): family of any type, location, bounding box in the view,
/// host or tagged elements, owner view, group membership, tags and, on request, room. Every read fails open: a field
/// Revit will not give is left out rather than failing the element.
/// </summary>
internal sealed class ViewElementFields
{
    private readonly Document _doc;
    private readonly bool _includeRoom;
    private readonly bool _includeAllTags;
    private readonly ElementTagIndex _tags;
    private readonly Dictionary<long, string> _viewNames = new();
    private readonly Dictionary<long, Phase?> _viewPhases = new();

    public ViewElementFields(Document doc, bool includeRoom, bool includeAllTags)
    {
        _doc = doc;
        _includeRoom = includeRoom;
        _includeAllTags = includeAllTags;
        _tags = ElementTagIndex.Build(doc);
    }

    public void Apply(ElementInfoDto info, Element element, View view)
    {
        if (string.IsNullOrEmpty(info.Family) && _doc.GetElement(element.GetTypeId()) is ElementType type)
            info.Family = Safe(() => type.FamilyName) ?? string.Empty;

        info.Location = LocationOf(element);
        info.BoundingBox = BoxOf(element, view);
        info.OwnerView = OwnerViewOf(element);
        info.Host = HostOf(element);
        info.TaggedElementIds = TaggedOf(element);
        ApplyGroup(info, element);
        info.Tags = TagsOf(info.ElementId, view);
        if (_includeRoom) info.Room = RoomOf(element, view);
    }

    private static ViewElementLocationDto? LocationOf(Element element)
    {
        try
        {
            switch (element)
            {
                case CurveElement curveElement when curveElement.GeometryCurve != null:
                    return CurveLocation(curveElement.GeometryCurve);
                case Grid grid when grid.Curve != null:
                    return CurveLocation(grid.Curve);
                case TextElement text:
                    return PointLocation("text", text.Coord, null);
                case IndependentTag tag:
                    return PointLocation("tagHead", tag.TagHeadPosition, null);
                case Viewport viewport:
                    return PointLocation("viewport", viewport.GetBoxCenter(), null);
            }
            switch (element.Location)
            {
                case LocationPoint point:
                    return PointLocation("point", point.Point, RotationOf(point));
                case LocationCurve curve when curve.Curve != null:
                    return CurveLocation(curve.Curve);
            }
        }
        catch { }
        return null;
    }

    private static ViewElementLocationDto PointLocation(string kind, XYZ point, double? rotationDeg) => new()
    {
        Kind = kind,
        Point = GeometryConvert.Point(point),
        RotationDeg = rotationDeg
    };

    private static ViewElementLocationDto CurveLocation(Curve curve)
    {
        var location = new ViewElementLocationDto { Kind = "curve", CurveType = curve.GetType().Name };
        if (curve.IsBound)
        {
            location.Start = GeometryConvert.Point(curve.GetEndPoint(0));
            location.End = GeometryConvert.Point(curve.GetEndPoint(1));
            location.LengthMm = GeometryUnits.RoundMm(GeometryUnits.FtToMm(curve.Length));
        }
        return location;
    }

    private static double? RotationOf(LocationPoint point)
    {
        // Rotation throws for location points that have none.
        try { return Math.Round(point.Rotation * 180.0 / Math.PI, 2); }
        catch { return null; }
    }

    private static BoxMmDto? BoxOf(Element element, View view)
    {
        try
        {
            var box = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
            return box == null ? null : GeometryConvert.Box(box);
        }
        catch
        {
            return null;
        }
    }

    private ViewElementRefDto? OwnerViewOf(Element element)
    {
        var id = element.OwnerViewId;
        if (id == null || id == ElementId.InvalidElementId) return null;
        if (!_viewNames.TryGetValue(id.Value, out var name))
        {
            name = (_doc.GetElement(id) as View)?.Name ?? string.Empty;
            _viewNames[id.Value] = name;
        }
        return new ViewElementRefDto { Id = id.Value, Name = name };
    }

    private static ViewElementRefDto? HostOf(Element element)
    {
        if (element is not FamilyInstance instance) return null;
        try
        {
            var host = instance.Host;
            return host == null
                ? null
                : new ViewElementRefDto { Id = host.Id.Value, Category = host.Category?.Name, Name = GeometryConvert.SafeName(host) };
        }
        catch
        {
            return null;
        }
    }

    private static List<long>? TaggedOf(Element element)
    {
        try
        {
            switch (element)
            {
                case IndependentTag tag:
                    var ids = tag.GetTaggedLocalElementIds()
                        .Where(id => id != null && id != ElementId.InvalidElementId)
                        .Select(id => id.Value)
                        .ToList();
                    return ids.Count > 0 ? ids : null;
                case RoomTag roomTag when roomTag.Room != null:
                    return new List<long> { roomTag.Room.Id.Value };
                case SpaceTag spaceTag when spaceTag.Space != null:
                    return new List<long> { spaceTag.Space.Id.Value };
                case AreaTag areaTag when areaTag.Area != null:
                    return new List<long> { areaTag.Area.Id.Value };
            }
        }
        catch { }
        return null;
    }

    /// <summary>Lines always say whether they are in a group (the drafting-view case); other elements only when they are.</summary>
    private static void ApplyGroup(ElementInfoDto info, Element element)
    {
        var groupId = element.GroupId;
        if (groupId != null && groupId != ElementId.InvalidElementId)
        {
            info.PartOfGroup = true;
            info.GroupId = groupId.Value;
        }
        else if (element is CurveElement)
        {
            info.PartOfGroup = false;
        }
    }

    /// <summary>The tags in this view, or in all views with includeTags=true. A view read leaves out an empty list unless includeTags asked for it.</summary>
    private List<TagInfoDto>? TagsOf(long elementId, View view)
    {
        var all = _tags.GetTags(elementId);
        if (_includeAllTags) return all ?? new List<TagInfoDto>();
        var here = all?.Where(tag => tag.ViewId == view.Id.Value).ToList();
        return here is { Count: > 0 } ? here : null;
    }

    private ViewElementRoomDto? RoomOf(Element element, View view)
    {
        if (element.ViewSpecific || element is Room) return null;
        var phase = PhaseOf(view);
        if (phase == null) return null;
        try
        {
            var room = (element as FamilyInstance)?.get_Room(phase);
            if (room == null)
            {
                var point = GeometryConvert.RepresentativePoint(element);
                if (point != null) room = _doc.GetRoomAtPoint(point, phase);
            }
            return room == null
                ? null
                : new ViewElementRoomDto
                {
                    Id = room.Id.Value,
                    Number = room.Number,
                    Name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? GeometryConvert.SafeName(room)
                };
        }
        catch
        {
            return null;
        }
    }

    private Phase? PhaseOf(View view)
    {
        if (_viewPhases.TryGetValue(view.Id.Value, out var phase)) return phase;
        try
        {
            var id = view.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId();
            phase = id == null ? null : _doc.GetElement(id) as Phase;
        }
        catch
        {
            phase = null;
        }
        _viewPhases[view.Id.Value] = phase;
        return phase;
    }

    private static string? Safe(Func<string> read)
    {
        try { return read(); }
        catch { return null; }
    }
}
