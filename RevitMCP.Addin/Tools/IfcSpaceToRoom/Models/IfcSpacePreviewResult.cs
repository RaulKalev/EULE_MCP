namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

/// <summary>
/// Full result returned by the preview_ifc_spaces MCP endpoint.
/// Phase 1: read-only; no modifications to the host Revit document.
/// </summary>
public class IfcSpacePreviewResult
{
    /// <summary>True when the operation completed without a fatal error.</summary>
    public bool Success { get; set; }

    /// <summary>Revit element id of the RevitLinkInstance that was inspected.</summary>
    public long LinkInstanceId { get; set; }

    /// <summary>Detected IFC Space candidates with metadata and readiness status.</summary>
    public List<IfcSpaceCandidate> Spaces { get; set; } = new();

    /// <summary>Aggregated counts across all candidates.</summary>
    public IfcSpacePreviewSummary Summary { get; set; } = new();

    /// <summary>Human-readable error message when Success = false.</summary>
    public string? Error { get; set; }
}
