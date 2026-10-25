using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Geometry;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Placement;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Geometry;

/// <summary>revit_find_elements_near (#107): elements within a radius of a point or an element, nearest first.</summary>
public sealed class FindElementsNearTool : IRevitMcpTool
{
    public string Name => "revit_find_elements_near";

    public const double MaxRadiusMm = 50000;

    public string Description =>
        "Finds elements within radiusMm (max 50000) of a point {x,y,z} in host mm or of an element (elementId: its " +
        "bounding box centre), sorted by distance. The distance is from the point to the nearest point of each " +
        "element's bounding box (0 when the point is inside it); centerDistanceMm is to the box centre. " +
        "categories[] is required unless maxResults is 200 or less (default 200, max 2000). includeLinks also searches " +
        "loaded links (linkNameFilters[] picks them by name); linked rows carry linkInstanceId. The element itself is " +
        "not listed. Unscoped calls are refused with status geometry_query_too_broad.";

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

        var categories = GeometryToolSupport.Strings(args, "categories");
        var linkNameFilters = GeometryToolSupport.Strings(args, "linkNameFilters");
        var includeLinks = ToolArguments.GetBool(args, "includeLinks");
        var hasPoint = GeometryArgs.IsGiven(args, "point");
        var elementId = ToolArguments.GetLong(args, "elementId");

        var tooBroad = GeometryQueryGuard.Validate(request.RequestId, hasCategoryFilter: false, hasElementIds: elementId > 0, hasSpatialScope: hasPoint,
            "revit_find_elements_near needs a centre: point {x,y,z} in mm or elementId.",
            "Pass point {x, y, z} in host mm", "or elementId (its bounding box centre is used)");
        if (tooBroad != null) return Task.FromResult(tooBroad);

        var radius = ToolArguments.GetDouble(args, "radiusMm");
        if (double.IsNaN(radius) || radius <= 0)
            return Task.FromResult(GeometryToolSupport.Fail(request, "radiusMm must be a positive distance in mm (max 50000)."));
        if (radius > MaxRadiusMm)
        {
            warnings.Add($"radiusMm {radius:0} exceeds the cap; using {MaxRadiusMm:0}.");
            radius = MaxRadiusMm;
        }

        var maxResults = GeometryQueryGuard.ResolveMaxResults(ToolArguments.GetInt(args, "maxResults", GeometryQueryGuard.DefaultMaxResults),
            categories.Length > 0, warnings, out var tooManyRows);
        if (tooManyRows)
            return Task.FromResult(GeometryQueryGuard.Refuse(request.RequestId,
                $"maxResults {maxResults} without categories is too broad; without categories at most {GeometryQueryGuard.DefaultMaxResults} rows are returned.",
                "Add categories[] (e.g. [\"Lighting Fixtures\"])", $"or keep maxResults at {GeometryQueryGuard.DefaultMaxResults} or less"));

        if (!GeometryToolSupport.TryResolveOrigin(doc, args, "point", "elementId", out var center, out var element, out _, out var originError))
            return Task.FromResult(GeometryToolSupport.Fail(request, originError!));

        var exclude = new HashSet<long>();
        if (element != null) exclude.Add(element.Id.Value);

        var models = GeometryToolSupport.Models(doc, includeLinks, linkNameFilters, warnings);
        var prefilter = BoxMath.FromPointRadius(center, radius);
        var found = GeometryToolSupport.Collect(models, prefilter, BoxMath.ModeIntersects, categories, Array.Empty<string>(), exclude, warnings, cancellationToken);
        var rows = found
            .Select(f => new { Found = f, Distance = BoxMath.DistancePointToBox(center, f.HostBox), CenterDistance = BoxMath.Distance(center, f.HostBox.Center) })
            .Where(r => r.Distance <= radius)
            .OrderBy(r => r.Distance).ThenBy(r => r.CenterDistance)
            .ToList();
        var listed = rows.Take(maxResults).ToList();
        if (rows.Count > listed.Count)
            warnings.Add($"{rows.Count} element(s) within {radius:0} mm; listing the {listed.Count} nearest (maxResults).");

        return Task.FromResult(GeometryToolSupport.Ok(request, sw,
            $"{rows.Count} element(s) within {radius:0} mm of {(element != null ? $"element {element.Id.Value}" : "the point")}" +
            (rows.Count > 0 ? $"; nearest {GeometryUnits.RoundMm(rows[0].Distance):0.#} mm." : "."),
            new
            {
                units = "mm, host internal coordinates",
                centerMm = PointMmDto.FromMm(center),
                centerSource = element != null ? $"element {element.Id.Value} bounding box centre" : "point",
                radiusMm = GeometryUnits.RoundMm(radius),
                categories,
                total = rows.Count,
                returned = listed.Count,
                truncated = rows.Count > listed.Count,
                elements = listed.Select(r => new
                {
                    elementId = r.Found.Element.Id.Value,
                    category = r.Found.Element.Category?.Name ?? string.Empty,
                    name = GeometryConvert.SafeName(r.Found.Element),
                    typeName = GeometryToolSupport.TypeName(r.Found.Element),
                    documentTitle = r.Found.Model.Doc.Title,
                    linkInstanceId = r.Found.Model.LinkInstanceId,
                    link = r.Found.Model.Describe(),
                    distanceMm = GeometryUnits.RoundMm(r.Distance),
                    centerDistanceMm = GeometryUnits.RoundMm(r.CenterDistance),
                    bbox = BoxMmDto.FromMm(r.Found.HostBox)
                }).ToList()
            },
            warnings));
    }
}
