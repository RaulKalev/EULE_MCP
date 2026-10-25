using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Geometry;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Placement;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Geometry;

/// <summary>revit_check_line_of_sight (#107): whether a straight line between two points is blocked, by what and where.</summary>
public sealed class CheckLineOfSightTool : IRevitMcpTool
{
    public string Name => "revit_check_line_of_sight";

    /// <summary>Hits closer to the end point than this do not block: the line reaches its target (feet, 1 mm).</summary>
    private const double EndToleranceFt = 1.0 / GeometryUnits.MmPerFoot;

    /// <summary>Hits listed in the result.</summary>
    private const int MaxListedHits = 10;

    public string Description =>
        "Checks whether the straight line between two points is clear, by casting one ray in a 3D view through the " +
        "host and loaded links (includeLinks, default true). from/to are points {x,y,z} in host mm, or fromElementId/" +
        "toElementId (bounding box centres; both elements are ignored, e.g. the camera and the door it should see). " +
        "blockingCategories[] default: walls, floors, ceilings, roofs, doors, structural columns and framing, curtain " +
        "panels, generic models; ignoreElementIds[] skips host elements. A hit nearer than the end point by more than " +
        "1 mm on a blocking category blocks the line. Returns clear, distanceMm, blockedBy (elementId, category, " +
        "linkInstanceId, pointMm, distanceMm) and the first 10 hits with blocking flags. The result names the 3D view " +
        "used; only geometry visible in it is found (searchViewId to choose). Status no_3d_view_for_raycast when the " +
        "model has no 3D view.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return Task.FromResult(documentFailure!);
        var doc = target.Document;
        var uidoc = ToolDocument.UiDocumentOf(uiapp, target);
        var args = request.Arguments;
        var warnings = new List<string>();

        var hasFrom = GeometryArgs.IsGiven(args, "from") || ToolArguments.GetLong(args, "fromElementId") > 0;
        var hasTo = GeometryArgs.IsGiven(args, "to") || ToolArguments.GetLong(args, "toElementId") > 0;
        var tooBroad = GeometryQueryGuard.Validate(request.RequestId, hasCategoryFilter: false, hasElementIds: false, hasSpatialScope: hasFrom && hasTo,
            "revit_check_line_of_sight needs both ends: from and to (points in mm) or fromElementId and toElementId.",
            "Pass from {x, y, z} and to {x, y, z} in host mm", "or fromElementId / toElementId");
        if (tooBroad != null) return Task.FromResult(tooBroad);

        if (!GeometryToolSupport.TryResolveOrigin(doc, args, "from", "fromElementId", out var fromMm, out var fromElement, out _, out var fromError))
            return Task.FromResult(GeometryToolSupport.Fail(request, fromError!));
        if (!GeometryToolSupport.TryResolveOrigin(doc, args, "to", "toElementId", out var toMm, out var toElement, out _, out var toError))
            return Task.FromResult(GeometryToolSupport.Fail(request, toError!));

        var line = toMm.Minus(fromMm);
        var lengthMm = line.Length;
        if (lengthMm < 1.0)
            return Task.FromResult(GeometryToolSupport.Fail(request, "from and to are less than 1 mm apart; there is no line to check."));
        if (lengthMm > AlignmentRequest.MaxSearchRadiusMm)
            warnings.Add($"The line is {lengthMm / 1000:0.0} m long; ray casting over long distances only sees what the 3D view shows.");

        var blocking = GeometryToolSupport.Strings(args, "blockingCategories");
        var blockingSet = blocking.Length > 0
            ? new HashSet<string>(blocking, StringComparer.OrdinalIgnoreCase)
            : GeometryCategories.DefaultBlocking();
        var ignore = GeometryToolSupport.IgnoreSet(ToolArguments.GetLongArray(args, "ignoreElementIds"), fromElement, includeDependents: false);
        if (toElement != null) ignore.Add(toElement.Id.Value);
        var includeLinks = ToolArguments.GetBool(args, "includeLinks", true);

        var view = RayCaster.ResolveView(doc, uidoc, request, warnings, out var viewFailure);
        if (view == null) return Task.FromResult(viewFailure!);
        var caster = new RayCaster(doc, view, includeLinks);

        var origin = GeometryConvert.FromMm(fromMm);
        var direction = GeometryConvert.ToXyz(line).Normalize();
        var distanceFt = GeometryUnits.MmToFt(lengthMm);
        var hits = caster.HitsAlong(origin, direction, distanceFt, GeometryToolSupport.Accept(ignore, Array.Empty<string>()));

        RayHit? blockedBy = null;
        var listed = new List<object>();
        foreach (var hit in hits)
        {
            var blocks = hit.DistanceFt < distanceFt - EndToleranceFt &&
                         GeometryCategories.Matches(blockingSet, hit.Target.BuiltInCategory, hit.Target.Category);
            if (blocks && blockedBy == null) blockedBy = hit;
            if (listed.Count < MaxListedHits)
            {
                listed.Add(new
                {
                    blocking = blocks,
                    elementId = hit.Target.ElementId,
                    category = hit.Target.Category,
                    name = hit.Target.Name,
                    documentTitle = hit.Target.DocumentTitle,
                    linkInstanceId = hit.Target.LinkInstanceId,
                    pointMm = GeometryConvert.Point(hit.Point),
                    distanceMm = GeometryToolSupport.Mm(hit.DistanceFt)
                });
            }
        }

        var clear = blockedBy == null;
        return Task.FromResult(GeometryToolSupport.Ok(request, sw,
            clear
                ? $"Clear: nothing in the blocking categories along the {GeometryUnits.RoundMm(lengthMm):0} mm line" +
                  (hits.Count > 0 ? $" ({hits.Count} other hit(s) do not block)." : ".")
                : $"Blocked after {GeometryToolSupport.Mm(blockedBy!.DistanceFt):0} mm by {blockedBy.Target.Category} {blockedBy.Target.ElementId}" +
                  (blockedBy.Target.LinkInstanceId.HasValue ? $" in link {blockedBy.Target.LinkInstanceId}" : string.Empty) + ".",
            new
            {
                units = "mm, host internal coordinates",
                clear,
                fromMm = PointMmDto.FromMm(fromMm),
                toMm = PointMmDto.FromMm(toMm),
                distanceMm = GeometryUnits.RoundMm(lengthMm),
                direction = DirectionDto.From(line),
                view = caster.DescribeView(),
                includeLinks,
                blockingCategories = blocking.Length > 0 ? blocking : GeometryCategories.DefaultBlockingNames,
                ignoredElementIds = ignore.OrderBy(i => i).ToList(),
                blockedBy = blockedBy == null ? null : RayCaster.ToDto(blockedBy, RayCaster.TowardCaster(
                    caster.FaceNormal(blockedBy.Reference, blockedBy.Point, out _) ?? GeometryConvert.ToVec(direction.Negate()), direction)),
                hitCount = hits.Count,
                hits = listed
            },
            warnings));
    }
}
