namespace RevitMCP.Core.Models;

public enum ToolPermission
{
    /// <summary>
    /// Changes nothing: not the model, files, configuration, stored records, the UI selection or connector state
    /// (temporary model changes rolled back before it returns, and new export output, are allowed). The bridge
    /// advertises it as read-only; ReadOnlyFlagTests keeps both sides in step.
    /// </summary>
    ReadOnly,
    RequiresApproval,
    /// <summary>
    /// Changes something other than model elements (UI selection, active document, connector records or files, a job or
    /// plan) and runs without approval. Not read-only on the bridge.
    /// </summary>
    DirectEdit,
    /// <summary>Destructive operation — always requires manual approval even when Direct Edit mode is on.</summary>
    DestructiveRequiresManualApproval
}
