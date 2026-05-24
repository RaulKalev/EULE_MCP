namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

/// <summary>
/// Caller-supplied options for the <c>convert_ifc_spaces_to_rooms</c> operation.
/// All dimension tolerances are in millimetres for human readability; services convert
/// to Revit internal feet as needed.
/// </summary>
public class IfcSpaceToRoomOptions
{
    // ── Scope ──────────────────────────────────────────────────────────────────

    /// <summary>Revit element id of the RevitLinkInstance to convert from.</summary>
    public long LinkInstanceId { get; set; }

    /// <summary>
    /// Optional filter — if non-empty, only these linked element IDs are processed.
    /// If empty, all IFC Space candidates in the link are processed.
    /// </summary>
    public List<long> LinkedElementIds { get; set; } = new();

    // ── Duplicate handling ─────────────────────────────────────────────────────

    /// <summary>
    /// How to handle duplicate / conflicting Rooms (same Number + Level):
    ///   "skip_existing"  — (default) skip when Number+Name+Level exactly match,
    ///                      AND skip when Number+Level match but Name differs.
    ///   "skip_conflicts" — alias for skip_existing; kept for backwards compatibility.
    ///   "allow_conflicts" — only skip exact Number+Name+Level matches;
    ///                       allows creation when Number+Level match but Name differs.
    /// Phase 3 does NOT support "overwrite" or "recreate".
    /// </summary>
    public string DuplicateMode { get; set; } = "skip_existing";

    // ── Tolerances ─────────────────────────────────────────────────────────────

    /// <summary>Maximum vertical offset (mm) for matching an IFC Space to a host Level. Default 300 mm.</summary>
    public double LevelMatchToleranceMm { get; set; } = 300.0;

    /// <summary>Maximum gap (mm) between consecutive curve endpoints for snapping. Default 3 mm.</summary>
    public double EndpointSnapToleranceMm { get; set; } = 3.0;

    /// <summary>Curves shorter than this (mm) are removed during loop cleanup. Default 1 mm.</summary>
    public double TinySegmentToleranceMm { get; set; } = 1.0;

    // ── Room data ──────────────────────────────────────────────────────────────

    /// <summary>
    /// When true, copy Number and Name from IFC Space metadata to the created Room.
    /// Only built-in Room fields are written — never shared parameters.
    /// Default true.
    /// </summary>
    public bool SetRoomNameAndNumber { get; set; } = true;

    /// <summary>
    /// When true, create Room Separation Lines from the IFC Space footprint loop
    /// before placing the Room. Recommended to ensure Revit can bound the Room.
    /// Default true.
    /// </summary>
    public bool CreateRoomSeparationLines { get; set; } = true;

    // ── Guards ────────────────────────────────────────────────────────────────

    /// <summary>
    /// When false (default), skip spaces whose IFC Name is empty or whitespace.
    /// Set to true to create un-named Rooms.
    /// </summary>
    public bool AllowCreateWithoutName { get; set; } = false;

    /// <summary>
    /// When false (default), skip spaces whose IFC Number is empty or whitespace.
    /// Set to true to create un-numbered Rooms.
    /// </summary>
    public bool AllowCreateWithoutNumber { get; set; } = false;

    // ── View creation ─────────────────────────────────────────────────────────

    /// <summary>
    /// When false (default), do not create floor-plan views for levels that have none.
    /// If no view exists for a Level, that Level's spaces return <c>SkippedNoView</c>.
    /// When true, a minimal floor-plan view named
    /// "MCP IFC Room Boundary - {LevelName}" is created automatically.
    /// </summary>
    public bool AllowCreateMissingBoundaryViews { get; set; } = false;

    // ── Probable candidate guard ──────────────────────────────────────────────

    /// <summary>
    /// When false (default), skip auto-collected elements whose IFC type could not be
    /// confirmed as IfcSpace (probable candidates only). Applies only when
    /// <see cref="LinkedElementIds"/> is empty (auto-collect mode).
    /// Explicit <see cref="LinkedElementIds"/> bypass this guard.
    /// </summary>
    public bool AllowProbableConversion { get; set; } = false;

    // ── Dry run ───────────────────────────────────────────────────────────────

    /// <summary>
    /// When true, perform all validation and matching but make no model modifications.
    /// Items that pass all checks are returned with status <c>DryRunReady</c>.
    /// Default false.
    /// </summary>
    public bool DryRun { get; set; } = false;
}
