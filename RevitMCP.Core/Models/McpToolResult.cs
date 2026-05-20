namespace RevitMCP.Core.Models;

public class McpToolResult
{
    public string RequestId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public object? Data { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public long DurationMs { get; set; }
}
