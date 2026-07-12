using System.Diagnostics;
using System.Text.Json;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Reports;
using RevitMCP.Core.Models;
using RevitMCP.Core.Models.Issues;

namespace RevitMCP.Addin.Tools.Reports;

/// <summary>
/// Exports an issue report as a self-contained offline HTML dashboard.
/// The report JSON is passed via the "reportJson" argument.
/// </summary>
public class ExportIssueReportHtmlDashboardTool : IRevitMcpTool, IBackgroundMcpTool
{
    public string Name        => "revit_export_issues_html_dashboard";
    public string Description => "Exports an issue report (passed as JSON) as a standalone offline HTML dashboard with filtering, sorting and severity cards. Returns the output file path. Requires approval — writes a file to disk.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory   Category   => ToolCategory.Reports;

    private static readonly IssueHtmlDashboardExporter _exporter = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        var reportJson = ToolArguments.GetString(request.Arguments, "reportJson");
        if (string.IsNullOrWhiteSpace(reportJson))
            return Task.FromResult(Fail(request, "reportJson is required."));

        var fileNameArg      = ToolArguments.GetString(request.Arguments, "fileName");
        var fileName         = string.IsNullOrWhiteSpace(fileNameArg) ? null : fileNameArg;
        var includeEmbedded  = ToolArguments.GetBool(request.Arguments, "includeEmbeddedJson", defaultValue: true);

        IssueReportDto report;
        try
        {
            report = JsonSerializer.Deserialize<IssueReportDto>(reportJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Deserialization returned null.");
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail(request, $"Failed to parse reportJson: {ex.Message}"));
        }

        string filePath;
        try
        {
            filePath = _exporter.Export(report, outputDir: null, fileName: fileName, includeEmbeddedJson: includeEmbedded);
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail(request, $"HTML export failed: {ex.Message}"));
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = $"HTML dashboard written to {filePath}",
            Data       = new { filePath, totalIssues = report.TotalCount, runId = report.RunId },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
