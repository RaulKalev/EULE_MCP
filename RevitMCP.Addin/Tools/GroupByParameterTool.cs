using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Query;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Convenience wrapper around revit_group_elements for single-parameter grouping.
/// Kept for backwards compatibility — prompts that worked before still work.
/// Groups and counts always cover every matching element (bounded only by the hard scan
/// safety cap); only the optional per-group element id lists are paged.
/// </summary>
public class GroupByParameterTool : IRevitMcpTool
{
    public string Name => "revit_group_by_parameter";
    public string Description => "Groups elements by a parameter value and returns counts computed over all matching elements (not a page). parameterName supports partial matching (e.g. 'Nimetus' matches 'ELENEA_ÜLD 001_Nimetus'); Type, Type Name, Family, Family Name and Family and Type group by the element's type/family names. Optionally filter by category; includeElementIds adds up to maxElementIdsPerGroup ids per group.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    private const int DefaultMaxElementIdsPerGroup = 100;

    private static readonly ElementQueryEngine _queryEngine = new();
    private static readonly GroupingEngine _groupingEngine = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null)
            return Task.FromResult(Fail(request, "No active document."));

        var parameterName = ToolArguments.GetString(request.Arguments, "parameterName");
        if (string.IsNullOrWhiteSpace(parameterName))
            return Task.FromResult(Fail(request, "parameterName is required."));

        var category = ToolArguments.GetString(request.Arguments, "category");
        var includeElementIds = ToolArguments.GetBool(request.Arguments, "includeElementIds", false);
        var maxIdsPerGroup = ToolArguments.GetInt(request.Arguments, "maxElementIdsPerGroup", DefaultMaxElementIdsPerGroup);
        if (maxIdsPerGroup <= 0) maxIdsPerGroup = DefaultMaxElementIdsPerGroup;

        // Identity names (Type, Family, ...) group by the element's type/family names, which
        // the engine fills in without a parameter read.
        var isIdentity = ElementIdentityParameters.IsIdentityName(parameterName);

        var queryOpts = new ElementQueryOptions
        {
            Category = category,
            IncludeInstanceParameters = !isIdentity,
            IncludeTypeParameters = !isIdentity,
            // Read only the grouped parameter: keeps the per-element read cheap and stops the
            // per-element parameter cap from hiding it behind 40 unrelated parameters.
            ReturnParameters = isIdentity ? new List<string>() : new List<string> { parameterName },
            ReturnParameterMatchMode = "ContainsNormalized",
            // Every matching element, not one page — bounded only by the scan safety cap.
            CollectAll = true,
            Limit = 0
        };

        var queryResult = _queryEngine.Query(uidoc.Document, uidoc, queryOpts, cancellationToken);
        if (!queryResult.Success)
            return Task.FromResult(Fail(request, queryResult.Message));

        var groupOpts = new GroupingOptions
        {
            GroupBy = new List<GroupKeyOptions>
            {
                new() { Type = "Parameter", ParameterName = parameterName, ParameterMatchMode = "ContainsNormalized" }
            },
            IncludeElements = includeElementIds
        };

        var groupResult = _groupingEngine.Group(queryResult.Elements, groupOpts);
        var summary = ParameterGroupSummary.Build(
            groupResult.GroupsFlat, queryResult.Elements.Count, includeElementIds, maxIdsPerGroup);

        var groups = summary.Groups.Select(g => includeElementIds
            ? (object)new { name = g.Name, count = g.Count, elementIds = g.ElementIds, elementIdsTruncated = g.ElementIdsTruncated }
            : new { name = g.Name, count = g.Count }).ToList();

        sw.Stop();
        var warnings = queryResult.Warnings.Concat(groupResult.Warnings).ToList();
        if (includeElementIds && summary.Groups.Any(g => g.ElementIdsTruncated == true))
            warnings.Add($"Element id lists are capped at {maxIdsPerGroup} per group (counts are complete). Raise maxElementIdsPerGroup or use revit_find_elements_by_parameter with a filter on '{parameterName}' to page through a group.");

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Grouped {summary.ElementsGrouped} elements: {summary.MatchedElements} have a parameter matching '{parameterName}', {summary.Groups.Count} distinct values.",
            Data = new
            {
                parameterName,
                categoryFilter = category,
                totalMatched = queryResult.TotalMatched,
                totalScanned = queryResult.TotalMatched,
                elementsGrouped = summary.ElementsGrouped,
                matchedElements = summary.MatchedElements,
                notFoundElements = summary.NotFoundElements,
                distinctValues = summary.Groups.Count,
                groups
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
