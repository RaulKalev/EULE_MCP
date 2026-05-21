using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Query;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class GroupElementsTool : IRevitMcpTool
{
    public string Name => "revit_group_elements";
    public string Description => "Groups model elements by one or more keys (Category, Family, Type, Level, or Parameter). Returns both flat rows (for Excel) and nested output (for AI summaries). Supports parameter filters and category scope.";
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

        var groupByParsed = ToolArguments.GetGroupByKeysWithWarnings(request.Arguments);
        var filtersParsed = ToolArguments.GetFiltersWithWarnings(request.Arguments);

        if (groupByParsed.ParseFailed)
            return Task.FromResult(Fail(request, "groupBy argument could not be parsed as a JSON array."));
        if (groupByParsed.Items.Count == 0)
            return Task.FromResult(Fail(request, "At least one groupBy key is required."));

        var queryOpts = new ElementQueryOptions
        {
            Category = ToolArguments.GetString(request.Arguments, "category"),
            UseSelection = ToolArguments.GetBool(request.Arguments, "useSelection"),
            ElementIds = ToolArguments.GetLongArray(request.Arguments, "elementIds").ToList(),
            Filters = filtersParsed.Items,
            IncludeInstanceParameters = true,
            IncludeTypeParameters = true,
            Limit = ToolArguments.GetInt(request.Arguments, "limit", 5000)
        };

        var queryResult = _queryEngine.Query(uidoc.Document, uidoc, queryOpts);
        if (!queryResult.Success)
            return Task.FromResult(Fail(request, queryResult.Message));

        var groupOpts = new GroupingOptions
        {
            GroupBy = groupByParsed.Items,
            IncludeElements = ToolArguments.GetBool(request.Arguments, "includeElements"),
            Limit = queryOpts.Limit
        };

        var groupResult = _groupingEngine.Group(queryResult.Elements, groupOpts);

        // Build flat output as list of plain dicts for JSON serialization
        var flatRows = groupResult.GroupsFlat.Select(row =>
        {
            var d = new Dictionary<string, object>(row.Keys.Select(kv =>
                new KeyValuePair<string, object>(kv.Key, kv.Value)));
            d["count"] = row.Count;
            if (row.ElementIds != null)
                d["elementIds"] = row.ElementIds;
            return d;
        }).ToList();

        sw.Stop();
        var warnings = queryResult.Warnings
            .Concat(filtersParsed.Warnings)
            .Concat(groupByParsed.Warnings)
            .Concat(groupResult.Warnings).ToList();

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = groupResult.Message,
            Data = new
            {
                totalElements = groupResult.TotalElements,
                totalGroups = groupResult.TotalGroups,
                groupsFlat = flatRows,
                groupsNested = groupResult.GroupsNested
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
