using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Approval;

/// <summary>
/// Represents a tool request that requires user approval before execution.
/// Holds the original request, a completion source for the pipe response,
/// and a human-readable summary for the UI.
/// </summary>
public class PendingApprovalRequest
{
    public string ApprovalId { get; set; } = Guid.NewGuid().ToString();
    public McpToolRequest OriginalRequest { get; set; } = null!;
    public TaskCompletionSource<McpToolResult> Completion { get; set; } = null!;
    public string ToolName { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>
    /// In-process identity token for the document active when approval was requested.
    /// This is deliberately typed as object so the approval model stays testable
    /// without referencing Autodesk assemblies.
    /// </summary>
    public bool IsDocumentBound { get; set; }
    public object? OriginDocumentToken { get; set; }
    public string OriginDocumentTitle { get; set; } = string.Empty;
    public long OriginDocumentVersion { get; set; }
    public bool IsSelectionBound { get; set; }
    public IReadOnlyList<long> OriginSelectionIds { get; set; } = Array.Empty<long>();
}
