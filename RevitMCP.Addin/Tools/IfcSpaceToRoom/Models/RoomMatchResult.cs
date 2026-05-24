namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

/// <summary>
/// Result of checking whether a native Revit Room already exists that matches an IFC Space.
/// Used internally by <c>RoomMatchService</c> during Phase 3 conversion.
/// Comparison uses built-in Room fields only (Level, Number, Name).
/// </summary>
public class RoomMatchResult
{
    /// <summary>True when a Room was found whose Number + Level + Name exactly match.</summary>
    public bool HasStrictMatch { get; set; }

    /// <summary>
    /// True when a Room was found whose Number + Level match but Name differs.
    /// Indicates a potential conflict that should be surfaced as a warning.
    /// </summary>
    public bool HasWarningMatch { get; set; }

    /// <summary>
    /// Revit element id of the conflicting Room (strict or warning match).
    /// Null when neither match type was found.
    /// </summary>
    public long? ExistingRoomId { get; set; }

    /// <summary>
    /// Human-readable description of the match type:
    /// "ExactMatch", "NumberConflict", or "NoMatch".
    /// </summary>
    public string MatchType { get; set; } = "NoMatch";

    /// <summary>
    /// Optional advisory message when <see cref="HasWarningMatch"/> is true.
    /// Null for exact matches and no-matches.
    /// </summary>
    public string? Warning { get; set; }
}
