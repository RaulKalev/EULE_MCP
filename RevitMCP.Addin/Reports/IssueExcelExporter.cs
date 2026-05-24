using System.IO;
using ClosedXML.Excel;
using RevitMCP.Addin.Export;
using RevitMCP.Core.Models.Issues;

namespace RevitMCP.Addin.Reports;

/// <summary>
/// Exports an IssueReportDto to a multi-sheet .xlsx file using ClosedXML.
/// Reuses ExportPathService for output path resolution.
/// </summary>
public class IssueExcelExporter
{
    private static readonly ExportPathService _pathService = new();

    public string Export(IssueReportDto report, string? outputDir = null)
    {
        var fileName = $"IssueReport_{SanitizeName(report.Title)}_{report.CreatedAt:yyyy-MM-dd_HHmm}.xlsx";
        var filePath = outputDir != null
            ? Path.Combine(outputDir, fileName)
            : _pathService.ResolveFilePath(fileName);

        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        using var wb = new XLWorkbook();
        AddSummarySheet(wb, report);
        AddIssuesSheet(wb, report);
        wb.SaveAs(filePath);

        return filePath;
    }

    // ── sheets ───────────────────────────────────────────────────────────────

    private static void AddSummarySheet(XLWorkbook wb, IssueReportDto report)
    {
        var ws = wb.Worksheets.Add("Summary");
        var row = 1;

        void AddRow(string label, string value)
        {
            ws.Cell(row, 1).Value = label;
            ws.Cell(row, 2).Value = value;
            ws.Cell(row, 1).Style.Font.Bold = true;
            row++;
        }

        AddRow("Report Title", report.Title);
        AddRow("Run ID", report.RunId);
        AddRow("Created At", report.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss UTC"));
        if (!string.IsNullOrWhiteSpace(report.ModelTitle))
            AddRow("Model", report.ModelTitle);
        if (report.SourceTools.Count > 0)
            AddRow("Source Tools", string.Join(", ", report.SourceTools));

        row++;
        AddRow("Total Issues", report.TotalCount.ToString());
        AddRow("Critical", report.CriticalCount.ToString());
        AddRow("Error", report.ErrorCount.ToString());
        AddRow("Warning", report.WarningCount.ToString());
        AddRow("Info", report.InfoCount.ToString());

        // By category breakdown
        if (report.Issues.Count > 0)
        {
            row++;
            ws.Cell(row, 1).Value = "Category";
            ws.Cell(row, 2).Value = "Count";
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 2).Style.Font.Bold = true;
            row++;

            foreach (var grp in report.Issues.GroupBy(i => i.Category).OrderBy(g => g.Key))
            {
                ws.Cell(row, 1).Value = grp.Key;
                ws.Cell(row, 2).Value = grp.Count();
                row++;
            }
        }

        ws.Column(1).Width = 22;
        ws.Column(2).Width = 50;
    }

    private static void AddIssuesSheet(XLWorkbook wb, IssueReportDto report)
    {
        var ws = wb.Worksheets.Add("Issues");

        // Header
        string[] headers = ["IssueId", "Severity", "Status", "Category", "SourceTool", "Title", "Description", "SuggestedFix", "ElementId", "LinkedElementId", "FilePath", "SheetNumber", "SheetName", "Discipline", "Phase", "CreatedAt"];
        for (var c = 0; c < headers.Length; c++)
        {
            ws.Cell(1, c + 1).Value = headers[c];
            ws.Cell(1, c + 1).Style.Font.Bold = true;
        }

        // Data rows
        var row = 2;
        foreach (var issue in IssueReportService.SortBySeverity(report.Issues))
        {
            ws.Cell(row, 1).Value = issue.IssueId;
            ws.Cell(row, 2).Value = issue.Severity.ToString();
            ws.Cell(row, 3).Value = issue.Status.ToString();
            ws.Cell(row, 4).Value = issue.Category;
            ws.Cell(row, 5).Value = issue.SourceTool;
            ws.Cell(row, 6).Value = issue.Title;
            ws.Cell(row, 7).Value = issue.Description;
            ws.Cell(row, 8).Value = issue.SuggestedFix ?? string.Empty;
            ws.Cell(row, 9).Value = issue.ElementId?.ToString() ?? string.Empty;
            ws.Cell(row, 10).Value = issue.LinkedElementId?.ToString() ?? string.Empty;
            ws.Cell(row, 11).Value = issue.FilePath ?? string.Empty;
            ws.Cell(row, 12).Value = issue.SheetNumber ?? string.Empty;
            ws.Cell(row, 13).Value = issue.SheetName ?? string.Empty;
            ws.Cell(row, 14).Value = issue.Discipline ?? string.Empty;
            ws.Cell(row, 15).Value = issue.Phase ?? string.Empty;
            ws.Cell(row, 16).Value = issue.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss UTC");

            // Severity colour coding
            var severityColour = issue.Severity switch
            {
                IssueSeverity.Critical => XLColor.FromHtml("#FF4C4C"),
                IssueSeverity.Error    => XLColor.FromHtml("#FF9966"),
                IssueSeverity.Warning  => XLColor.FromHtml("#FFD966"),
                _                      => XLColor.NoColor
            };
            if (severityColour != XLColor.NoColor)
                ws.Cell(row, 2).Style.Fill.BackgroundColor = severityColour;

            row++;
        }

        ws.RangeUsed()?.SetAutoFilter();
        ws.Columns().AdjustToContents(1, 60);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static string SanitizeName(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name, @"[\\/:*?""<>|\s]+", "_").Trim('_');
}
