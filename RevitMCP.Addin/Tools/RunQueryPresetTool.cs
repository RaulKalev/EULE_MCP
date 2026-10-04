using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Export;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Presets;
using RevitMCP.Addin.Query;
using RevitMCP.Addin.Services;
using RevitMCP.Core.Models;
using RevitMCP.Core.Safety;

namespace RevitMCP.Addin.Tools;

public class RunQueryPresetTool : IRevitMcpTool
{
    public string Name => "revit_run_query_preset";
    public string Description => "Runs a saved query preset by name. Can return JSON results or export to Excel.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Reports;

    private static readonly QueryPresetStore _store = new();
    private static readonly ElementQueryEngine _queryEngine = new();
    private static readonly GroupingEngine _groupingEngine = new();
    private static readonly ExportPathService _pathService = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null)
            return Task.FromResult(Fail(request, "No active document."));

        var presetName = ToolArguments.GetString(request.Arguments, "presetName");
        if (string.IsNullOrWhiteSpace(presetName))
            return Task.FromResult(Fail(request, "presetName is required."));

        var preset = _store.FindByName(presetName);
        if (preset == null)
        {
            var available = _store.GetPresetNames();
            var list = available.Count > 0
                ? $" Available: {string.Join(", ", available)}"
                : " No presets configured.";
            return Task.FromResult(Fail(request, $"Preset '{presetName}' not found.{list}"));
        }

        var exportToExcel = ToolArguments.GetBool(request.Arguments, "exportToExcel");
        var fileName = ToolArguments.GetString(request.Arguments, "fileName", $"{preset.Name.Replace(" ", "_")}.xlsx");
        var limit = ToolArguments.GetInt(request.Arguments, "limit", 5000);

        // Run query
        var queryOpts = new ElementQueryOptions
        {
            Category = preset.Category,
            Filters = preset.Filters,
            ReturnParameters = preset.Parameters,
            IncludeInstanceParameters = true,
            IncludeTypeParameters = true,
            // Group and export over every match up to limit; the JSON element list is paged below.
            CollectAll = true,
            Limit = limit
        };

        var queryResult = _queryEngine.Query(uidoc.Document, uidoc, queryOpts);
        if (!queryResult.Success)
            return Task.FromResult(Fail(request, queryResult.Message));

        // Group if needed
        GroupingResult? groupingResult = null;
        if (preset.GroupBy.Count > 0)
        {
            groupingResult = _groupingEngine.Group(queryResult.Elements, new GroupingOptions
            {
                GroupBy = preset.GroupBy
            });
        }

        // Export or return JSON
        if (exportToExcel)
        {
            var filePath = _pathService.ResolveFilePath(fileName);
            var ctx = RevitContextService.Read(uiapp);

            var exportReq = new ExcelExportRequest
            {
                FilePath = filePath,
                ModelName = ctx.DocumentTitle,
                CentralPath = ctx.CentralModelPath,
                RevitUser = ctx.RevitUsername,
                CategoryFilter = preset.Category,
                ParameterFilters = preset.Filters,
                GroupBy = preset.GroupBy,
                ParameterColumns = preset.Parameters,
                OutputMode = preset.DefaultOutputMode,
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
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"Preset '{preset.Name}' exported to Excel.",
                Data = new
                {
                    presetName = preset.Name,
                    filePath,
                    totalElements = queryResult.TotalMatched,
                    groupRows = groupingResult?.TotalGroups ?? 0
                },
                Warnings = queryResult.Warnings,
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        // Return JSON results. Groups cover every collected element; the element list is one page.
        sw.Stop();
        var warnings = queryResult.Warnings.Concat(groupingResult?.Warnings ?? []).ToList();
        var pageSize = QueryGuard.ResolveEffectiveLimits(
            ToolArguments.GetInt(request.Arguments, "pageSize", -1), limit, 0, 0).effectivePageSize;
        var page = Math.Max(0, ToolArguments.GetInt(request.Arguments, "page", 0));
        var pageElements = queryResult.Elements.Skip(page * pageSize).Take(pageSize).ToList();
        var hasMore = queryResult.Elements.Count > (page + 1) * pageSize;
        if (hasMore)
            warnings.Add($"Element list paged: showing {pageElements.Count} of {queryResult.Elements.Count} (page {page}). Groups cover all of them; use 'page' / 'pageSize' or exportToExcel for the rest.");

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Preset '{preset.Name}' matched {queryResult.TotalMatched} elements; returned {pageElements.Count}.",
            Data = new
            {
                presetName = preset.Name,
                totalElements = queryResult.TotalMatched,
                collectedElements = queryResult.Elements.Count,
                returnedElements = pageElements.Count,
                page,
                pageSize,
                hasMore,
                elements = pageElements,
                groupRows = groupingResult?.TotalGroups ?? 0,
                groups = groupingResult?.GroupsFlat
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
