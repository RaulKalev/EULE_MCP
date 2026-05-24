namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

/// <summary>
/// Aggregated counts from one <c>preview_ifc_space_geometry</c> operation.
/// </summary>
public class IfcGeometryPreviewSummary
{
    /// <summary>Total number of IFC Space candidates that were attempted.</summary>
    public int TotalCandidates { get; set; }

    /// <summary>Candidates with a fully usable footprint (status = GeometryReady).</summary>
    public int GeometryReady { get; set; }

    /// <summary>Candidates with advisory warnings but still usable.</summary>
    public int WithWarnings { get; set; }

    /// <summary>Candidates where no solid geometry could be found.</summary>
    public int NoSolidGeometry { get; set; }

    /// <summary>Candidates where no horizontal bottom face could be found.</summary>
    public int NoUsableBottomFace { get; set; }

    /// <summary>Candidates where loop extraction or validation failed.</summary>
    public int InvalidLoop { get; set; }

    /// <summary>Candidates that failed due to an unexpected error.</summary>
    public int Failed { get; set; }

    /// <summary>True when the result set was truncated due to MaxResults.</summary>
    public bool Truncated { get; set; }
}
