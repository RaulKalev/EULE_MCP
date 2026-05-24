using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Delivery;
using RevitMCP.Addin.FileSystem;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Reports;
using RevitMCP.Core.Models;
using RevitMCP.Core.Models.Issues;
using System.Diagnostics;

namespace RevitMCP.Addin.Tools.Delivery;

/// <summary>
/// Runs the full delivery QA workflow:
/// 1. Scan folder
/// 2. Compare against Revit sheets
/// 3. Compare against Excel register (if provided)
/// 4. Merge issues and optionally export Excel + Markdown reports
/// </summary>
public class DeliveryRunFullCheckTool : IRevitMcpTool
{
    public string Name => "delivery_run_full_check";
    public string Description => "Runs the full delivery QA workflow: folder scan, Revit sheet comparison, optional Excel register comparison, and optional report export. Read-only.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Delivery;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

        var folderPath = ToolArguments.GetString(request.Arguments, "folderPath");
        if (string.IsNullOrWhiteSpace(folderPath))
            return Task.FromResult(Fail(request, "folderPath is required."));

        var excelFilePath = ToolArguments.GetString(request.Arguments, "excelFilePath");
        var requiredExtRaw = ToolArguments.GetStringArray(request.Arguments, "requiredExtensions");
        var requiredExtensions = (requiredExtRaw.Length > 0 ? requiredExtRaw : new[] { "pdf", "dwg" })
            .Select(e => e.TrimStart('.').ToLowerInvariant()).ToList();
        var stageFilter = ToolArguments.GetStringArray(request.Arguments, "stageFilter").ToList();
        var disciplineFilter = ToolArguments.GetStringArray(request.Arguments, "disciplineFilter").ToList();
        var exportExcel = ToolArguments.GetBool(request.Arguments, "exportExcelReport", false);
        var exportMarkdown = ToolArguments.GetBool(request.Arguments, "exportMarkdownReport", false);
        var maxResults = ToolArguments.GetInt(request.Arguments, "maxResults", 5000);
        var recursive = ToolArguments.GetBool(request.Arguments, "recursive", true);

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
            .Select(s =>
            {
                var parsed = RevitSheetNumberParser.Parse(s.SheetNumber);
                return new RevitSheetDescriptor
                {
                    SheetNumber   = parsed?.SheetNumberCore ?? s.SheetNumber,
                    SheetName     = s.Name,
                    Discipline    = parsed?.Discipline,
                    Stage         = parsed?.Stage,
                    ProjectNumber = parsed?.ProjectNumber
                };
            })
            .ToList();

        var runId = Guid.NewGuid().ToString("N")[..8].ToUpper();
        var allIssues = new List<IssueDto>();

        // Step 2 — Revit comparison
        var revitIssues = DeliveryRevitSheetComparer.Compare(scan, sheets, requiredExtensions, stageFilter, disciplineFilter, runId);
        allIssues.AddRange(revitIssues);

        // Step 3 — Excel register comparison (optional)
        string? registerCompareError = null;
        if (!string.IsNullOrWhiteSpace(excelFilePath))
        {
            var regErr = policy.ValidateRead(excelFilePath);
            if (regErr != null)
            {
                allIssues.Add(new IssueDto
                {
                    IssueId = $"{runId}-REGWARN",
                    RunId = runId,
                    SourceTool = "delivery_run_full_check",
                    Severity = IssueSeverity.Warning,
                    Category = "Delivery.Config",
                    Title = "Excel register path not accessible",
                    Description = regErr,
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }
            else
            {
                var worksheetName = ToolArguments.GetString(request.Arguments, "worksheetName");
                var (registerIssues, compareError) = DeliveryExcelRegisterComparer.Compare(
                    scan, excelFilePath, worksheetName, requiredExtensions, runId);

                registerCompareError = compareError;
                if (compareError == null)
                    allIssues.AddRange(registerIssues);
            }
        }

        // Step 4 — Optional folder-policy checks (temp files, old revisions, etc.)
        var folderPolicy = BuildFolderPolicy(request, requiredExtensions);
        if (folderPolicy != null)
        {
            int policySeq = allIssues.Count + 1;
            var policyIssues = DeliveryFolderPolicyChecker.Check(scan, folderPolicy, runId, ref policySeq);
            allIssues.AddRange(policyIssues);
        }

        // Re-sequence IDs
        for (int i = 0; i < allIssues.Count; i++)
        {
            allIssues[i].IssueId = $"{runId}-{i + 1:D4}";
        }

        var report = DeliveryIssueBuilder.BuildReport(runId,
            $"Full Delivery Check — {System.IO.Path.GetFileName(folderPath)}",
            allIssues);

        // Optional export
        string? excelReportPath = null;
        string? markdownReportPath = null;
        var exportWarnings = new List<string>();

        if (exportExcel || exportMarkdown)
        {
            var exportDir = folderPath;

            if (exportExcel)
            {
                try
                {
                    var exporter = new IssueExcelExporter();
                    excelReportPath = exporter.Export(report, exportDir);
                }
                catch (Exception ex) { exportWarnings.Add($"Excel export failed: {ex.Message}"); }
            }

            if (exportMarkdown)
            {
                try
                {
                    var exporter = new IssueMarkdownExporter();
                    markdownReportPath = exporter.Export(report, exportDir);
                }
                catch (Exception ex) { exportWarnings.Add($"Markdown export failed: {ex.Message}"); }
            }
        }

        var warnings = scan.Warnings.Concat(exportWarnings).ToList();
        if (registerCompareError != null)
            warnings.Insert(0, $"Excel register comparison skipped: {registerCompareError}");

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Full delivery check complete. {allIssues.Count} issue(s) found ({report.ErrorCount} errors, {report.WarningCount} warnings). " +
                      $"Checked {scan.Files.Count} file(s) against {sheets.Count} Revit sheet(s).",
            Data = new
            {
                runId,
                summary = new
                {
                    totalFiles = scan.Files.Count,
                    totalSheets = sheets.Count,
                    totalIssues = report.TotalCount,
                    criticalIssues = report.CriticalCount,
                    errorIssues = report.ErrorCount,
                    warningIssues = report.WarningCount,
                    infoIssues = report.InfoCount
                },
                issueReport = report,
                excelReportPath,
                markdownReportPath
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static DeliveryFolderPolicy? BuildFolderPolicy(McpToolRequest request, List<string> requiredExtensions)
    {
        var args = request.Arguments;
        bool hasAny = ToolArguments.GetBool(args, "checkTempFiles", false)
                   || ToolArguments.GetBool(args, "checkOldRevisions", false)
                   || ToolArguments.GetBool(args, "checkSuspiciousExtensions", false)
                   || ToolArguments.GetBool(args, "checkRequiredFolders", false)
                   || ToolArguments.GetStringArray(args, "requiredProjectFileExtensions").Length > 0;
        if (!hasAny) return null;

        var policy = new DeliveryFolderPolicy
        {
            CheckTempFiles            = ToolArguments.GetBool(args, "checkTempFiles", true),
            CheckOldRevisions         = ToolArguments.GetBool(args, "checkOldRevisions", true),
            CheckSuspiciousExtensions = ToolArguments.GetBool(args, "checkSuspiciousExtensions", false),
            CheckRequiredFolders      = ToolArguments.GetBool(args, "checkRequiredFolders", false),
            RequiredExtensions        = requiredExtensions
        };
        var requiredFolders = ToolArguments.GetStringArray(args, "requiredFolders");
        if (requiredFolders.Length > 0) policy.RequiredFolders = [.. requiredFolders];

        var allowedExtras = ToolArguments.GetStringArray(args, "allowedExtraExtensions");
        if (allowedExtras.Length > 0) policy.AllowedExtraExtensions = [.. allowedExtras];

        var reqProjectExts = ToolArguments.GetStringArray(args, "requiredProjectFileExtensions");
        if (reqProjectExts.Length > 0) policy.RequiredProjectFileExtensions = [.. reqProjectExts];

        var ignored = ToolArguments.GetStringArray(args, "ignoredPatterns");
        if (ignored.Length > 0) policy.IgnoredPatterns = [.. ignored];

        return policy;
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
