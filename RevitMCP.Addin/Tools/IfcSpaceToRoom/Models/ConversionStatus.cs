namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

/// <summary>
/// Per-space outcome constants for the <c>convert_ifc_spaces_to_rooms</c> tool.
/// Only one status is assigned to each candidate.
/// </summary>
public static class ConversionStatus
{
    // ── Success ────────────────────────────────────────────────────────────────

    /// <summary>A native Room was created and parameters written successfully.</summary>
    public const string Created = "Created";

    /// <summary>
    /// Dry-run mode: all checks passed and this space would be converted if dryRun=false.
    /// No model modifications were made.
    /// </summary>
    public const string DryRunReady = "DryRunReady";

    // ── Skipped (non-fatal, no action taken) ──────────────────────────────────

    /// <summary>An exact Number + Name + Level match exists — skipped to avoid duplicates.</summary>
    public const string SkippedExisting = "SkippedExisting";

    /// <summary>
    /// A Room with the same Number on the same Level already exists but with a different Name.
    /// Skipped by default; surfaced as a warning for operator review.
    /// </summary>
    public const string SkippedDuplicateWarning = "SkippedDuplicateWarning";

    /// <summary>IFC Space has no Room Name and <c>allowCreateWithoutName</c> is false.</summary>
    public const string SkippedMissingName = "SkippedMissingName";

    /// <summary>IFC Space has no Room Number and <c>allowCreateWithoutNumber</c> is false.</summary>
    public const string SkippedMissingNumber = "SkippedMissingNumber";

    /// <summary>No host Level could be matched within the elevation tolerance.</summary>
    public const string SkippedNoLevelMatch = "SkippedNoLevelMatch";

    /// <summary>Geometry extraction returned a non-GeometryReady status (no valid footprint).</summary>
    public const string SkippedInvalidGeometry = "SkippedInvalidGeometry";

    /// <summary>
    /// No suitable floor-plan view was found and none could be created for the matched Level.
    /// Room boundary lines cannot be created without a view.
    /// </summary>
    public const string SkippedNoView = "SkippedNoView";

    // ── Failed (transaction was attempted but rolled back) ────────────────────

    /// <summary>Room Separation Lines could not be created for the footprint boundary.</summary>
    public const string FailedBoundaryCreation = "FailedBoundaryCreation";

    /// <summary>The Room element could not be placed at the computed placement point.</summary>
    public const string FailedRoomCreation = "FailedRoomCreation";

    /// <summary>The Room was placed but Number / Name could not be written.</summary>
    public const string FailedParameterWrite = "FailedParameterWrite";

    /// <summary>An unexpected exception occurred during conversion of this space.</summary>
    public const string Error = "Error";
}
