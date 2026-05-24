namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

/// <summary>
/// JSON-serializable result for one Room in the <c>sync_ifc_space_room_data</c> endpoint.
/// No Revit API types — safe for pipe serialization.
/// </summary>
public class RoomSyncItemResult
{
    // ── Identity ──────────────────────────────────────────────────────────────

    /// <summary>Revit element id of the IFC Space in the linked document.</summary>
    public long LinkedElementId { get; set; }

    /// <summary>Revit element id of the native Room in the host document.</summary>
    public long RoomId { get; set; }

    // ── Outcome ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Sync outcome for this item. See <see cref="SyncStatus"/> for all possible values.
    /// </summary>
    public string Status { get; set; } = SyncStatus.Error;

    /// <summary>Confidence level used during safety re-validation.</summary>
    public string Confidence { get; set; } = RoomMatchConfidence.None;

    // ── Name change ───────────────────────────────────────────────────────────

    /// <summary>Room Name before the update. Null if Name was not involved.</summary>
    public string? OldName { get; set; }

    /// <summary>Target Room Name (from IFC). Null if Name was not requested.</summary>
    public string? NewName { get; set; }

    // ── Number change ─────────────────────────────────────────────────────────

    /// <summary>Room Number before the update. Null if Number was not involved.</summary>
    public string? OldNumber { get; set; }

    /// <summary>Target Room Number (from IFC). Null if Number was not requested.</summary>
    public string? NewNumber { get; set; }

    // ── Diagnostics ───────────────────────────────────────────────────────────

    /// <summary>Advisory messages about this item.</summary>
    public List<string> Warnings { get; set; } = new();
}
