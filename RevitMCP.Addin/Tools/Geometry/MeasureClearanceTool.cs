using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Geometry;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Placement;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Geometry;

/// <summary>revit_measure_clearance (#107): the free distance around an element or a point, per direction.</summary>
public sealed class MeasureClearanceTool : IRevitMcpTool
{
    public string Name => "revit_measure_clearance";

    public const double DefaultMaxDistanceMm = 5000;

    /// <summary>How far behind the start point each ray begins, so a face lying exactly on it is still met (feet, ~15 mm).</summary>
    private const double BackOffFt = 0.05;

    public string Description =>
        "Measures the free distance around an element (elementId) or a point {x,y,z} in host mm by casting rays in a " +
        "3D view through the host and loaded links (includeLinks, default true). For an element each ray starts on " +
        "its bounding box (the face centre for the axis directions), so freeMm is the gap to what it faces; the " +
        "element, what depends on it (hosted children, sub-components) and ignoreElementIds[] are ignored. " +
        "directions: axes (default: +x, -x, +y, -y, up, down), local (front = facing, back, left, right, up, down), " +
        "horizontal, all, labels like \"front,up\", or vectors [{x,y,z}] (max 32). maxDistanceMm default 5000; a " +
        "direction with nothing within it has freeMm null. categories[] restricts the blockers. Returns per " +
        "direction freeMm and the blocking element (elementId, category, linkInstanceId, pointMm), plus minFreeMm " +
        "and its direction. The bounding box approximates the element; the result names the 3D view used (searchViewId " +
        "to choose). Status no_3d_view_for_raycast when the model has no 3D view.";

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

        var hasPoint = GeometryArgs.IsGiven(args, "point");
        var elementId = ToolArguments.GetLong(args, "elementId");
        var tooBroad = GeometryQueryGuard.Validate(request.RequestId, hasCategoryFilter: false, hasElementIds: elementId > 0, hasSpatialScope: hasPoint,
            "revit_measure_clearance needs an element (elementId) or a point {x,y,z} in mm.",
            "Pass elementId (rays start on its bounding box)", "or point {x, y, z} in host mm");
        if (tooBroad != null) return Task.FromResult(tooBroad);

        if (!GeometryToolSupport.TryResolveOrigin(doc, args, "point", "elementId", out var originMm, out var element, out var elementBox, out var originError))
            return Task.FromResult(GeometryToolSupport.Fail(request, originError!));

        Vec3? facing = null, hand = null;
        if (element is FamilyInstance instance)
        {
            try { facing = GeometryConvert.ToVec(instance.FacingOrientation); hand = GeometryConvert.ToVec(instance.HandOrientation); }
            catch { }
        }
        args.TryGetValue("directions", out var rawDirections);
        var directions = ClearanceMath.Resolve(rawDirections, ClearanceMath.SetAxes, 8, facing, hand, out var directionError);
        if (directions == null)
            return Task.FromResult(GeometryQueryGuard.Refuse(request.RequestId, directionError!, "Use a direction set or at most 32 vectors."));

        var maxDistanceMm = GeometryToolSupport.ClampDistanceMm(ToolArguments.GetDouble(args, "maxDistanceMm", DefaultMaxDistanceMm), DefaultMaxDistanceMm, "maxDistanceMm", warnings);
        var includeLinks = ToolArguments.GetBool(args, "includeLinks", true);
        var categories = GeometryToolSupport.Strings(args, "categories");
        var ignore = GeometryToolSupport.IgnoreSet(ToolArguments.GetLongArray(args, "ignoreElementIds"), element, includeDependents: true);

        var view = RayCaster.ResolveView(doc, uidoc, request, warnings, out var viewFailure);
        if (view == null) return Task.FromResult(viewFailure!);
        var caster = new RayCaster(doc, view, includeLinks);
        var accept = GeometryToolSupport.Accept(ignore, categories);
        var maxFt = GeometryUnits.MmToFt(maxDistanceMm);

        var results = new List<object>();
        var measured = new List<(string Label, double? FreeMm)>();
        foreach (var direction in directions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var startMm = element != null && elementBox.HasValue ? ClearanceMath.ExitPoint(elementBox.Value, direction.Direction) : originMm;
            var ray = GeometryConvert.ToXyz(direction.Direction).Normalize();
            var start = GeometryConvert.FromMm(startMm) - ray * BackOffFt;
            var hit = caster.HitsAlong(start, ray, maxFt + BackOffFt, accept).FirstOrDefault();

            double? freeMm = hit == null ? null : GeometryToolSupport.Mm(ClearanceMath.FreeDistance(hit.DistanceFt, BackOffFt));
            measured.Add((direction.Label, freeMm));
            results.Add(new
            {
                direction = direction.Label,
                vector = DirectionDto.From(direction.Direction),
                startMm = PointMmDto.FromMm(startMm),
                freeMm,
                clearBeyondMm = hit == null ? GeometryUnits.RoundMm(maxDistanceMm) : (double?)null,
                hit = hit == null ? null : RayCaster.ToDto(hit, normal: null, distanceMm: freeMm)
            });
        }

        var minimum = ClearanceMath.Minimum(measured);
        return Task.FromResult(GeometryToolSupport.Ok(request, sw,
            minimum == null
                ? $"Nothing within {maxDistanceMm:0} mm in {directions.Count} direction(s)."
                : $"Smallest clearance {minimum.Value.FreeMm:0.#} mm ({minimum.Value.Label}); " +
                  $"{measured.Count(m => m.FreeMm == null)} of {directions.Count} direction(s) clear beyond {maxDistanceMm:0} mm.",
            new
            {
                units = "mm, host internal coordinates",
                originMm = PointMmDto.FromMm(originMm),
                originSource = element != null ? $"element {element.Id.Value} bounding box" : "point",
                elementBox = element != null && elementBox.HasValue ? BoxMmDto.FromMm(elementBox.Value) : null,
                view = caster.DescribeView(),
                includeLinks,
                maxDistanceMm = GeometryUnits.RoundMm(maxDistanceMm),
                ignoredElementIds = ignore.OrderBy(i => i).Take(50).ToList(),
                minFreeMm = minimum?.FreeMm,
                minDirection = minimum?.Label,
                directions = results
            },
            warnings));
    }
}
