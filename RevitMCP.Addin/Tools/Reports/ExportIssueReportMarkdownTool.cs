using System.Diagnostics;
using System.Text.Json;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Reports;
using RevitMCP.Core.Models;
using RevitMCP.Core.Models.Issues;

namespace RevitMCP.Addin.Tools.Reports;

/// <summary>
/// Exports a previously-built IssueReportDto as a Markdown (.md) file.
/// The report JSON is passed in via the "reportJson" argument.
/// </summary>
public class ExportIssueReportMarkdownTool : IRevitMcpTool
{
    public string Name => "revit_export_issues_markdown";
    public string Description => "Exports an issue report (passed as JSON) to a Markdown (.md) file. Returns the output file path.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Reports;

    private static readonly IssueMarkdownExporter _exporter = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        var reportJson = ToolArguments.GetString(request.Arguments, "reportJson");
        if (string.IsNullOrWhiteSpace(reportJson))
            return Task.FromResult(Fail(request, "reportJson is required."));

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
            filePath = _exporter.Export(report);
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail(request, $"Markdown export failed: {ex.Message}"));
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Issue report Markdown written to {filePath}",
            Data = new { filePath, totalIssues = report.TotalCount, runId = report.RunId },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
