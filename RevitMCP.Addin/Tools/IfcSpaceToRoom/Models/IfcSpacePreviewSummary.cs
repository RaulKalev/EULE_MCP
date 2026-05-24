namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

/// <summary>
/// Aggregated counts from one preview_ifc_spaces operation.
/// </summary>
public class IfcSpacePreviewSummary
{
    /// <summary>Total number of IFC Space candidates found in the linked document.</summary>
    public int TotalCandidates { get; set; }

    /// <summary>Candidates with status = Ready (can be converted in a later Phase).</summary>
    public int Ready { get; set; }

    /// <summary>Candidates where an exact matching Room already exists in the host document.</summary>
    public int AlreadyExists { get; set; }

    /// <summary>Candidates with status = Warning (usable but advisory conditions present).</summary>
    public int Warnings { get; set; }

    /// <summary>Candidates with status = MissingMetadata.</summary>
    public int MissingMetadata { get; set; }

    /// <summary>Candidates with status = NoLevelMatch.</summary>
    public int NoLevelMatch { get; set; }

    /// <summary>Candidates that failed processing (exceptions or unrecoverable errors).</summary>
    public int Failed { get; set; }

    /// <summary>True if the result set was truncated due to MaxResults.</summary>
    public bool Truncated { get; set; }
}
