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
    /// <summary>
    /// Reads report JSON with enums by name ("Warning") or number: exported reports use names, the tools' own output
    /// numbers, and the merge rejected names (M25).
    /// </summary>
    public static readonly System.Text.Json.JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public IssueReportDto MergeReports(string mergedTitle, IEnumerable<IssueReportDto> reports) =>
        MergeReports(mergedTitle, reports, out _);

    /// <summary>
    /// Merges the reports. An issue id seen before is the same issue (ids carry their run id), so it is kept once:
    /// merging a report with itself, or with a re-export of it, doubled every issue (M25). Issues without an id are
    /// all kept.
    /// </summary>
    public IssueReportDto MergeReports(string mergedTitle, IEnumerable<IssueReportDto> reports, out int duplicatesRemoved)
    {
        var allReports = reports.ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var allIssues = new List<IssueDto>();
        duplicatesRemoved = 0;
        foreach (var issue in allReports.SelectMany(r => r.Issues))
        {
            if (!string.IsNullOrWhiteSpace(issue.IssueId) && !seen.Add(issue.IssueId))
            {
                duplicatesRemoved++;
                continue;
            }
            allIssues.Add(issue);
        }
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
