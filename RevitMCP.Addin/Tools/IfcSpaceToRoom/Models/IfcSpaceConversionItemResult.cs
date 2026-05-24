namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

/// <summary>
/// JSON-serializable result for one IFC Space candidate from the
/// <c>convert_ifc_spaces_to_rooms</c> tool.
/// No Revit API types — safe for pipe serialization.
/// </summary>
public class IfcSpaceConversionItemResult
{
    // ── Identity ──────────────────────────────────────────────────────────────

    /// <summary>Revit element id of the linked IFC Space element.</summary>
    public long LinkedElementId { get; set; }

    // ── IFC metadata ──────────────────────────────────────────────────────────

    /// <summary>Room number read from IFC parameters. Null if not found.</summary>
    public string? Number { get; set; }

    /// <summary>Room name read from IFC parameters. Null if not found.</summary>
    public string? Name { get; set; }

    /// <summary>Name of the matched host Level. Null if level matching failed.</summary>
    public string? TargetLevelName { get; set; }

    // ── Outcome ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Conversion outcome. See <see cref="ConversionStatus"/> for all possible values.
    /// </summary>
    public string Status { get; set; } = ConversionStatus.Error;

    /// <summary>
    /// Revit element id of the newly created Room.
    /// Null for all statuses except <c>Created</c>.
    /// </summary>
    public long? CreatedRoomId { get; set; }

    /// <summary>
    /// Revit element id of the pre-existing Room that caused a skip.
    /// Set for <c>SkippedExisting</c> and <c>SkippedDuplicateWarning</c>.
    /// </summary>
    public long? ExistingRoomId { get; set; }

    /// <summary>
    /// Number of Room Separation Line segments created for this space.
    /// 0 when no boundary lines were created (skip, dry-run, or failure).
    /// </summary>
    public int CreatedBoundaryLineCount { get; set; }

    // ── Diagnostics ───────────────────────────────────────────────────────────

    /// <summary>Non-blocking advisory messages.</summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>Blocking error messages that caused a failure or skip.</summary>
    public List<string> Errors { get; set; } = new();
}
