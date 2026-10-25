using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Delivery;
using RevitMCP.Addin.FileSystem;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;
using RevitMCP.Core.Models.Issues;

namespace RevitMCP.Addin.Tools.Delivery;

/// <summary>
/// Compares files in a delivery folder against Revit sheets.
/// Returns an <see cref="IssueReportDto"/>-compatible result.
/// </summary>
public class DeliveryCheckAgainstRevitSheetsTool : IRevitMcpTool
{
    public string Name => "delivery_check_against_revit_sheets";
    public string Description => "Compares exported files in a delivery folder against Revit sheets. Returns an IssueReportDto with missing files, orphan files, stage/discipline mismatches, and duplicate exports. Read-only.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Delivery;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var documentTarget = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (documentTarget == null) return Task.FromResult(documentFailure!);
        var doc = documentTarget.Document;

        var folderPath = ToolArguments.GetString(request.Arguments, "folderPath");
        if (string.IsNullOrWhiteSpace(folderPath))
            return Task.FromResult(Fail(request, "folderPath is required."));

        var requiredExtRaw = ToolArguments.GetStringArray(request.Arguments, "requiredExtensions");
        var requiredExtensions = (requiredExtRaw.Length > 0 ? requiredExtRaw : new[] { "pdf", "dwg" })
            .Select(e => e.TrimStart('.').ToLowerInvariant()).ToList();

        var stageFilter = ToolArguments.GetStringArray(request.Arguments, "stageFilter").ToList();
        var disciplineFilter = ToolArguments.GetStringArray(request.Arguments, "disciplineFilter").ToList();
        var sheetNumberFilter = ToolArguments.GetString(request.Arguments, "sheetNumberFilter");
        var recursive = ToolArguments.GetBool(request.Arguments, "recursive", true);
        var maxResults = ToolArguments.GetInt(request.Arguments, "maxResults", 5000);

        // Scan folder
        var policy = new FilePathPolicy();
        var scanner = new DeliveryFileScanner(policy);
        var scan = scanner.Scan(folderPath, recursive, requiredExtensions, maxResults);
        if (!scan.Success)
            return Task.FromResult(Fail(request, scan.Error!));

        // Collect Revit sheets
        var sheets = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .Where(s => !s.IsPlaceholder)
            .Select(s => RevitSheetDescriptor.FromSheet(s.SheetNumber, s.Name))
            .Where(s => string.IsNullOrWhiteSpace(sheetNumberFilter) ||
                        s.SheetNumber.Contains(sheetNumberFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var runId = Guid.NewGuid().ToString("N")[..8].ToUpper();
        var comparison = DeliveryRevitSheetComparer.CompareSheets(scan, sheets, requiredExtensions, stageFilter, disciplineFilter, runId);
        var issues = comparison.Issues;
        var warnings = new List<string>(scan.Warnings);
        if (comparison.UnparsedSheetNumbers.Count > 0)
            warnings.Add($"{comparison.UnparsedSheetNumbers.Count} sheet(s) skipped by disciplineFilter: sheet number not recognised.");

        var report = DeliveryIssueBuilder.BuildReport(runId,
            $"Delivery vs Revit Sheets — {System.IO.Path.GetFileName(folderPath)}",
            issues);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Delivery check complete. {issues.Count} issue(s) found. " +
                      DeliveryRevitSheetComparer.DescribeSheetCounts(scan.Files.Count, sheets.Count, comparison),
            Data = new
            {
                summary = new
                {
                    totalFiles = scan.Files.Count,
                    totalSheets = sheets.Count,
                    comparedSheets = comparison.ComparedSheetCount,
                    unparsedSheets = comparison.UnparsedSheetNumbers.Count,
                    totalIssues = report.TotalCount,
                    criticalIssues = report.CriticalCount,
                    errorIssues = report.ErrorCount,
                    warningIssues = report.WarningCount,
                    infoIssues = report.InfoCount
                },
                issueReport = report
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
