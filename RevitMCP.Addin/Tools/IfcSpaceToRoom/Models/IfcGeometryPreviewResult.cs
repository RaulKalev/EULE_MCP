namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

/// <summary>
/// Full response from the <c>preview_ifc_space_geometry</c> MCP endpoint.
/// Phase 2: read-only geometry preview.
/// </summary>
public class IfcGeometryPreviewResult
{
    /// <summary>True when the operation completed without a fatal error.</summary>
    public bool Success { get; set; }

    /// <summary>Revit element id of the RevitLinkInstance that was inspected.</summary>
    public long LinkInstanceId { get; set; }

    /// <summary>Per-space geometry extraction results.</summary>
    public List<IfcGeometryItem> Items { get; set; } = new();

    /// <summary>Aggregated counts across all candidates.</summary>
    public IfcGeometryPreviewSummary Summary { get; set; } = new();

    /// <summary>Human-readable error message when Success = false.</summary>
    public string? Error { get; set; }
}
