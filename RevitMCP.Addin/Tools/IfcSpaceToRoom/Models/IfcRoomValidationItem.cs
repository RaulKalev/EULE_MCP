namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

/// <summary>
/// JSON-serializable validation result for one IFC Space candidate.
/// No Revit API types — safe for pipe serialization.
/// </summary>
public class IfcRoomValidationItem
{
    // ── IFC Space identity ────────────────────────────────────────────────────

    /// <summary>Revit element id of the linked IFC Space element.</summary>
    public long LinkedElementId { get; set; }

    /// <summary>Room number read from IFC parameters. Null if not found.</summary>
    public string? IfcNumber { get; set; }

    /// <summary>Room name read from IFC parameters. Null if not found.</summary>
    public string? IfcName { get; set; }

    /// <summary>Name of the matched host Level. Null if Level matching failed.</summary>
    public string? TargetLevelName { get; set; }

    /// <summary>Revit element id of the matched host Level. Null if Level matching failed.</summary>
    public long? TargetLevelId { get; set; }

    // ── Validation outcome ────────────────────────────────────────────────────

    /// <summary>
    /// Validation status. See <see cref="ValidationStatus"/> for all possible values.
    /// </summary>
    public string Status { get; set; } = ValidationStatus.Error;

    /// <summary>
    /// Match confidence level. See <see cref="RoomMatchConfidence"/> for all possible values.
    /// </summary>
    public string Confidence { get; set; } = RoomMatchConfidence.None;

    // ── Matched Room ──────────────────────────────────────────────────────────

    /// <summary>Revit element id of the best-matching native Room. Null if no match found.</summary>
    public long? MatchedRoomId { get; set; }

    /// <summary>Room Number of the matched Room. Null if no match.</summary>
    public string? MatchedRoomNumber { get; set; }

    /// <summary>Room Name of the matched Room. Null if no match.</summary>
    public string? MatchedRoomName { get; set; }

    // ── Geometry comparison ───────────────────────────────────────────────────

    /// <summary>
    /// 2D distance between IFC Space placement point and matched Room location point (mm).
    /// Null if geometry comparison was not performed or placement point not available.
    /// </summary>
    public double? LocationDeltaMm { get; set; }

    /// <summary>
    /// Relative difference between IFC Space area and matched Room area (percent).
    /// Null if geometry comparison was not performed or area not available.
    /// </summary>
    public double? AreaDeltaPercent { get; set; }

    /// <summary>Approximate IFC Space area in m². Null if not computed.</summary>
    public double? IfcAreaM2 { get; set; }

    /// <summary>Matched Room area in m². Null if no match or Room is unplaced.</summary>
    public double? RoomAreaM2 { get; set; }

    // ── Recommendation ────────────────────────────────────────────────────────

    /// <summary>
    /// Suggested follow-up action:
    ///   "None"               — match is clean, no action needed.
    ///   "ReviewGeometry"     — identity matches but geometry differs.
    ///   "ReviewAndSync"      — medium-confidence match, review before syncing.
    ///   "ReviewManually"     — low-confidence or ambiguous, manual review required.
    ///   "ReviewAndResolve"   — ambiguous match, must be resolved before any update.
    ///   "CreateRoom"         — no matching Room found; consider running Phase 3.
    /// </summary>
    public string RecommendedAction { get; set; } = "None";

    // ── Diagnostics ───────────────────────────────────────────────────────────

    /// <summary>Advisory messages about this element.</summary>
    public List<string> Warnings { get; set; } = new();
}
