using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Interfaces;

/// <summary>
/// A <see cref="ToolPermission.RequiresApproval"/> tool whose arguments decide whether a call needs approval,
/// e.g. a memory note: shared with the project it is approved, kept for the user alone it is not.
/// Never relaxes <see cref="ToolPermission.DestructiveRequiresManualApproval"/>.
/// </summary>
public interface IConditionalApprovalTool
{
    bool NeedsApproval(McpToolRequest request);
}
