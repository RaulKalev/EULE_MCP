namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

/// <summary>
/// Lightweight DTO describing a single RevitLinkInstance that may contain IFC-derived elements.
/// Phase 1: read-only, no modifications.
/// </summary>
public class IfcLinkInfo
{
    /// <summary>Revit element id of the RevitLinkInstance in the host document.</summary>
    public long LinkInstanceId { get; set; }

    /// <summary>Name of the link instance as shown in Revit.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Title of the linked document (may be null if unloaded).</summary>
    public string DocumentTitle { get; set; } = string.Empty;

    /// <summary>True if the linked document is currently loaded and accessible.</summary>
    public bool IsLoaded { get; set; }

    /// <summary>
    /// True when the link is identified as IFC-derived based on name/path heuristics.
    /// Does not guarantee the link actually contains IfcSpace elements.
    /// </summary>
    public bool IsLikelyIfc { get; set; }

    /// <summary>True if a valid total transform could be obtained for coordinate mapping.</summary>
    public bool TransformAvailable { get; set; }
}
