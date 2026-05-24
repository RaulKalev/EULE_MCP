namespace RevitMCP.Core.Models.Issues;

public class IssueDto
{
    /// <summary>Unique identifier in the format &lt;runId&gt;-&lt;NNNN&gt;.</summary>
    public string IssueId { get; set; } = string.Empty;

    /// <summary>Name of the tool that produced this issue.</summary>
    public string SourceTool { get; set; } = string.Empty;

    /// <summary>Run correlation ID (matches IssueReportDto.RunId).</summary>
    public string RunId { get; set; } = string.Empty;

    public IssueSeverity Severity { get; set; } = IssueSeverity.Warning;
    public IssueStatus Status { get; set; } = IssueStatus.Open;

    /// <summary>Broad category, e.g. "SheetNaming", "ParameterQA", "Delivery", "ClashDetection".</summary>
    public string Category { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? SuggestedFix { get; set; }

    // Revit context (optional)
    public long? ElementId { get; set; }
    public long? LinkedElementId { get; set; }

    // File/sheet context (optional)
    public string? FilePath { get; set; }
    public string? SheetNumber { get; set; }
    public string? SheetName { get; set; }

    // Discipline / phase context (optional, e.g. "EL", "EN", "EA", "EP", "PP", "TP")
    public string? Discipline { get; set; }
    public string? Phase { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
