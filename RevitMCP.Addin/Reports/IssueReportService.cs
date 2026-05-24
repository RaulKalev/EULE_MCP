using RevitMCP.Core.Models.Issues;

namespace RevitMCP.Addin.Reports;

/// <summary>
/// In-memory issue report manager. Creates a run-stamped report, normalises issue IDs,
/// and provides sorting/filtering helpers used by the exporter tools.
/// </summary>
public class IssueReportService
{
    /// <summary>
    /// Creates a new IssueReportDto and stamps every issue with a unique run-prefixed ID
    /// in the format &lt;runId&gt;-&lt;NNNN&gt; (e.g. "2026-05-24T00-00-00Z_abcd1234-0001").
    /// </summary>
    public IssueReportDto CreateReport(
        string title,
        IEnumerable<IssueDto> issues,
        string? modelTitle = null,
        string? runId = null)
    {
        var effectiveRunId = runId ?? BuildRunId();

        var issueList = issues.ToList();

        // Stamp IDs
        for (var i = 0; i < issueList.Count; i++)
        {
            issueList[i].RunId = effectiveRunId;
            if (string.IsNullOrWhiteSpace(issueList[i].IssueId))
                issueList[i].IssueId = $"{effectiveRunId}-{(i + 1):D4}";
        }

        var sourceTool = issueList
            .Select(x => x.SourceTool)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        return new IssueReportDto
        {
            RunId = effectiveRunId,
            Title = title,
            SourceTools = sourceTool,
            ModelTitle = modelTitle,
            Issues = issueList,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Merges multiple reports into one. Each source report's issues keep their original IDs;
    /// a new overall run ID is generated for the merged report.
    /// </summary>
    public IssueReportDto MergeReports(string mergedTitle, IEnumerable<IssueReportDto> reports)
    {
        var allReports = reports.ToList();
        var allIssues = allReports.SelectMany(r => r.Issues).ToList();
        var modelTitles = allReports
            .Select(r => r.ModelTitle)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct()
            .ToList();

        return new IssueReportDto
        {
            RunId = BuildRunId(),
            Title = mergedTitle,
            SourceTools = allReports.SelectMany(r => r.SourceTools).Distinct().OrderBy(x => x).ToList(),
            ModelTitle = modelTitles.Count > 0 ? string.Join(", ", modelTitles) : null,
            Issues = allIssues,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>Returns issues sorted by Severity descending then CreatedAt ascending.</summary>
    public static IEnumerable<IssueDto> SortBySeverity(IEnumerable<IssueDto> issues) =>
        issues.OrderByDescending(i => i.Severity).ThenBy(i => i.CreatedAt);

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string BuildRunId()
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH-mm-ssZ");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return $"{stamp}_{suffix}";
    }
}
