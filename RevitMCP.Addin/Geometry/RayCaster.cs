using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Placement;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Geometry;

/// <summary>What a ray hit, resolved through the link instance when the hit is inside a link.</summary>
internal sealed class RayTarget
{
    /// <summary>The element id — in the link document when <see cref="LinkInstanceId"/> is set.</summary>
    public long ElementId { get; set; }
    public string Category { get; set; } = string.Empty;

    /// <summary>The category's BuiltInCategory name (<c>OST_Walls</c>), or empty.</summary>
    public string BuiltInCategory { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>"Host", the link's name (or "Link"), or "Unknown" when the reference could not be read.</summary>
    public string Model { get; set; } = string.Empty;
    public long? LinkInstanceId { get; set; }
    public string LinkName { get; set; } = string.Empty;
    public string DocumentTitle { get; set; } = string.Empty;
}

/// <summary>One hit along a ray, nearest first in <see cref="RayCaster.HitsAlong"/>.</summary>
internal sealed class RayHit
{
    public Reference Reference { get; set; } = null!;
    public RayTarget Target { get; set; } = null!;

    /// <summary>Distance from the ray origin, feet.</summary>
    public double DistanceFt { get; set; }

    /// <summary>Where the ray met the face, host coordinates, feet.</summary>
    public XYZ Point { get; set; } = XYZ.Zero;
}

/// <summary>
/// Ray casting shared by the alignment, face placement and geometry query tools (#107). Rays run through a
/// <see cref="ReferenceIntersector"/> on a 3D view, which sees the host model and, with
/// <c>FindReferencesInRevitLinks</c>, loaded links, so a wall in a linked architecture or IFC model is found like a
/// native one. Only geometry visible in that view can be hit. No ElementFilter is passed on purpose:
/// ReferenceIntersector applies its filter to the RevitLinkInstance rather than to the elements inside the link, so a
/// category filter would discard every linked hit. Callers filter after the hit, where the linked element resolves.
/// </summary>
internal sealed class RayCaster
{
    /// <summary>Offset of the two extra rays that measure a hit face's plane, in feet (~76 mm).</summary>
    public const double DefaultProbeOffsetFt = 0.25;

    private readonly Document _doc;
    private readonly ReferenceIntersector _intersector;
    private readonly Dictionary<(long, long), RayTarget> _targets = new();

    public RayCaster(Document doc, View3D view, bool includeLinks)
    {
        _doc = doc;
        View = view;
        _intersector = new ReferenceIntersector(view) { TargetType = FindReferenceTarget.Face };
        _intersector.FindReferencesInRevitLinks = includeLinks;
    }

    public Document Document => _doc;

    /// <summary>The 3D view the rays are cast in.</summary>
    public View3D View { get; }

    /// <summary>Every face reference along the ray, in Revit's order; empty when Revit refuses the cast.</summary>
    public IList<ReferenceWithContext> Find(XYZ origin, XYZ direction)
    {
        try { return _intersector.Find(origin, direction) ?? (IList<ReferenceWithContext>)Array.Empty<ReferenceWithContext>(); }
        catch { return Array.Empty<ReferenceWithContext>(); }
    }

    /// <summary>
    /// The nearest hit with <paramref name="minDistanceFt"/> &lt; distance ≤ <paramref name="maxDistanceFt"/> (hits
    /// behind the origin never count) that <paramref name="exclude"/> does not rule out. Exclusion is checked only for
    /// hits nearer than the best so far, so expensive checks run rarely.
    /// </summary>
    public ReferenceWithContext? NearestHit(
        XYZ origin, XYZ direction, double maxDistanceFt, Func<Reference, bool>? exclude = null, double minDistanceFt = -1)
    {
        ReferenceWithContext? best = null;
        foreach (var hit in Find(origin, direction))
        {
            if (hit.Proximity < 0 || hit.Proximity <= minDistanceFt || hit.Proximity > maxDistanceFt) continue;
            if (best != null && hit.Proximity >= best.Proximity) continue;
            if (exclude != null && exclude(hit.GetReference())) continue;
            best = hit;
        }
        return best;
    }

    /// <summary>
    /// Every accepted hit along the ray within the distance window, nearest first, one per element (the nearest face
    /// of each). <paramref name="direction"/> must be a unit vector.
    /// </summary>
    public List<RayHit> HitsAlong(
        XYZ origin, XYZ direction, double maxDistanceFt, Func<RayTarget, bool>? accept = null, double minDistanceFt = -1)
    {
        var byElement = new Dictionary<(long, long), RayHit>();
        foreach (var hit in Find(origin, direction))
        {
            if (hit.Proximity < 0 || hit.Proximity <= minDistanceFt || hit.Proximity > maxDistanceFt) continue;
            var reference = hit.GetReference();
            var key = KeyOf(reference);
            if (byElement.TryGetValue(key, out var existing) && existing.DistanceFt <= hit.Proximity) continue;
            var target = ResolveTarget(reference);
            if (accept != null && !accept(target)) continue;
            byElement[key] = new RayHit
            {
                Reference = reference,
                Target = target,
                DistanceFt = hit.Proximity,
                Point = origin + direction * hit.Proximity
            };
        }
        return byElement.Values.OrderBy(h => h.DistanceFt).ToList();
    }

    /// <summary>Resolves what a hit reference points at, in the host model or inside a link. Cached per element.</summary>
    public RayTarget ResolveTarget(Reference reference)
    {
        (long, long) key;
        try { key = KeyOf(reference); }
        catch { return new RayTarget { Model = "Unknown" }; }
        if (_targets.TryGetValue(key, out var cached)) return cached;

        var target = new RayTarget();
        try
        {
            if (reference.LinkedElementId != ElementId.InvalidElementId)
            {
                target.LinkInstanceId = reference.ElementId.Value;
                var link = _doc.GetElement(reference.ElementId) as RevitLinkInstance;
                target.LinkName = link?.Name ?? string.Empty;
                target.ElementId = reference.LinkedElementId.Value;

                var linkDoc = link?.GetLinkDocument();
                var linked = linkDoc?.GetElement(reference.LinkedElementId);
                target.Category = linked?.Category?.Name ?? string.Empty;
                target.BuiltInCategory = GeometryConvert.BuiltInName(linked?.Category);
                target.Name = linked != null ? GeometryConvert.SafeName(linked) : string.Empty;
                target.Model = target.LinkName.Length > 0 ? target.LinkName : "Link";
                target.DocumentTitle = linkDoc?.Title ?? string.Empty;
            }
            else
            {
                target.ElementId = reference.ElementId.Value;
                var element = _doc.GetElement(reference.ElementId);
                target.Category = element?.Category?.Name ?? string.Empty;
                target.BuiltInCategory = GeometryConvert.BuiltInName(element?.Category);
                target.Name = element != null ? GeometryConvert.SafeName(element) : string.Empty;
                target.Model = "Host";
                target.DocumentTitle = _doc.Title;
            }
        }
        catch
        {
            target.Model = "Unknown";
        }
        _targets[key] = target;
        return target;
    }

    /// <summary>
    /// The face's own normal at <paramref name="hitPoint"/> (host coordinates), or null when the face cannot be read.
    /// For a face inside a link, <paramref name="linkGeometryReference"/> receives the same face as a reference built
    /// from the linked element's geometry — a second way in when Revit refuses the ray's reference for hosting.
    /// The sign is the face's own (outward of its solid), not turned towards the caster.
    /// </summary>
    public Vec3? FaceNormal(Reference reference, XYZ hitPoint, out Reference? linkGeometryReference)
    {
        linkGeometryReference = null;
        try
        {
            Face? face;
            var toModel = Transform.Identity;

            if (reference.LinkedElementId != ElementId.InvalidElementId)
            {
                if (_doc.GetElement(reference.ElementId) is not RevitLinkInstance link) return null;
                var linked = link.GetLinkDocument()?.GetElement(reference.LinkedElementId);
                if (linked == null) return null;
                face = linked.GetGeometryObjectFromReference(reference.CreateReferenceInLink()) as Face;
                toModel = link.GetTotalTransform();
                if (face?.Reference != null)
                    linkGeometryReference = face.Reference.CreateLinkReference(link);
            }
            else
            {
                face = _doc.GetElement(reference.ElementId)?.GetGeometryObjectFromReference(reference) as Face;
            }

            if (face == null) return null;

            var local = toModel.Inverse.OfPoint(hitPoint);
            var projection = face.Project(local);
            if (projection == null) return null;

            var normal = toModel.OfVector(face.ComputeNormal(projection.UVPoint));
            return normal.IsZeroLength() ? null : GeometryConvert.ToVec(normal.Normalize());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Measures a hit face's plane from three points on it: the first hit and two more rays offset perpendicular to
    /// the first by <paramref name="offsetFt"/>. <paramref name="probe"/> casts from the given origin and returns its
    /// hit point when it landed on the same surface, else null. The normal is turned towards the caster; null when a
    /// probe misses or the points are collinear (a curved, fragmented or narrow face).
    /// </summary>
    public static Vec3? MeasurePlane(XYZ origin, XYZ rayDirection, XYZ hitPoint, Func<XYZ, XYZ?> probe, double offsetFt = DefaultProbeOffsetFt)
    {
        var (u, v) = PerpendicularAxes(rayDirection);
        var first = probe(origin + u * offsetFt);
        var second = probe(origin + v * offsetFt);
        if (first == null || second == null) return null;

        return AlignmentMath.PlaneNormal(
            GeometryConvert.ToVec(hitPoint), GeometryConvert.ToVec(first), GeometryConvert.ToVec(second),
            GeometryConvert.ToVec(rayDirection.Negate()));
    }

    /// <summary>Identifies a face: two references to the same face give the same key.</summary>
    public string FaceKeyOf(Reference reference)
    {
        try
        {
            // The stable representation ends with the hit's UV for point-on-face references; the part before it
            // names the element and the face.
            var stable = reference.ConvertToStableRepresentation(_doc);
            var uv = stable.LastIndexOf("/", StringComparison.Ordinal);
            return uv > 0 && stable.IndexOf(":", uv, StringComparison.Ordinal) < 0 ? stable.Substring(0, uv) : stable;
        }
        catch
        {
            return $"{reference.ElementId.Value}|{reference.LinkedElementId.Value}";
        }
    }

    /// <summary>Two unit vectors perpendicular to <paramref name="direction"/> and to each other.</summary>
    public static (XYZ U, XYZ V) PerpendicularAxes(XYZ direction)
    {
        var seed = Math.Abs(direction.Z) > 0.9 ? XYZ.BasisX : XYZ.BasisZ;
        var u = direction.CrossProduct(seed);
        if (u.IsZeroLength())
            u = direction.CrossProduct(XYZ.BasisY);
        u = u.Normalize();
        return (u, direction.CrossProduct(u).Normalize());
    }

    /// <summary>The face normal turned towards the caster (against the ray), for classification and results.</summary>
    public static Vec3 TowardCaster(Vec3 normal, XYZ rayDirection)
    {
        var toward = GeometryConvert.ToVec(rayDirection.Negate());
        return normal.Dot(toward) < 0 ? normal.Negated() : normal;
    }

    /// <summary>The result DTO of a hit. <paramref name="distanceMm"/> overrides the distance along the ray.</summary>
    public static HitDto ToDto(RayHit hit, Vec3? normal = null, string? surfaceKind = null, string? normalSource = null, double? distanceMm = null) => new()
    {
        ElementId = hit.Target.ElementId,
        Category = hit.Target.Category,
        Name = hit.Target.Name,
        DocumentTitle = hit.Target.DocumentTitle,
        LinkInstanceId = hit.Target.LinkInstanceId,
        PointMm = GeometryConvert.Point(hit.Point),
        Normal = normal.HasValue ? DirectionDto.From(normal.Value) : null,
        DistanceMm = GeometryUnits.RoundMm(distanceMm ?? GeometryUnits.FtToMm(hit.DistanceFt)),
        SurfaceKind = surfaceKind,
        NormalSource = normalSource
    };

    /// <summary>
    /// The 3D view for ray casting, chosen like the align tools (<see cref="AlignmentRequest.ResolveSearchView"/>):
    /// <c>searchViewId</c>, else the active 3D view, else the least clipped 3D view (isometric without a section box, then with one, then perspective). A missing view is answered
    /// with status <see cref="GeometryStatus.No3DView"/>; a fallback view and an active section box are warned about.
    /// </summary>
    public static View3D? ResolveView(
        Document doc, UIDocument? uidoc, McpToolRequest request, List<string> warnings, out McpToolResult? failure)
    {
        failure = null;
        var view = AlignmentRequest.ResolveSearchView(doc, uidoc, request.Arguments, warnings, out var error);
        if (view != null)
        {
            var explicitView = ToolArguments.GetLong(request.Arguments, "searchViewId") > 0;
            if (!explicitView && !(uidoc?.ActiveView is View3D active && active.Id == view.Id))
                warnings.Add($"No 3D view is active; the rays use 3D view '{view.Name}' ({view.Id.Value}), the least clipped one " +
                             "(isometric without a section box first). Only what is visible in it can be hit — pass searchViewId to choose another.");
            return view;
        }

        var searched = ToolArguments.GetLong(request.Arguments, "searchViewId") > 0;
        failure = new McpToolResult
        {
            RequestId = request.RequestId,
            Success = false,
            Status = searched ? "validation_failed" : GeometryStatus.No3DView,
            Message = searched
                ? error ?? "searchViewId is not a usable 3D view."
                : $"'{doc.Title}' has no 3D view, and ray casting needs one. Create a 3D view (in Revit: View > 3D View, " +
                  "or with the connector's view creation tool), then call again — optionally with searchViewId."
        };
        return null;
    }

    /// <summary>The view the rays used, for every ray tool's result.</summary>
    public object DescribeView()
    {
        var sectionBox = false;
        try { sectionBox = View.IsSectionBoxActive; } catch { }
        return new { id = View.Id.Value, name = View.Name, sectionBoxActive = sectionBox };
    }

    private static (long, long) KeyOf(Reference reference) => (reference.ElementId.Value, reference.LinkedElementId.Value);
}
