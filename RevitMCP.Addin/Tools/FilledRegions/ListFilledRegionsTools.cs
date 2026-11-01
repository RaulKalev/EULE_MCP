using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.FilledRegions;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.FilledRegions;

/// <summary>revit_list_filled_regions (#135): regions in a view, of a type or by id, with type, boundary, area and parameters.</summary>
public sealed class ListFilledRegionsTool : IRevitMcpTool
{
    public string Name => "revit_list_filled_regions";

    public string Description =>
        "Lists filled and masking regions in a view (view: name or id), by elementIds, or of a type (type: name or id); " +
        "without any of these every region of the document up to limit (default 200). Per region: id, view, type, " +
        "isMasking, areaM2 (Revit's Area value), loops (closed point lists in model mm with the view plane u/v and arcMid, " +
        "the same format revit_create_filled_regions takes) and instance parameters. includeBoundary and includeParameters default true.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return Task.FromResult(failure!);
        var doc = target.Document;
        var args = request.Arguments;

        var viewReference = ToolArguments.GetString(args, "view").Trim();
        var typeReference = ToolArguments.GetString(args, "type").Trim();
        var elementIds = ToolArguments.GetLongArray(args, "elementIds");
        var limit = Math.Max(1, ToolArguments.GetInt(args, "limit", 200));
        var includeBoundary = ToolArguments.GetBool(args, "includeBoundary", true);
        var includeParameters = ToolArguments.GetBool(args, "includeParameters", true);

        View? view = null;
        if (viewReference.Length > 0)
        {
            view = FilledRegionSupport.ResolveView(doc, viewReference, out var reason);
            if (view == null) return Task.FromResult(Fail(request, RegionCodes.ViewNotFound, reason!));
        }
        FilledRegionType? type = null;
        if (typeReference.Length > 0)
        {
            type = FilledRegionSupport.ResolveType(doc, typeReference);
            if (type == null)
                return Task.FromResult(Fail(request, RegionCodes.TypeNotFound, $"No filled region type '{typeReference}'. Types: {FilledRegionSupport.TypeNames(doc)}."));
        }

        var warnings = new List<string>();
        var regions = new List<FilledRegion>();
        if (elementIds.Length > 0)
        {
            foreach (var id in elementIds)
            {
                if (doc.GetElement(new ElementId(id)) is FilledRegion region) regions.Add(region);
                else warnings.Add($"Element {id} is not a filled region.");
            }
        }
        else
        {
            regions.AddRange(new FilteredElementCollector(doc).OfClass(typeof(FilledRegion)).Cast<FilledRegion>());
        }
        if (view != null) regions = regions.Where(r => r.OwnerViewId == view.Id).ToList();
        if (type != null) regions = regions.Where(r => r.GetTypeId() == type.Id).ToList();

        var total = regions.Count;
        var payload = new List<object>();
        var views = new Dictionary<long, View?>();
        foreach (var region in regions.Take(limit))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owner = ViewOf(doc, region, views);
            payload.Add(new
            {
                id = region.Id.Value,
                viewId = region.OwnerViewId.Value,
                viewName = owner?.Name,
                viewType = owner?.ViewType.ToString(),
                type = new { id = region.GetTypeId().Value, name = doc.GetElement(region.GetTypeId())?.Name },
                isMasking = region.IsMasking,
                areaM2 = FilledRegionSupport.RevitAreaM2(region),
                loopCount = region.GetBoundaries().Count,
                loops = includeBoundary ? FilledRegionSupport.DescribeBoundary(region, owner == null ? null : FilledRegionSupport.FrameOf(owner)) : null,
                parameters = includeParameters ? FilledRegionSupport.DescribeParameters(doc, region) : null
            });
        }

        if (total > limit) warnings.Add($"Showing {limit} of {total} regions: raise limit, or narrow by view or type.");
        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{total} filled region(s).",
            Data = new { total, truncated = total > limit, regions = payload },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static View? ViewOf(Document doc, FilledRegion region, Dictionary<long, View?> cache)
    {
        var id = region.OwnerViewId.Value;
        if (!cache.TryGetValue(id, out var view))
        {
            view = doc.GetElement(region.OwnerViewId) as View;
            cache[id] = view;
        }
        return view;
    }

    private static McpToolResult Fail(McpToolRequest request, string status, string message) =>
        new() { RequestId = request.RequestId, Success = false, Status = status, Message = message };
}

/// <summary>revit_list_filled_region_types (#135): types with patterns, colours, line weight, masking and instance count.</summary>
public sealed class ListFilledRegionTypesTool : IRevitMcpTool
{
    public string Name => "revit_list_filled_region_types";

    public string Description =>
        "Lists filled region types: id, name, foregroundPattern, foregroundColor, backgroundPattern, backgroundColor (patterns " +
        "as {id, name, target}, colours as #RRGGBB), lineWeight, isMasking and instanceCount. includePatterns (default true) " +
        "adds the fill patterns and the line styles a filled region can use.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return Task.FromResult(failure!);
        var doc = target.Document;

        var counts = new FilteredElementCollector(doc).OfClass(typeof(FilledRegion)).Cast<FilledRegion>()
            .GroupBy(r => r.GetTypeId().Value).ToDictionary(g => g.Key, g => g.Count());

        var types = FilledRegionSupport.Types(doc).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Select(t => (object)new
        {
            id = t.Id.Value,
            name = t.Name,
            foregroundPattern = FilledRegionPatterns.Describe(doc, t.ForegroundPatternId),
            foregroundColor = FilledRegionPatterns.Hex(t.ForegroundPatternColor),
            backgroundPattern = FilledRegionPatterns.Describe(doc, t.BackgroundPatternId),
            backgroundColor = FilledRegionPatterns.Hex(t.BackgroundPatternColor),
            lineWeight = t.LineWeight,
            isMasking = t.IsMasking,
            instanceCount = counts.TryGetValue(t.Id.Value, out var count) ? count : 0
        }).ToList();

        object? patterns = null;
        object? lineStyles = null;
        if (ToolArguments.GetBool(request.Arguments, "includePatterns", true))
        {
            patterns = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(p => FilledRegionPatterns.Describe(doc, p.Id)).ToList();
            lineStyles = FilledRegionSupport.LineStyles(doc).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(s => new { id = s.Id.Value, name = s.GraphicsStyleCategory?.Name ?? s.Name }).ToList();
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{types.Count} filled region type(s).",
            Data = new { types, patterns, lineStyles },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}
