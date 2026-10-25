using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Geometry;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Geometry;

/// <summary>revit_find_elements_in_box (#107): elements whose bounding box meets a box, in the host and in links.</summary>
public sealed class FindElementsInBoxTool : IRevitMcpTool
{
    public string Name => "revit_find_elements_in_box";

    public string Description =>
        "Finds elements whose bounding box intersects (mode=intersects, default) or lies inside (mode=inside) a box, " +
        "in host internal mm. The box is box {min:{x,y,z}, max:{x,y,z}}, or aroundElementId grown by paddingMm, or " +
        "roomId (the room's bounding box; roomLinkInstanceId for a room in a link). categories[] is required unless " +
        "maxResults is 200 or less (default 200, max 2000); levels[] narrows by level name. includeLinks also searches " +
        "loaded links (linkNameFilters[] picks them by name); linked rows carry linkInstanceId. Rows: elementId, " +
        "category, name, typeName, bbox, distanceToBoxCenterMm, sorted by that distance. Unscoped calls are refused " +
        "with status geometry_query_too_broad.";

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
        var levels = GeometryToolSupport.Strings(args, "levels");
        var linkNameFilters = GeometryToolSupport.Strings(args, "linkNameFilters");
        var mode = ToolArguments.GetString(args, "mode", BoxMath.ModeIntersects).Trim().ToLowerInvariant();
        if (mode.Length == 0) mode = BoxMath.ModeIntersects;
        if (!BoxMath.IsKnownMode(mode))
            return Task.FromResult(GeometryToolSupport.Fail(request, $"Unknown mode '{mode}'. Valid: intersects, inside."));
        var includeLinks = ToolArguments.GetBool(args, "includeLinks");

        var hasBox = GeometryArgs.IsGiven(args, "box");
        var aroundId = ToolArguments.GetLong(args, "aroundElementId");
        var roomId = ToolArguments.GetLong(args, "roomId");
        var tooBroad = GeometryQueryGuard.Validate(request.RequestId, hasCategoryFilter: false, hasElementIds: aroundId > 0 || roomId > 0, hasSpatialScope: hasBox,
            "revit_find_elements_in_box needs a box: box, aroundElementId or roomId.",
            "Pass box {min:{x,y,z}, max:{x,y,z}} in mm", "or aroundElementId with paddingMm", "or roomId (roomLinkInstanceId for a room in a link)");
        if (tooBroad != null) return Task.FromResult(tooBroad);

        var maxResults = GeometryQueryGuard.ResolveMaxResults(ToolArguments.GetInt(args, "maxResults", GeometryQueryGuard.DefaultMaxResults),
            categories.Length > 0, warnings, out var tooManyRows);
        if (tooManyRows)
            return Task.FromResult(GeometryQueryGuard.Refuse(request.RequestId,
                $"maxResults {maxResults} without categories is too broad; without categories at most {GeometryQueryGuard.DefaultMaxResults} rows are returned.",
                "Add categories[] (e.g. [\"Lighting Fixtures\"])", $"or keep maxResults at {GeometryQueryGuard.DefaultMaxResults} or less"));

        if (!TryResolveBox(doc, args, request, hasBox, aroundId, roomId, warnings, out var queryBox, out var source, out var exclude, out var failure))
            return Task.FromResult(failure!);

        var models = GeometryToolSupport.Models(doc, includeLinks, linkNameFilters, warnings);
        var found = GeometryToolSupport.Collect(models, queryBox, mode, categories, levels, exclude, warnings, cancellationToken);
        var center = queryBox.Center;
        var rows = found
            .Select(f => new { Found = f, Distance = BoxMath.Distance(f.HostBox.Center, center) })
            .OrderBy(r => r.Distance)
            .ToList();
        var listed = rows.Take(maxResults).ToList();
        if (rows.Count > listed.Count)
            warnings.Add($"{rows.Count} element(s) matched; listing the {listed.Count} nearest the box centre (maxResults).");

        return Task.FromResult(GeometryToolSupport.Ok(request, sw,
            $"{rows.Count} element(s) {(mode == BoxMath.ModeInside ? "inside" : "intersecting")} the box" +
            $"{(includeLinks ? $" in the host and {models.Count - 1} link(s)" : " in the host model")}; {listed.Count} listed.",
            new
            {
                units = "mm, host internal coordinates",
                box = BoxMmDto.FromMm(queryBox),
                boxSource = source,
                mode,
                categories,
                levels,
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
                    bbox = BoxMmDto.FromMm(r.Found.HostBox),
                    distanceToBoxCenterMm = GeometryUnits.RoundMm(r.Distance)
                }).ToList()
            },
            warnings));
    }

    private static bool TryResolveBox(
        Document doc, Dictionary<string, object?> args, McpToolRequest request, bool hasBox, long aroundId, long roomId,
        List<string> warnings, out BoxMm box, out string source, out HashSet<long> exclude, out McpToolResult? failure)
    {
        box = default;
        source = string.Empty;
        exclude = new HashSet<long>();
        failure = null;
        var padding = ToolArguments.GetDouble(args, "paddingMm");

        if (hasBox)
        {
            args.TryGetValue("box", out var raw);
            if (!GeometryArgs.TryReadBox(raw, "box", out box, out var error))
            {
                failure = GeometryToolSupport.Fail(request, error!);
                return false;
            }
            source = "box";
        }
        else if (aroundId > 0)
        {
            var element = doc.GetElement(new ElementId(aroundId));
            var elementBox = element == null ? null : GeometryToolSupport.HostBox(element, null);
            if (elementBox == null)
            {
                failure = GeometryToolSupport.Fail(request, element == null
                    ? $"aroundElementId {aroundId} was not found in '{doc.Title}'."
                    : $"Element {aroundId} has no bounding box.");
                return false;
            }
            box = elementBox.Value;
            exclude.Add(aroundId);
            source = $"aroundElementId {aroundId}";
        }
        else
        {
            var roomModel = new GeometryModel { Doc = doc };
            var roomLink = ToolArguments.GetLong(args, "roomLinkInstanceId");
            if (roomLink > 0)
            {
                var link = GeometryToolSupport.ResolveLink(doc, roomLink, request, out failure);
                if (link == null) return false;
                roomModel = link;
            }
            var room = roomModel.Doc.GetElement(new ElementId(roomId));
            var roomBox = room == null ? null : GeometryToolSupport.HostBox(room, roomModel.ToHost);
            if (room is not Room || roomBox == null)
            {
                failure = GeometryToolSupport.Fail(request, room == null
                    ? $"roomId {roomId} was not found in the {roomModel.Label}" + (roomLink > 0 ? "." : " (a room in a link needs roomLinkInstanceId).")
                    : room is not Room ? $"Element {roomId} is not a room." : $"Room {roomId} has no bounding box (not placed or not enclosed).");
                return false;
            }
            box = roomBox.Value;
            if (roomModel.Link == null) exclude.Add(roomId);
            source = $"room {((Room)room).Number} ({roomModel.Label}), its bounding box";
        }

        if (Math.Abs(padding) > 0)
        {
            box = BoxMath.Pad(box, padding);
            source += $", padded by {padding:0.#} mm";
        }
        else if (aroundId > 0 && !hasBox)
        {
            warnings.Add("aroundElementId without paddingMm searches the element's own bounding box only.");
        }
        return true;
    }
}
