namespace RevitMCP.Core.Models;

public class McpToolRequest
{
    public string RequestId { get; set; } = Guid.NewGuid().ToString();
    public string ToolName { get; set; } = string.Empty;
    public ToolPermission Permission { get; set; }
    public Dictionary<string, object?> Arguments { get; set; } = new();
    public string ClientName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>
    /// Set to true after user approves a RequiresApproval tool request.
    /// When true, ExternalEventHandler executes the tool without re-prompting.
    /// </summary>
    public bool IsApproved { get; set; }
}
