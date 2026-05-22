using System.Diagnostics;
using System.Text.Json;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Coordination.Clash.DTOs;
using RevitMCP.Addin.Coordination.Clash.Reports;
using RevitMCP.Addin.Coordination.Clash.Services;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class ExportClashReportToExcelTool : IRevitMcpTool
{
    public string Name => "revit_export_clash_report_to_excel";
    public string Description => "Exports clash detection results to an Excel workbook with summary, per-rule, per-level, per-linked-model, and per-category-pair sheets.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Coordination;

    private readonly ClashRunCacheService _cache = new();
    private readonly ClashExcelExporter _exporter = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var args = request.Arguments;
        var useLastRun = ToolArguments.GetBool(args, "useLastRun", true);
        var clashesJson = ToolArguments.GetString(args, "clashesJson", "");
        var fileName = ToolArguments.GetString(args, "fileName", "Clash_Report.xlsx");
        var includeSummary = ToolArguments.GetBool(args, "includeSummary", true);
        var includeByRule = ToolArguments.GetBool(args, "includeByRule", true);
        var includeByLevel = ToolArguments.GetBool(args, "includeByLevel", true);
        var includeByLinkedModel = ToolArguments.GetBool(args, "includeByLinkedModel", true);
        var includeByCategoryPair = ToolArguments.GetBool(args, "includeByCategoryPair", true);

        ClashRunResultDto? run = null;
        if (useLastRun)
            run = _cache.Load();
        else if (!string.IsNullOrWhiteSpace(clashesJson))
            run = JsonSerializer.Deserialize<ClashRunResultDto>(clashesJson);

        if (run == null)
            return Task.FromResult(Fail(request, "No clash data found. Run a detection first or provide clashesJson."));

        var outPath = _exporter.Export(run, fileName, includeSummary, includeByRule, includeByLevel, includeByLinkedModel, includeByCategoryPair);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Exported {run.TotalClashes} clash(es) to: {outPath}",
            Data = new { filePath = outPath, totalClashes = run.TotalClashes },
            Warnings = run.Warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
