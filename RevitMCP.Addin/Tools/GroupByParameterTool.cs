using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Query;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Convenience wrapper around revit_group_elements for single-parameter grouping.
/// Kept for backwards compatibility — prompts that worked before still work.
/// </summary>
public class GroupByParameterTool : IRevitMcpTool
{
    public string Name => "revit_group_by_parameter";
    public string Description => "Groups elements by a parameter value and returns counts. parameterName supports partial matching (e.g. 'Nimetus' matches 'ELENEA_ÜLD 001_Nimetus'). Optionally filter by category.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

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

        var queryOpts = new ElementQueryOptions
        {
            Category = category,
            IncludeInstanceParameters = true,
            IncludeTypeParameters = true,
            Limit = 10_000
        };

        var queryResult = _queryEngine.Query(uidoc.Document, uidoc, queryOpts);
        if (!queryResult.Success)
            return Task.FromResult(Fail(request, queryResult.Message));

        var groupOpts = new GroupingOptions
        {
            GroupBy = new List<GroupKeyOptions>
            {
                new() { Type = "Parameter", ParameterName = parameterName, ParameterMatchMode = "ContainsNormalized" }
            }
        };

        var groupResult = _groupingEngine.Group(queryResult.Elements, groupOpts);

        // Filter out "not found" group for backwards-compatible output
        var found = groupResult.GroupsFlat.Where(r => r.Keys.Values.FirstOrDefault() != "(not found)").ToList();
        var matchedElements = found.Sum(r => r.Count);

        var groups = found
            .OrderByDescending(r => r.Count)
            .Select(r => new { name = r.Keys.Values.FirstOrDefault() ?? string.Empty, count = r.Count })
            .ToList();

        sw.Stop();
        var warnings = queryResult.Warnings.Concat(groupResult.Warnings).ToList();

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Found {matchedElements} elements with parameter matching '{parameterName}'. {groups.Count} distinct values.",
            Data = new
            {
                parameterName,
                categoryFilter = category,
                totalScanned = queryResult.TotalMatched,
                matchedElements,
                distinctValues = groups.Count,
                groups
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
