namespace RevitMCP.Core.Models;

public class McpToolResult
{
    public string RequestId { get; set; } = string.Empty;
    public bool Success { get; set; }
    /// <summary>
    /// Machine-readable status for structured error handling.
    /// Values: approval_required | approval_rejected | revit_busy | transaction_failed | validation_failed | unknown_tool
    /// Null means a normal success or generic failure — inspect Success + Message.
    /// </summary>
    public string? Status { get; set; }
    public string Message { get; set; } = string.Empty;
    public object? Data { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public long DurationMs { get; set; }
}
