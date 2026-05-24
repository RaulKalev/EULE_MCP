namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

/// <summary>
/// Per-space status constants for the <c>validate_ifc_space_room_conversion</c> endpoint.
/// Reflects both the match outcome and any detected anomaly.
/// </summary>
public static class ValidationStatus
{
    // ── Matched ────────────────────────────────────────────────────────────────

    /// <summary>Same Level + same Number + same Name. Geometry within tolerance (if checked).</summary>
    public const string HighConfidenceMatch = "HighConfidenceMatch";

    /// <summary>
    /// Same Level + same Number (Name differs), or same Level + same Name (Number differs),
    /// with location/area within tolerance.
    /// </summary>
    public const string MediumConfidenceMatch = "MediumConfidenceMatch";

    /// <summary>
    /// Same Level + location/area within tolerance, but Number and Name differ.
    /// </summary>
    public const string LowConfidenceMatch = "LowConfidenceMatch";

    // ── Anomalies ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Number + Name + Level match but location or area is outside tolerance.
    /// The Room may have been moved or the boundary changed after creation.
    /// </summary>
    public const string PossibleMoveOrGeometryChange = "PossibleMoveOrGeometryChange";

    /// <summary>
    /// Number matches on Level but Name is different. May be a deliberate rename.
    /// </summary>
    public const string PossibleRename = "PossibleRename";

    /// <summary>Name matches on Level but Number differs.</summary>
    public const string NumberMismatch = "NumberMismatch";

    /// <summary>Number matches but Level differs.</summary>
    public const string LevelMismatch = "LevelMismatch";

    // ── Problematic ───────────────────────────────────────────────────────────

    /// <summary>More than one Room is a plausible match for this IFC Space.</summary>
    public const string AmbiguousMatch = "AmbiguousMatch";

    /// <summary>No Room was found that matches this IFC Space on any Level.</summary>
    public const string MissingRoom = "MissingRoom";

    // ── Not processable ───────────────────────────────────────────────────────

    /// <summary>Geometry extraction failed — cannot do geometry-based comparison.</summary>
    public const string NoGeometry = "NoGeometry";

    /// <summary>No host Level could be matched to this element's elevation.</summary>
    public const string NoLevelMatch = "NoLevelMatch";

    /// <summary>An unexpected exception occurred during processing.</summary>
    public const string Error = "Error";
}
