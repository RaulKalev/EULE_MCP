namespace RevitMCP.Core.Models;

public class McpToolResult
{
    public string RequestId { get; set; } = string.Empty;
    public bool Success { get; set; }
    /// <summary>
    /// Machine-readable status for structured error handling.
    /// Values: approval_required | approval_rejected | approval_expired | approval_context_changed |
    /// queue_full | request_cancelled | request_timeout | revit_busy |
    /// tool_execution_failed | transaction_failed | validation_failed | unknown_tool |
    /// checkpoint_not_found | checkpoint_other_document | snapshot_too_large (checkpoints, #98) |
    /// plan_not_found | plan_not_active | plan_envelope_violated (plans, #99; plan_exhausted is a warning only) |
    /// job_not_found | job_not_resumable | job_step_invalid (jobs, #100)
    /// Null means a normal success or generic failure — inspect Success + Message.
    /// </summary>
    public string? Status { get; set; }
    public string Message { get; set; } = string.Empty;
    public object? Data { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public long DurationMs { get; set; }
}
