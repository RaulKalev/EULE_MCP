using System.IO;
using System.Text;
using RevitMCP.Core.Models.Issues;

namespace RevitMCP.Addin.Reports;

/// <summary>
/// Exports an IssueReportDto to a Markdown (.md) file.
/// </summary>
public class IssueMarkdownExporter
{
    public string Export(IssueReportDto report, string? outputDir = null)
    {
        var baseDir = outputDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "RKTools", "RevitMCP", "Exports");
        Directory.CreateDirectory(baseDir);

        var fileName = $"IssueReport_{SanitizeName(report.Title)}_{report.CreatedAt:yyyy-MM-dd_HHmm}.md";
        var filePath = Path.Combine(baseDir, fileName);

        File.WriteAllText(filePath, BuildMarkdown(report), Encoding.UTF8);
        return filePath;
    }

    private static string BuildMarkdown(IssueReportDto report)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# {report.Title}");
        sb.AppendLine();
        sb.AppendLine($"**Run ID:** `{report.RunId}`  ");
        sb.AppendLine($"**Created:** {report.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC  ");
        if (!string.IsNullOrWhiteSpace(report.ModelTitle))
            sb.AppendLine($"**Model:** {report.ModelTitle}  ");
        if (report.SourceTools.Count > 0)
            sb.AppendLine($"**Source Tools:** {string.Join(", ", report.SourceTools)}  ");

        sb.AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine($"| Severity | Count |");
        sb.AppendLine($"|----------|-------|");
        sb.AppendLine($"| Critical | {report.CriticalCount} |");
        sb.AppendLine($"| Error | {report.ErrorCount} |");
        sb.AppendLine($"| Warning | {report.WarningCount} |");
        sb.AppendLine($"| Info | {report.InfoCount} |");
        sb.AppendLine($"| **Total** | **{report.TotalCount}** |");

        // Category breakdown
        var byCategory = report.Issues.GroupBy(i => i.Category).OrderBy(g => g.Key).ToList();
        if (byCategory.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## By Category");
            sb.AppendLine();
            sb.AppendLine($"| Category | Count |");
            sb.AppendLine($"|----------|-------|");
            foreach (var grp in byCategory)
                sb.AppendLine($"| {grp.Key} | {grp.Count()} |");
        }

        // Issues table
        if (report.Issues.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Issues");
            sb.AppendLine();
            sb.AppendLine("| ID | Severity | Category | Title | Description | Suggested Fix |");
            sb.AppendLine("|----|----------|----------|-------|-------------|---------------|");

            foreach (var issue in IssueReportService.SortBySeverity(report.Issues))
            {
                var id = Escape(issue.IssueId);
                var sev = issue.Severity.ToString();
                var cat = Escape(issue.Category);
                var title = Escape(issue.Title);
                var desc = Escape(issue.Description);
                var fix = Escape(issue.SuggestedFix ?? string.Empty);
                sb.AppendLine($"| {id} | {sev} | {cat} | {title} | {desc} | {fix} |");
            }
        }

        return sb.ToString();
    }

    private static string Escape(string s) =>
        s.Replace("|", "\\|").Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");

    private static string SanitizeName(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name, @"[\\/:*?""<>|\s]+", "_").Trim('_');
}
