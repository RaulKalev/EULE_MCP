using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Coordination.Clash.Services;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class GetClashCandidatesTool : IRevitMcpTool
{
    public string Name => "revit_get_clash_candidates";
    public string Description => "Returns candidate element counts for a clash check without running detection. Use this to estimate scope before committing to a full run.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Coordination;

    private readonly ClashCandidateCollector _collector = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var documentTarget = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (documentTarget == null) return Task.FromResult(documentFailure!);
        var doc = documentTarget.Document;

        var args = request.Arguments;
        var sourceCategories = ToolArguments.GetStringArray(args, "sourceCategories");
        var targetCategories = ToolArguments.GetStringArray(args, "targetCategories");
        var includeLinks = ToolArguments.GetBool(args, "includeLinks", true);
        var includeGenericModels = ToolArguments.GetBool(args, "includeGenericModels", true);
        var includeImportedGeometry = ToolArguments.GetBool(args, "includeImportedGeometry", true);
        var linkNameFilters = ToolArguments.GetStringArray(args, "linkNameFilters");
        var limit = ToolArguments.GetInt(args, "limit", 5000);

        if (sourceCategories.Length == 0) return Task.FromResult(Fail(request, "sourceCategories is required."));
        if (targetCategories.Length == 0) return Task.FromResult(Fail(request, "targetCategories is required."));

        var (sources, srcWarnings) = _collector.Collect(doc, sourceCategories, includeLinks, includeGenericModels, includeImportedGeometry, linkNameFilters, limit);
        var (targets, tgtWarnings) = _collector.Collect(doc, targetCategories, includeLinks, includeGenericModels, includeImportedGeometry, linkNameFilters, limit);

        var allWarnings = srcWarnings.Concat(tgtWarnings).Distinct().ToList();
        long maxPairs = (long)sources.Count * targets.Count;

        // X4: a set cut at limit looked complete. The collector stops at limit, so reaching it means "maybe more".
        var sourceTruncated = limit > 0 && sources.Count >= limit;
        var targetTruncated = limit > 0 && targets.Count >= limit;
        if (sourceTruncated || targetTruncated)
            allWarnings.Add($"The {(sourceTruncated && targetTruncated ? "source and target sets" : sourceTruncated ? "source set" : "target set")} " +
                            $"reached limit {limit}; there may be more candidates. Raise limit (0 = unlimited) for the full count.");

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "Source: {0} elements, Target: {1} elements. Max pairs: {2:N0}.", sources.Count, targets.Count, maxPairs),
            Data = new
            {
                sourceCount = sources.Count,
                targetCount = targets.Count,
                sourceTruncated,
                targetTruncated,
                maxPairs,
                sourceBreakdown = sources.GroupBy(c => $"{c.Category} ({c.Model})").ToDictionary(g => g.Key, g => g.Count()),
                targetBreakdown = targets.GroupBy(c => $"{c.Category} ({c.Model})").ToDictionary(g => g.Key, g => g.Count()),
            },
            Warnings = allWarnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
