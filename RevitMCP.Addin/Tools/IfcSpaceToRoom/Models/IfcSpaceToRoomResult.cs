namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

/// <summary>
/// Top-level JSON-serializable result for the <c>convert_ifc_spaces_to_rooms</c> tool.
/// Aggregates per-space outcomes and provides a run-level summary.
/// </summary>
public class IfcSpaceToRoomResult
{
    /// <summary>True when the operation completed without a fatal error.</summary>
    public bool Success { get; set; }

    /// <summary>Set when a fatal error prevented the run from starting (e.g. link not found).</summary>
    public string? Error { get; set; }

    /// <summary>Link instance id that was processed.</summary>
    public long LinkInstanceId { get; set; }

    /// <summary>True when the operation ran in dry-run mode (no model changes).</summary>
    public bool DryRun { get; set; }

    // ── Summary counters ─────────────────────────────────────────────────────

    /// <summary>Total number of spaces processed.</summary>
    public int TotalProcessed { get; set; }

    /// <summary>Number of Rooms successfully created (or DryRunReady in dry-run mode).</summary>
    public int CreatedRooms { get; set; }

    /// <summary>
    /// Number of Room Separation boundary loops created.
    /// Each space that got boundary lines counts as one loop, regardless of segment count.
    /// </summary>
    public int CreatedBoundaryLoops { get; set; }

    /// <summary>Number of spaces skipped because an identical Room already exists.</summary>
    public int SkippedExisting { get; set; }

    /// <summary>Number of spaces skipped with a conflict warning (same Number, different Name).</summary>
    public int SkippedWarnings { get; set; }

    /// <summary>Number of spaces that failed conversion (transaction rolled back per space).</summary>
    public int Failed { get; set; }

    // ── Per-space results ─────────────────────────────────────────────────────

    /// <summary>Detailed result for each processed IFC Space candidate.</summary>
    public List<IfcSpaceConversionItemResult> Items { get; set; } = new();
}
