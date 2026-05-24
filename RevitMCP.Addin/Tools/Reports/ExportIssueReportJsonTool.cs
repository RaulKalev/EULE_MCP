using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Export;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Reports;
using RevitMCP.Core.Models;
using RevitMCP.Core.Models.Issues;

namespace RevitMCP.Addin.Tools.Reports;

/// <summary>
/// Exports a previously-built IssueReportDto as a JSON file.
/// The report JSON is passed in via the "reportJson" argument.
/// </summary>
public class ExportIssueReportJsonTool : IRevitMcpTool
{
    public string Name => "revit_export_issues_json";
    public string Description => "Exports an issue report (passed as JSON) to a .json file on disk. Returns the output file path. Requires approval — writes a file to disk.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Reports;

    private static readonly ExportPathService _pathService = new();

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

        var outputDir = _pathService.ExportDirectory;
        var fileName = $"IssueReport_{SanitizeName(report.Title)}_{report.CreatedAt:yyyy-MM-dd_HHmm}.json";
        var filePath = Path.Combine(outputDir, fileName);

        try
        {
            Directory.CreateDirectory(outputDir);
            var pretty = JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, pretty, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail(request, $"Failed to write JSON: {ex.Message}"));
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Issue report written to {filePath}",
            Data = new { filePath, totalIssues = report.TotalCount, runId = report.RunId },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };

    private static string SanitizeName(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name, @"[\\/:*?""<>|\s]+", "_").Trim('_');
}
