using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Delivery;
using RevitMCP.Addin.FileSystem;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Delivery;

/// <summary>
/// Compares files in a delivery folder against an Excel document register.
/// Returns an <see cref="RevitMCP.Core.Models.Issues.IssueReportDto"/>-compatible result.
/// No Revit document required.
/// </summary>
public class DeliveryCheckAgainstExcelRegisterTool : IRevitMcpTool
{
    public string Name => "delivery_check_against_excel_register";
    public string Description => "Compares files in a delivery folder against an Excel document register. Returns issues for missing files, missing register rows, and duplicate document numbers. Read-only.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Delivery;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        var folderPath = ToolArguments.GetString(request.Arguments, "folderPath");
        var excelFilePath = ToolArguments.GetString(request.Arguments, "excelFilePath");
        if (string.IsNullOrWhiteSpace(folderPath))
            return Task.FromResult(Fail(request, "folderPath is required."));
        if (string.IsNullOrWhiteSpace(excelFilePath))
            return Task.FromResult(Fail(request, "excelFilePath is required."));

        var worksheetName = ToolArguments.GetString(request.Arguments, "worksheetName");
        var requiredExtRaw = ToolArguments.GetStringArray(request.Arguments, "requiredExtensions");
        var requiredExtensions = (requiredExtRaw.Length > 0 ? requiredExtRaw : new[] { "pdf", "dwg" })
            .Select(e => e.TrimStart('.').ToLowerInvariant()).ToList();
        var recursive = ToolArguments.GetBool(request.Arguments, "recursive", true);
        var maxResults = ToolArguments.GetInt(request.Arguments, "maxResults", 5000);

        // Validate read access to both paths
        var policy = new FilePathPolicy();
        var folderErr = policy.ValidateRead(folderPath);
        if (folderErr != null) return Task.FromResult(Fail(request, folderErr));
        var excelErr = policy.ValidateRead(excelFilePath);
        if (excelErr != null) return Task.FromResult(Fail(request, excelErr));

        // Scan folder
        var scanner = new DeliveryFileScanner(policy);
        var scan = scanner.Scan(folderPath, recursive, requiredExtensions, maxResults);
        if (!scan.Success)
            return Task.FromResult(Fail(request, scan.Error!));

        var runId = Guid.NewGuid().ToString("N")[..8].ToUpper();
        var (issues, compareError) = DeliveryExcelRegisterComparer.Compare(scan, excelFilePath, worksheetName, requiredExtensions, runId);
        if (compareError != null)
            return Task.FromResult(Fail(request, compareError));

        var report = DeliveryIssueBuilder.BuildReport(runId,
            $"Delivery vs Excel Register — {System.IO.Path.GetFileName(folderPath)}",
            issues);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Register check complete. {issues.Count} issue(s) found. Checked {scan.Files.Count} file(s) against Excel register.",
            Data = new
            {
                summary = new
                {
                    totalFiles = scan.Files.Count,
                    totalIssues = report.TotalCount,
                    errorIssues = report.ErrorCount,
                    warningIssues = report.WarningCount
                },
                issueReport = report
            },
            Warnings = scan.Warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
