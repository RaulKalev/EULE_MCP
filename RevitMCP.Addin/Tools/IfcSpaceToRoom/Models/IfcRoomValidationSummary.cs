namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

/// <summary>
/// Aggregate counts from a <c>validate_ifc_space_room_conversion</c> run.
/// </summary>
public class IfcRoomValidationSummary
{
    /// <summary>Total number of IFC Space candidates processed.</summary>
    public int TotalIfcSpaces { get; set; }

    /// <summary>Spaces with a High-confidence Room match.</summary>
    public int HighConfidenceMatches { get; set; }

    /// <summary>Spaces with a Medium-confidence Room match.</summary>
    public int MediumConfidenceMatches { get; set; }

    /// <summary>Spaces with a Low-confidence Room match.</summary>
    public int LowConfidenceMatches { get; set; }

    /// <summary>Spaces where no matching Room was found.</summary>
    public int MissingRooms { get; set; }

    /// <summary>Spaces where multiple Rooms could match (ambiguous).</summary>
    public int AmbiguousMatches { get; set; }

    /// <summary>
    /// Spaces with a High-confidence identity match but geometry differences
    /// (possible move or boundary change after creation).
    /// </summary>
    public int PossibleGeometryChanges { get; set; }

    /// <summary>Spaces with a possible Room rename detected (Number matches, Name differs).</summary>
    public int PossibleRenames { get; set; }

    /// <summary>
    /// Spaces that could not be processed (no Level match, no geometry, or unexpected error).
    /// </summary>
    public int Unprocessable { get; set; }

    /// <summary>True when results were truncated at maxResults.</summary>
    public bool Truncated { get; set; }
}
