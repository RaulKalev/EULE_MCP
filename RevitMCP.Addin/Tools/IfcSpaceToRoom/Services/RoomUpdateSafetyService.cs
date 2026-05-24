using RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>
/// Enforces safety rules before a Room built-in field update is applied.
/// Called once per item inside <c>ControlledRoomSyncService</c>.
///
/// Blocked conditions:
///   - Match is Ambiguous
///   - Requested Room does not exist
///   - IFC Space does not exist
///   - Neither updateName nor updateNumber is selected
///   - Requested new value is empty and empty writes are not allowed
///   - Match confidence is Medium and allowMediumConfidenceUpdates = false
///   - Match confidence is Low and allowLowConfidenceUpdates = false
///
/// Forbidden writes (always):
///   - Shared parameters
///   - Comments
///   - Extensible Storage
///   - IFC GUIDs
///   - Room boundaries
///   - Room deletion
/// </summary>
public static class RoomUpdateSafetyService
{
    /// <summary>
    /// Checks all safety gates for one sync item.
    /// </summary>
    /// <param name="request">The item to check.</param>
    /// <param name="confidence">Re-computed match confidence for this item.</param>
    /// <param name="allowMediumConfidence">Whether medium-confidence updates are allowed.</param>
    /// <param name="allowLowConfidence">Whether low-confidence updates are allowed.</param>
    /// <param name="ifcSpaceFound">Whether the IFC Space element was found in the linked document.</param>
    /// <param name="roomFound">Whether the Room element was found in the host document.</param>
    /// <param name="targetName">Target Room Name (from IFC). Null/empty if Name is not being updated.</param>
    /// <param name="targetNumber">Target Room Number (from IFC). Null/empty if Number is not being updated.</param>
    /// <param name="blockStatus">
    /// Output: the <see cref="SyncStatus"/> constant describing why this item was blocked.
    /// Null when no safety gate triggered.
    /// </param>
    /// <returns>True when the update is safe to proceed; false when blocked.</returns>
    public static bool Check(
        RoomSyncItemRequest request,
        string confidence,
        bool allowMediumConfidence,
        bool allowLowConfidence,
        bool ifcSpaceFound,
        bool roomFound,
        string? targetName,
        string? targetNumber,
        out string? blockStatus)
    {
        blockStatus = null;

        // ── 1. Room must exist ─────────────────────────────────────────────────
        if (!roomFound)
        {
            blockStatus = SyncStatus.BlockedRoomNotFound;
            return false;
        }

        // ── 2. IFC Space must exist ────────────────────────────────────────────
        if (!ifcSpaceFound)
        {
            blockStatus = SyncStatus.BlockedSpaceNotFound;
            return false;
        }

        // ── 3. At least one field must be selected ─────────────────────────────
        if (!request.UpdateName && !request.UpdateNumber)
        {
            blockStatus = SyncStatus.BlockedMissingSelection;
            return false;
        }

        // ── 4. Ambiguous matches are always blocked ────────────────────────────
        if (confidence == RoomMatchConfidence.Ambiguous)
        {
            blockStatus = SyncStatus.BlockedAmbiguousMatch;
            return false;
        }

        // ── 5. Confidence gates ────────────────────────────────────────────────
        if (confidence == RoomMatchConfidence.Medium && !allowMediumConfidence)
        {
            blockStatus = SyncStatus.BlockedMediumConfidence;
            return false;
        }

        if (confidence == RoomMatchConfidence.Low && !allowLowConfidence)
        {
            blockStatus = SyncStatus.BlockedLowConfidence;
            return false;
        }

        if (confidence == RoomMatchConfidence.None)
        {
            // No match found at all — cannot safely update
            blockStatus = SyncStatus.BlockedLowConfidence;
            return false;
        }

        // ── 6. Target values must not be empty ─────────────────────────────────
        if (request.UpdateName   && string.IsNullOrWhiteSpace(targetName))
        {
            blockStatus = SyncStatus.BlockedEmptyValue;
            return false;
        }

        if (request.UpdateNumber && string.IsNullOrWhiteSpace(targetNumber))
        {
            blockStatus = SyncStatus.BlockedEmptyValue;
            return false;
        }

        return true;
    }
}
