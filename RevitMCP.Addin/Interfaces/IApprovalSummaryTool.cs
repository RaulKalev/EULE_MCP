using Autodesk.Revit.UI;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Interfaces;

/// <summary>
/// A tool whose approval summary needs the live model (e.g. how many elements a rollback deletes), built on the
/// Revit API thread when the request is parked for approval. Returning null falls back to ApprovalSummaryBuilder.
/// </summary>
public interface IApprovalSummaryTool
{
    string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request);
}
