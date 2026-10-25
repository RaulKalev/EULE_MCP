using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Coordination.Clash.Geometry;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Geometry;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Geometry;

/// <summary>revit_get_element_geometry (#107): where elements are and which way they face, in host mm.</summary>
public sealed class GetElementGeometryTool : IRevitMcpTool
{
    public string Name => "revit_get_element_geometry";

    public string Description =>
        "Returns the geometry of up to 100 elements by id, in host internal mm (0.1 mm): location (point with rotation, " +
        "or curve endpoints and length), bbox {min, max, sizeMm, centerMm} (the owner view's box for view-specific " +
        "elements), facing and hand orientation with mirrored/flipped flags, host {id, category} (a face host inside a " +
        "link is resolved to the linked element), level, room (last phase), ownerViewId, isPinned, groupId. " +
        "linkInstanceId reads ids inside that link and transforms everything to host coordinates. " +
        "includeSolidsSummary adds the volume (mm³) and face count of the element's first solid. Calls without " +
        "elementIds are refused with status geometry_query_too_broad.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return Task.FromResult(documentFailure!);
        var doc = target.Document;
        var args = request.Arguments;
        var warnings = new List<string>();

        var ids = ToolArguments.GetLongArray(args, "elementIds").Where(id => id > 0).Distinct().ToList();
        var tooBroad = GeometryQueryGuard.Validate(request.RequestId, hasCategoryFilter: false, hasElementIds: ids.Count > 0, hasSpatialScope: false,
            "revit_get_element_geometry needs elementIds: it never reads the geometry of a whole model.",
            "Pass elementIds (find them with revit_graph_query or revit_find_elements_in_box first).");
        if (tooBroad != null) return Task.FromResult(tooBroad);
        if (ids.Count > GeometryQueryGuard.MaxElementIds)
            return Task.FromResult(GeometryQueryGuard.Refuse(request.RequestId,
                $"{ids.Count} element ids given; at most {GeometryQueryGuard.MaxElementIds} per call.",
                $"Split the ids into calls of {GeometryQueryGuard.MaxElementIds} or fewer."));

        var model = new GeometryModel { Doc = doc };
        var linkInstanceId = ToolArguments.GetLong(args, "linkInstanceId");
        if (linkInstanceId > 0)
        {
            var link = GeometryToolSupport.ResolveLink(doc, linkInstanceId, request, out var linkFailure);
            if (link == null) return Task.FromResult(linkFailure!);
            model = link;
        }

        var includeSolids = ToolArguments.GetBool(args, "includeSolidsSummary");
        Phase? phase = null;
        try { phase = model.Doc.Phases.Cast<Phase>().LastOrDefault(); } catch { }

        var elements = new List<object>();
        var missing = new List<long>();
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var element = model.Doc.GetElement(new ElementId(id));
            if (element == null)
            {
                missing.Add(id);
                continue;
            }
            elements.Add(Describe(model, element, phase, includeSolids, warnings));
        }
        if (missing.Count > 0)
            warnings.Add($"{missing.Count} id(s) not found in the {model.Label}: {string.Join(", ", missing.Take(20))}" +
                         (linkInstanceId > 0 ? "." : ". Ids inside a link need linkInstanceId."));

        return Task.FromResult(GeometryToolSupport.Ok(request, sw,
            $"Geometry of {elements.Count} of {ids.Count} element(s) in the {model.Label}, host coordinates in mm.",
            new
            {
                units = "mm, host internal coordinates",
                link = model.Describe(),
                phase = phase?.Name,
                count = elements.Count,
                notFound = missing,
                elements
            },
            warnings));
    }

    private static object Describe(GeometryModel model, Element element, Phase? phase, bool includeSolids, List<string> warnings)
    {
        var toHost = model.ToHost;
        var box = GeometryConvert.ElementBox(element, out var boxSource);
        var instance = element as FamilyInstance;
        var level = RevitLevelReader.Resolve(element);

        return new
        {
            elementId = element.Id.Value,
            uniqueId = element.UniqueId,
            category = element.Category?.Name ?? string.Empty,
            name = GeometryConvert.SafeName(element),
            typeName = GeometryToolSupport.TypeName(element),
            documentTitle = model.Doc.Title,
            linkInstanceId = model.LinkInstanceId,
            location = Location(element, toHost),
            bbox = box == null ? null : GeometryConvert.Box(box, toHost),
            bboxSource = box == null ? null : boxSource,
            facing = instance == null ? null : Safe(() => GeometryConvert.Direction(instance.FacingOrientation, toHost)),
            hand = instance == null ? null : Safe(() => GeometryConvert.Direction(instance.HandOrientation, toHost)),
            mirrored = instance == null ? (bool?)null : SafeBool(() => instance.Mirrored),
            facingFlipped = instance == null ? (bool?)null : SafeBool(() => instance.FacingFlipped),
            handFlipped = instance == null ? (bool?)null : SafeBool(() => instance.HandFlipped),
            host = instance == null ? null : Host(model, instance),
            level = level.Found ? new { id = level.Id, name = level.Name, source = level.Source } : null,
            room = RoomOf(model.Doc, element, instance, phase),
            ownerViewId = Id(() => element.OwnerViewId),
            isPinned = SafeBool(() => element.Pinned),
            groupId = Id(() => element.GroupId),
            solids = includeSolids ? Solids(element, warnings) : null
        };
    }

    private static object? Location(Element element, Transform? toHost)
    {
        try
        {
            switch (element.Location)
            {
                case LocationPoint lp:
                    double? rotation = null;
                    try { rotation = lp.Rotation * 180.0 / Math.PI; } catch { }
                    // A link's rotation about Z adds to the element's own.
                    if (rotation.HasValue && toHost != null)
                        rotation += Math.Atan2(toHost.BasisX.Y, toHost.BasisX.X) * 180.0 / Math.PI;
                    return new
                    {
                        kind = "point",
                        pointMm = GeometryConvert.Point(lp.Point, toHost),
                        rotationDeg = rotation.HasValue ? Math.Round(Normalize(rotation.Value), 2) : (double?)null
                    };
                case LocationCurve lc:
                    var curve = lc.Curve;
                    return new
                    {
                        kind = curve is Line ? "line" : "curve",
                        startMm = GeometryConvert.Point(curve.GetEndPoint(0), toHost),
                        endMm = GeometryConvert.Point(curve.GetEndPoint(1), toHost),
                        lengthMm = GeometryUnits.RoundMm(GeometryUnits.FtToMm(curve.Length))
                    };
            }
        }
        catch { }
        return new { kind = "none" };
    }

    private static double Normalize(double degrees)
    {
        var d = degrees % 360.0;
        if (d < 0) d += 360.0;
        return d >= 359.995 ? 0 : d;
    }

    /// <summary>
    /// The host of a hosted instance. Hosted on a face of a linked element, Revit reports the link instance as
    /// <c>Host</c>; the linked element behind <c>HostFace</c> is reported instead.
    /// </summary>
    private static object? Host(GeometryModel model, FamilyInstance instance)
    {
        try
        {
            var host = instance.Host;
            if (host == null) return null;
            if (host is RevitLinkInstance link)
            {
                Reference? face = null;
                try { face = instance.HostFace; } catch { }
                if (face != null && face.LinkedElementId != ElementId.InvalidElementId)
                {
                    var linked = link.GetLinkDocument()?.GetElement(face.LinkedElementId);
                    return new
                    {
                        id = face.LinkedElementId.Value,
                        category = linked?.Category?.Name ?? string.Empty,
                        name = linked != null ? GeometryConvert.SafeName(linked) : string.Empty,
                        linkInstanceId = link.Id.Value,
                        linkName = GeometryConvert.SafeName(link)
                    };
                }
                return new { id = link.Id.Value, category = link.Category?.Name ?? "RVT Links", name = GeometryConvert.SafeName(link), linkInstanceId = (long?)null, linkName = (string?)null };
            }
            return new
            {
                id = host.Id.Value,
                category = host.Category?.Name ?? string.Empty,
                name = GeometryConvert.SafeName(host),
                linkInstanceId = model.LinkInstanceId,
                linkName = model.Link != null ? model.LinkName : null
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The room of the element in the given phase: FamilyInstance.Room, else the room at its point (bbox centre).</summary>
    private static object? RoomOf(Document doc, Element element, FamilyInstance? instance, Phase? phase)
    {
        if (phase == null) return null;
        Room? room = null;
        var method = "none";
        try
        {
            if (instance != null)
            {
                room = instance.get_Room(phase);
                if (room != null) method = "FamilyInstance.Room";
            }
            if (room == null && element is not Room)
            {
                var point = GeometryConvert.RepresentativePoint(element);
                if (point != null)
                {
                    room = doc.GetRoomAtPoint(point, phase);
                    if (room != null) method = "GetRoomAtPoint";
                }
            }
        }
        catch { }
        if (room == null) return null;
        return new
        {
            id = room.Id.Value,
            number = room.Number,
            name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? GeometryConvert.SafeName(room),
            method
        };
    }

    private static object? Solids(Element element, List<string> warnings)
    {
        var solid = ClashSolidExtractor.TryExtract(element, out var warning);
        if (solid == null)
        {
            if (warning != null) warnings.Add(warning);
            return new { hasSolid = false };
        }
        const double mm3PerFt3 = GeometryUnits.MmPerFoot * GeometryUnits.MmPerFoot * GeometryUnits.MmPerFoot;
        return new
        {
            hasSolid = true,
            volumeMm3 = GeometryUnits.RoundVolume(solid.Volume * mm3PerFt3),
            faceCount = solid.Faces.Size,
            note = "First solid of the element's geometry (ClashSolidExtractor); nested instances are searched."
        };
    }

    private static long? Id(Func<ElementId> read)
    {
        try
        {
            var id = read();
            return id == null || id == ElementId.InvalidElementId ? null : id.Value;
        }
        catch
        {
            return null;
        }
    }

    private static T? Safe<T>(Func<T?> read) where T : class
    {
        try { return read(); }
        catch { return null; }
    }

    private static bool? SafeBool(Func<bool> read)
    {
        try { return read(); }
        catch { return null; }
    }
}
