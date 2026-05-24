namespace RevitMCP.Core.Safety;

/// <summary>
/// Structured result returned by safety guards when a request cannot be fulfilled safely.
/// Tools convert this to a <c>McpToolResult { Success=false }</c> and set it as the Data payload.
/// </summary>
public sealed class SafeToolResult
{
    public bool Success { get; set; }

    /// <summary>Machine-readable error code from <see cref="ToolErrorCodes"/>.</summary>
    public string? Error { get; set; }

    public string? Message { get; set; }

    /// <summary>Optional structured detail about the rejected request (e.g., measured sizes).</summary>
    public object? Data { get; set; }

    /// <summary>Human-readable remediation suggestions shown to the AI model.</summary>
    public string[] SuggestedActions { get; set; } = Array.Empty<string>();
}
