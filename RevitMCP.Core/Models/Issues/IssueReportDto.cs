namespace RevitMCP.Core.Models.Issues;

public class IssueReportDto
{
    /// <summary>Unique run ID used as prefix for all IssueDto.IssueId values in this report.</summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>Human-readable title for this report.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Tools that contributed to this report.</summary>
    public List<string> SourceTools { get; set; } = new();

    /// <summary>Revit model title (if applicable).</summary>
    public string? ModelTitle { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<IssueDto> Issues { get; set; } = new();

    // Convenience counts
    public int TotalCount => Issues.Count;
    public int CriticalCount => Issues.Count(i => i.Severity == IssueSeverity.Critical);
    public int ErrorCount => Issues.Count(i => i.Severity == IssueSeverity.Error);
    public int WarningCount => Issues.Count(i => i.Severity == IssueSeverity.Warning);
    public int InfoCount => Issues.Count(i => i.Severity == IssueSeverity.Info);
}
