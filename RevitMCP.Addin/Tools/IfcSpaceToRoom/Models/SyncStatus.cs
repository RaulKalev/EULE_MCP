namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

/// <summary>
/// Per-item outcome constants for the <c>sync_ifc_space_room_data</c> endpoint.
/// </summary>
public static class SyncStatus
{
    // ── Dry-run planned actions ────────────────────────────────────────────────

    /// <summary>Dry run: Name would be updated.</summary>
    public const string DryRunWouldUpdateName = "DryRunWouldUpdateName";

    /// <summary>Dry run: Number would be updated.</summary>
    public const string DryRunWouldUpdateNumber = "DryRunWouldUpdateNumber";

    /// <summary>Dry run: both Name and Number would be updated.</summary>
    public const string DryRunWouldUpdateNameAndNumber = "DryRunWouldUpdateNameAndNumber";

    // ── Applied updates ───────────────────────────────────────────────────────

    /// <summary>Room Name was updated successfully.</summary>
    public const string UpdatedName = "UpdatedName";

    /// <summary>Room Number was updated successfully.</summary>
    public const string UpdatedNumber = "UpdatedNumber";

    /// <summary>Both Room Name and Number were updated successfully.</summary>
    public const string UpdatedNameAndNumber = "UpdatedNameAndNumber";

    // ── No-op ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// All requested fields already match IFC values — no update was needed.
    /// </summary>
    public const string NoChangeNeeded = "NoChangeNeeded";

    // ── Blocked ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Blocked: match confidence is Low and <c>allowLowConfidenceUpdates</c> was not set.
    /// </summary>
    public const string BlockedLowConfidence = "BlockedLowConfidence";

    /// <summary>
    /// Blocked: match confidence is Medium and <c>allowMediumConfidenceUpdates</c> was not set.
    /// </summary>
    public const string BlockedMediumConfidence = "BlockedMediumConfidence";

    /// <summary>Blocked: match is Ambiguous — manual resolution required.</summary>
    public const string BlockedAmbiguousMatch = "BlockedAmbiguousMatch";

    /// <summary>Blocked: the specified Room does not exist in the host document.</summary>
    public const string BlockedRoomNotFound = "BlockedRoomNotFound";

    /// <summary>Blocked: the specified IFC Space does not exist in the linked document.</summary>
    public const string BlockedSpaceNotFound = "BlockedSpaceNotFound";

    /// <summary>
    /// Blocked: both <c>updateName</c> and <c>updateNumber</c> are false — nothing to do.
    /// </summary>
    public const string BlockedMissingSelection = "BlockedMissingSelection";

    /// <summary>Blocked: the new value would be empty and empty writes are not allowed.</summary>
    public const string BlockedEmptyValue = "BlockedEmptyValue";

    /// <summary>An unexpected exception occurred during this item's processing.</summary>
    public const string Error = "Error";
}
