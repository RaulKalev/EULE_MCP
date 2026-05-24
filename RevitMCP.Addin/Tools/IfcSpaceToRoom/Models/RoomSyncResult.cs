namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

/// <summary>
/// Top-level JSON-serializable result for the <c>sync_ifc_space_room_data</c> endpoint.
/// </summary>
public class RoomSyncResult
{
    /// <summary>True when the operation completed without a fatal error.</summary>
    public bool Success { get; set; }

    /// <summary>Set when a fatal error prevented the run from starting.</summary>
    public string? Error { get; set; }

    /// <summary>True when the operation ran in dry-run mode (no model changes).</summary>
    public bool DryRun { get; set; }

    // ── Summary counters ─────────────────────────────────────────────────────

    /// <summary>Total number of items submitted.</summary>
    public int TotalItems { get; set; }

    /// <summary>Number of Rooms successfully updated (or planned in dry-run).</summary>
    public int Updated { get; set; }

    /// <summary>Number of items that required no change (values already matched).</summary>
    public int NoChangeNeeded { get; set; }

    /// <summary>Number of items blocked by safety rules or confidence gates.</summary>
    public int Blocked { get; set; }

    /// <summary>Number of items that failed with an error.</summary>
    public int Failed { get; set; }

    // ── Per-item results ──────────────────────────────────────────────────────

    /// <summary>Detailed result for each submitted sync item.</summary>
    public List<RoomSyncItemResult> Items { get; set; } = new();
}
