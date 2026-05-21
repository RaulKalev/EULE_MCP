using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Export;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Query;
using RevitMCP.Addin.Services;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class ExportQueryToExcelTool : IRevitMcpTool
{
    public string Name => "revit_export_query_to_excel";
    public string Description => "Queries model elements and exports the results to an Excel (.xlsx) file. Supports category filter, parameter filters, multi-parameter grouping, and selectable parameter columns. Returns the file path.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Reports;

    private static readonly ElementQueryEngine _queryEngine = new();
    private static readonly GroupingEngine _groupingEngine = new();
    private static readonly ExportPathService _pathService = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null)
            return Task.FromResult(Fail(request, "No active document."));

        var doc = uidoc.Document;
        var groupByParsed = ToolArguments.GetGroupByKeysWithWarnings(request.Arguments);
        var filtersParsed = ToolArguments.GetFiltersWithWarnings(request.Arguments);

        if (filtersParsed.ParseFailed)
            return Task.FromResult(Fail(request, "filters argument could not be parsed. Export aborted to avoid exporting unfiltered data."));
        if (groupByParsed.ParseFailed)
            return Task.FromResult(Fail(request, "groupBy argument could not be parsed. Export aborted to avoid ungrouped export."));

        var groupByKeys = groupByParsed.Items;
        var filters = filtersParsed.Items;
        var paramCols = ToolArguments.GetStringArray(request.Arguments, "parameters").ToList();
        var outputMode = ToolArguments.GetString(request.Arguments, "outputMode", "Both");
        var fileName = ToolArguments.GetString(request.Arguments, "fileName", "RevitMCP_Export.xlsx");
        var category = ToolArguments.GetString(request.Arguments, "category");

        var queryOpts = new ElementQueryOptions
        {
            Category = category,
            UseSelection = ToolArguments.GetBool(request.Arguments, "useSelection"),
            ElementIds = ToolArguments.GetLongArray(request.Arguments, "elementIds").ToList(),
            Filters = filters,
            ReturnParameters = paramCols,
            IncludeInstanceParameters = true,
            IncludeTypeParameters = true,
            Limit = ToolArguments.GetInt(request.Arguments, "limit", 5000)
        };

        var queryResult = _queryEngine.Query(doc, uidoc, queryOpts);
        if (!queryResult.Success)
            return Task.FromResult(Fail(request, queryResult.Message));

        GroupingResult? groupingResult = null;
        if (groupByKeys.Count > 0 && (outputMode == "Both" || outputMode == "Groups"))
        {
            groupingResult = _groupingEngine.Group(queryResult.Elements, new GroupingOptions
            {
                GroupBy = groupByKeys
            });
        }

        var filePath = _pathService.ResolveFilePath(fileName);

        // Capture Revit context for summary
        var ctx = RevitContextService.Read(uiapp);

        var exportReq = new ExcelExportRequest
        {
            FilePath = filePath,
            ModelName = ctx.DocumentTitle,
            CentralPath = ctx.CentralModelPath,
            RevitUser = ctx.RevitUsername,
            CategoryFilter = category,
            ParameterFilters = filters,
            GroupBy = groupByKeys,
            ParameterColumns = paramCols,
            OutputMode = outputMode,
            QueryResult = queryResult,
            GroupingResult = groupingResult
        };

        try
        {
            new ExcelExportService().Export(exportReq);
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail(request, $"Excel export failed: {ex.Message}"));
        }

        sw.Stop();
        var warnings = queryResult.Warnings
            .Concat(filtersParsed.Warnings)
            .Concat(groupByParsed.Warnings)
            .Concat(groupingResult?.Warnings ?? []).ToList();

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = "Excel export created.",
            Data = new
            {
                filePath,
                totalElements = queryResult.TotalMatched,
                returnedElements = queryResult.Elements.Count,
                groupRows = groupingResult?.TotalGroups ?? 0
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
