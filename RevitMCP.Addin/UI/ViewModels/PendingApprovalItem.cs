namespace RevitMCP.Addin.UI.ViewModels;

/// <summary>
/// Lightweight UI display model for a pending approval request,
/// shown in the Pending tab of the MCP window.
/// </summary>
public class PendingApprovalItem
{
    /// <summary>Internal ID used to approve/reject this item.</summary>
    public string ApprovalId { get; init; } = string.Empty;

    /// <summary>HH:mm:ss timestamp when the request arrived.</summary>
    public string Time { get; init; } = string.Empty;

    /// <summary>Tool name (e.g. revit_set_parameter).</summary>
    public string Tool { get; init; } = string.Empty;

    /// <summary>Human-readable description of what the tool will do.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Client that sent the request (e.g. "Claude Code").</summary>
    public string Client { get; init; } = string.Empty;

    /// <summary>Document instance the approval is bound to.</summary>
    public string Model { get; init; } = string.Empty;
}
