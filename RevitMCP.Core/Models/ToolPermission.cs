namespace RevitMCP.Core.Models;

public enum ToolPermission
{
    ReadOnly,
    RequiresApproval,
    DirectEdit,
    /// <summary>Destructive operation — always requires manual approval even when Direct Edit mode is on.</summary>
    DestructiveRequiresManualApproval
}
