using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>
/// Geometry queries (#107, docs/geometry-queries.md): element geometry, elements in a box or near a point, nearest
/// surface, line of sight and clearance. All read-only; every output is host internal mm (0.1 mm).
/// </summary>
internal sealed partial class RevitMcpTools
{
    private const string GeometryPointDescription = "Point in host internal mm: {\"x\":..,\"y\":..,\"z\":..} or [x, y, z].";

    private const string GeometryDirectionsDescription =
        "Ray directions: a set (axes = +x,-x,+y,-y,up,down; horizontal = compass ring; up; down; all; local = the element's front/back/left/right/up/down), " +
        "labels like \"+x,up\" or \"front\", or vectors [{\"x\":1,\"y\":0,\"z\":0}] (max 32).";

    private const string GeometryViewDescription =
        "3D view the rays are cast in (only what is visible in it is found). Default: the active 3D view, else the least clipped 3D view (isometric without a section box first); the result names the view used and any linked models hidden in it.";

    [McpServerTool(Name = "revit_get_element_geometry", ReadOnly = true),
     Description("Geometry of up to 100 elements by id, host mm (0.1 mm): location (point + rotationDeg, or curve start/end/length), bbox {min, max, sizeMm, centerMm} (owner view box for view-specific elements), facing/hand orientation with mirrored and flipped flags, host {id, category} (a face host in a link resolves to the linked element), level, room (last phase), ownerViewId, isPinned, groupId. linkInstanceId reads ids inside that link, transformed to host coordinates. includeSolidsSummary adds volume (mm³) and face count. Without elementIds the call is refused (geometry_query_too_broad).")]
    public async Task<string> GetElementGeometry(
        [Description("Element ids (max 100). Ids inside a link need linkInstanceId.")] long[] elementIds,
        [Description("Link instance whose document the ids belong to; 0 = host model.")] long linkInstanceId = 0,
        [Description("Add the first solid's volume (mm³) and face count. Default false.")] bool includeSolidsSummary = false,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["elementIds"] = elementIds ?? [],
            ["linkInstanceId"] = linkInstanceId,
            ["includeSolidsSummary"] = includeSolidsSummary,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync("revit_get_element_geometry", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_find_elements_in_box", ReadOnly = true),
     Description("Elements whose bounding box intersects (mode=intersects) or lies inside (mode=inside) a box in host mm: box {min:{x,y,z}, max:{x,y,z}}, or aroundElementId grown by paddingMm, or roomId (the room's bounding box; roomLinkInstanceId for a room in a link). categories[] is required unless maxResults ≤ 200 (default 200, max 2000); levels[] narrows by level name. includeLinks searches loaded links too (linkNameFilters[] by name); linked rows carry linkInstanceId. Rows: elementId, category, name, typeName, bbox, distanceToBoxCenterMm, nearest the box centre first. Unscoped calls are refused (geometry_query_too_broad).")]
    public async Task<string> FindElementsInBox(
        [Description("Box in host mm: {\"min\":{\"x\":..,\"y\":..,\"z\":..},\"max\":{..}} (corners in any order).")] object? box = null,
        [Description("Use this host element's bounding box instead of box (grow it with paddingMm). The element itself is not listed.")] long aroundElementId = 0,
        [Description("Grow (positive) or shrink (negative) the box on every side, mm.")] double paddingMm = 0,
        [Description("Use this room's bounding box instead of box.")] long roomId = 0,
        [Description("Link instance holding roomId (rooms in a linked architecture model); 0 = host.")] long roomLinkInstanceId = 0,
        [Description("Categories to find, e.g. [\"Lighting Fixtures\"]. Required when maxResults > 200.")] string[]? categories = null,
        [Description("intersects (default) or inside.")] string mode = "intersects",
        [Description("Also search loaded links. Default false.")] bool includeLinks = false,
        [Description("Only links whose name contains one of these (with includeLinks).")] string[]? linkNameFilters = null,
        [Description("Only elements on these levels (names).")] string[]? levels = null,
        [Description("Rows to return, nearest the box centre first (default 200, max 2000).")] int maxResults = 200,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["box"] = ToJToken(box),
            ["aroundElementId"] = aroundElementId,
            ["paddingMm"] = paddingMm,
            ["roomId"] = roomId,
            ["roomLinkInstanceId"] = roomLinkInstanceId,
            ["categories"] = categories ?? [],
            ["mode"] = mode,
            ["includeLinks"] = includeLinks,
            ["linkNameFilters"] = linkNameFilters ?? [],
            ["levels"] = levels ?? [],
            ["maxResults"] = maxResults,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync("revit_find_elements_in_box", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_find_elements_near", ReadOnly = true),
     Description("Elements within radiusMm (max 50000) of a point in host mm or of an element (elementId: its bounding box centre), nearest first. distanceMm is from the point to the nearest point of each element's bounding box (0 inside it); centerDistanceMm to the box centre. categories[] is required unless maxResults ≤ 200 (default 200, max 2000). includeLinks searches loaded links too (linkNameFilters[] by name); linked rows carry linkInstanceId. E.g. luminaires within 3 m of a panel: elementId=<panel>, radiusMm=3000, categories=[\"Lighting Fixtures\"]. Unscoped calls are refused (geometry_query_too_broad).")]
    public async Task<string> FindElementsNear(
        [Description("Search radius, mm (max 50000).")] double radiusMm,
        [Description(GeometryPointDescription)] object? point = null,
        [Description("Host element to measure from instead of point (its bounding box centre). It is not listed.")] long elementId = 0,
        [Description("Categories to find, e.g. [\"Lighting Fixtures\"]. Required when maxResults > 200.")] string[]? categories = null,
        [Description("Also search loaded links. Default false.")] bool includeLinks = false,
        [Description("Only links whose name contains one of these (with includeLinks).")] string[]? linkNameFilters = null,
        [Description("Rows to return, nearest first (default 200, max 2000).")] int maxResults = 200,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["radiusMm"] = radiusMm,
            ["point"] = ToJToken(point),
            ["elementId"] = elementId,
            ["categories"] = categories ?? [],
            ["includeLinks"] = includeLinks,
            ["linkNameFilters"] = linkNameFilters ?? [],
            ["maxResults"] = maxResults,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync("revit_find_elements_near", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_find_nearest_surface", ReadOnly = true),
     Description("Nearest wall / ceiling / floor / roof / any surface from a point in host mm or an element (elementId: its bounding box centre; the element is ignored), by ray casting in a 3D view through the host and loaded links. Surfaces are classified by face normal (a Roofs category makes a roof). Returns the best hit per requested surface — distanceMm is perpendicular to the face when its foot lies on it, else along the ray — and the nearest hit per direction (elementId, category, linkInstanceId, pointMm, normal, surfaceKind). Default 16 horizontal directions, maxDistanceMm 10000. E.g. is a detector ≥ 500 mm from every wall: surfaces=[\"wall\"], compare best.wall.hit.distanceMm. Status no_3d_view_for_raycast when the model has no 3D view.")]
    public async Task<string> FindNearestSurface(
        [Description(GeometryPointDescription)] object? point = null,
        [Description("Host element to search from instead of point (its bounding box centre).")] long elementId = 0,
        [Description("Surface kinds: wall, ceiling, floor, roof, any (default [\"any\"]).")] string[]? surfaces = null,
        [Description(GeometryDirectionsDescription + " Default horizontal.")] object? directions = null,
        [Description("Compass directions in the horizontal set (4-32, default 16).")] int horizontalSamples = 16,
        [Description("How far to look, mm (default 10000, max 50000).")] double maxDistanceMm = 10000,
        [Description("Only elements of these categories count as surfaces (default: any). Linked IFC elements often have generic categories.")] string[]? categories = null,
        [Description("Host elements the rays pass through.")] long[]? ignoreElementIds = null,
        [Description("Also hit loaded links. Default true.")] bool includeLinks = true,
        [Description("How far a face may tilt and still count as wall/ceiling/floor, degrees (1-44, default 30).")] double angleToleranceDegrees = 30,
        [Description(GeometryViewDescription)] long searchViewId = 0,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["point"] = ToJToken(point),
            ["elementId"] = elementId,
            ["surfaces"] = surfaces ?? [],
            ["directions"] = ToJToken(directions),
            ["horizontalSamples"] = horizontalSamples,
            ["maxDistanceMm"] = maxDistanceMm,
            ["categories"] = categories ?? [],
            ["ignoreElementIds"] = ignoreElementIds ?? [],
            ["includeLinks"] = includeLinks,
            ["angleToleranceDegrees"] = angleToleranceDegrees,
            ["searchViewId"] = searchViewId,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync("revit_find_nearest_surface", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_check_line_of_sight", ReadOnly = true),
     Description("Whether the straight line between two points in host mm is blocked, by what and where: one ray in a 3D view through the host and loaded links. from/to, or fromElementId/toElementId (bounding box centres; both elements are ignored — e.g. can camera C see door D: fromElementId=C, toElementId=D). blockingCategories[] default: walls, floors, ceilings, roofs, doors, structural columns/framing, curtain panels, generic models. Returns clear, distanceMm, blockedBy (elementId, category, linkInstanceId, pointMm, distanceMm) and the first 10 hits. Status no_3d_view_for_raycast when the model has no 3D view.")]
    public async Task<string> CheckLineOfSight(
        [Description("Start point. " + GeometryPointDescription)] object? from = null,
        [Description("End point. " + GeometryPointDescription)] object? to = null,
        [Description("Host element at the start instead of from (its bounding box centre; ignored by the ray).")] long fromElementId = 0,
        [Description("Host element at the end instead of to (its bounding box centre; ignored by the ray).")] long toElementId = 0,
        [Description("Categories that block the line (default: walls, floors, ceilings, roofs, doors, structural columns and framing, curtain panels, generic models).")] string[]? blockingCategories = null,
        [Description("Host elements the ray passes through.")] long[]? ignoreElementIds = null,
        [Description("Also hit loaded links. Default true.")] bool includeLinks = true,
        [Description(GeometryViewDescription)] long searchViewId = 0,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["from"] = ToJToken(from),
            ["to"] = ToJToken(to),
            ["fromElementId"] = fromElementId,
            ["toElementId"] = toElementId,
            ["blockingCategories"] = blockingCategories ?? [],
            ["ignoreElementIds"] = ignoreElementIds ?? [],
            ["includeLinks"] = includeLinks,
            ["searchViewId"] = searchViewId,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync("revit_check_line_of_sight", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_measure_clearance", ReadOnly = true),
     Description("Free distance around an element or a point in host mm, per direction, by ray casting in a 3D view through the host and loaded links. For an element the rays start on its bounding box (face centres for the axis directions), so freeMm is the gap to what it faces; the element, its hosted children and ignoreElementIds[] are ignored. directions default axes (+x, -x, +y, -y, up, down); local uses the element's front (facing)/back/left/right. Returns per direction freeMm (null = nothing within maxDistanceMm, default 5000) and the blocking element, plus minFreeMm and its direction. E.g. front clearance of a panel: elementId=<panel>, directions=\"front\". Status no_3d_view_for_raycast when the model has no 3D view.")]
    public async Task<string> MeasureClearance(
        [Description("Host element to measure around (rays start on its bounding box).")] long elementId = 0,
        [Description("Point to measure from instead of an element. " + GeometryPointDescription)] object? point = null,
        [Description(GeometryDirectionsDescription + " Default axes.")] object? directions = null,
        [Description("How far to look, mm (default 5000, max 50000).")] double maxDistanceMm = 5000,
        [Description("More host elements the rays pass through (the element and its hosted children always are).")] long[]? ignoreElementIds = null,
        [Description("Only elements of these categories block (default: any).")] string[]? categories = null,
        [Description("Also hit loaded links. Default true.")] bool includeLinks = true,
        [Description(GeometryViewDescription)] long searchViewId = 0,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["elementId"] = elementId,
            ["point"] = ToJToken(point),
            ["directions"] = ToJToken(directions),
            ["maxDistanceMm"] = maxDistanceMm,
            ["ignoreElementIds"] = ignoreElementIds ?? [],
            ["categories"] = categories ?? [],
            ["includeLinks"] = includeLinks,
            ["searchViewId"] = searchViewId,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync("revit_measure_clearance", args, cancellationToken));
    }
}
