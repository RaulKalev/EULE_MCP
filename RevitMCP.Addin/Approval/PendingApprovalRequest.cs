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
}
