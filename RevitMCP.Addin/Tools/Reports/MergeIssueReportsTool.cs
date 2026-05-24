using System.Diagnostics;
using System.Text.Json;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Reports;
using RevitMCP.Core.Models;
using RevitMCP.Core.Models.Issues;

namespace RevitMCP.Addin.Tools.Reports;

/// <summary>
/// Merges multiple IssueReportDtos into a single consolidated report.
/// Pass an array of report JSON strings via "reportJsonArray".
/// </summary>
public class MergeIssueReportsTool : IRevitMcpTool
{
    public string Name => "revit_merge_issue_reports";
    public string Description => "Merges multiple issue reports (array of JSON strings) into one consolidated report. Returns the merged report as JSON.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Reports;

    private static readonly IssueReportService _service = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        var reportJsonArray = ToolArguments.GetStringArray(request.Arguments, "reportJsonArray");
        if (reportJsonArray.Length == 0)
            return Task.FromResult(Fail(request, "reportJsonArray is required (array of issue report JSON strings)."));

        var mergedTitle = ToolArguments.GetString(request.Arguments, "title", "Merged Issue Report");

        var reports = new List<IssueReportDto>();
        var parseErrors = new List<string>();

        for (var i = 0; i < reportJsonArray.Length; i++)
        {
            try
            {
                var r = JsonSerializer.Deserialize<IssueReportDto>(reportJsonArray[i],
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (r != null) reports.Add(r);
            }
            catch (Exception ex)
            {
                parseErrors.Add($"Item [{i}]: {ex.Message}");
            }
        }

        if (reports.Count == 0)
            return Task.FromResult(Fail(request, $"No valid reports to merge. Parse errors: {string.Join("; ", parseErrors)}"));

        var merged = _service.MergeReports(mergedTitle, reports);

        string mergedJson;
        try
        {
            mergedJson = JsonSerializer.Serialize(merged,
                new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail(request, $"Serialization failed: {ex.Message}"));
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Merged {reports.Count} report(s) into {merged.TotalCount} issue(s).",
            Data = new
            {
                mergedReport = mergedJson,
                runId = merged.RunId,
                totalIssues = merged.TotalCount,
                criticalCount = merged.CriticalCount,
                errorCount = merged.ErrorCount,
                warningCount = merged.WarningCount,
                infoCount = merged.InfoCount,
                sourceReportCount = reports.Count
            },
            Warnings = parseErrors,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
