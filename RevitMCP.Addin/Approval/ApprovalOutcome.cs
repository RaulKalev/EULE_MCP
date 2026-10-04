using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Approval;

/// <summary>
/// What happened to one approval-gated request after <c>approval_required</c> was returned. The
/// caller's pipe call ends at that point, so without this record an approved request that failed
/// (model changed, expired, Revit busy, transaction error) was only visible in the activity log.
/// </summary>
public sealed class ApprovalOutcome
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Rejected = "rejected";

    public string ApprovalId { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>pending | running | succeeded | failed | rejected.</summary>
    public string State { get; internal set; } = Pending;
    public DateTimeOffset? DecidedAt { get; internal set; }
    public DateTimeOffset? CompletedAt { get; internal set; }

    /// <summary>The tool's result once it finished (or why it never ran).</summary>
    public McpToolResult? Result { get; internal set; }

    public bool IsFinal => State is Succeeded or Failed or Rejected;

    /// <summary>The request's completion; read directly so a lookup never trails the continuation.</summary>
    internal Task<McpToolResult>? Completion { get; set; }

    /// <summary>Applies the completion's result if it finished but the continuation has not run yet.</summary>
    internal void Refresh()
    {
        if (!IsFinal && Completion is { IsCompleted: true } task)
            Complete(ResultOf(task, RequestId), DateTimeOffset.Now);
    }

    internal static McpToolResult ResultOf(Task<McpToolResult> task, string requestId) =>
        task.Status == TaskStatus.RanToCompletion
            ? task.Result
            : new McpToolResult
            {
                RequestId = requestId,
                Success = false,
                Status = "tool_execution_failed",
                Message = task.Exception?.GetBaseException().Message ?? "The approved request did not complete."
            };

    internal void Complete(McpToolResult result, DateTimeOffset now)
    {
        if (IsFinal) return;
        Result = result;
        CompletedAt = now;
        State = result.Success ? Succeeded
            : result.Status == "approval_rejected" ? Rejected
            : Failed;
    }
}
