namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

/// <summary>
/// Caller-supplied options for the <c>sync_ifc_space_room_data</c> endpoint.
/// </summary>
public class RoomSyncOptions
{
    // ── Scope ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Revit element id of the RevitLinkInstance.
    /// Used to look up IFC Space elements when re-validating before writing.
    /// </summary>
    public long LinkInstanceId { get; set; }

    /// <summary>
    /// The set of Room updates to apply.
    /// Each item identifies one IFC Space and one native Room and specifies
    /// which built-in fields to update.
    /// </summary>
    public List<RoomSyncItemRequest> Items { get; set; } = new();

    // ── Confidence gates ───────────────────────────────────────────────────────

    /// <summary>
    /// When true, updates may proceed for Medium-confidence matches.
    /// Default false.
    /// </summary>
    public bool AllowMediumConfidenceUpdates { get; set; } = false;

    /// <summary>
    /// When true, updates may proceed for Low-confidence matches.
    /// Default false. Strongly discouraged — use with caution.
    /// </summary>
    public bool AllowLowConfidenceUpdates { get; set; } = false;

    // ── Safety ────────────────────────────────────────────────────────────────

    /// <summary>
    /// When true (default), perform all checks and return planned actions without
    /// modifying the model. Set to false to apply updates.
    /// </summary>
    public bool DryRun { get; set; } = true;
}
