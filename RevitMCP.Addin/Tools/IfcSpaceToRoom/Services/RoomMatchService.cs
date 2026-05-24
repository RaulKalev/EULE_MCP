using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>
/// Checks whether a native Revit Room already exists that conflicts with an IFC Space
/// being converted. Comparison uses built-in Room fields only (Level + Number + Name).
/// No shared parameters are read or written. No model modifications.
/// Phase 3: used during conversion to enforce the "no overwrite" rule.
/// </summary>
public class RoomMatchService
{
    // ── Cached room snapshot (populated once per conversion run) ──────────────

    private record RoomSnapshot(long RoomId, long LevelId, string Number, string Name);

    private List<RoomSnapshot>? _rooms;

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pre-loads all placed Rooms from <paramref name="hostDoc"/> for efficient repeated matching.
    /// Must be called once before any <see cref="Check"/> calls.
    /// </summary>
    public void LoadRooms(Document hostDoc)
    {
        _rooms = new FilteredElementCollector(hostDoc)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .Cast<Room>()
            .Where(r => r.Area > 0) // placed rooms only — Area == 0 means unplaced
            .Select(r => new RoomSnapshot(
                RoomId:  r.Id.Value,
                LevelId: GetRoomLevelId(r),
                Number:  (r.Number ?? string.Empty).Trim(),
                Name:    (r.Name   ?? string.Empty).Trim()
            ))
            .ToList();
    }

    /// <summary>
    /// Checks whether a Room matching the given identity already exists.
    /// </summary>
    /// <param name="targetLevelId">Matched host Level element id.</param>
    /// <param name="number">IFC Space room number (may be null/empty).</param>
    /// <param name="name">IFC Space room name (may be null/empty).</param>
    /// <returns>
    /// A <see cref="RoomMatchResult"/> indicating whether a strict or warning match was found.
    /// </returns>
    public RoomMatchResult Check(long? targetLevelId, string? number, string? name)
    {
        if (_rooms == null)
            throw new InvalidOperationException("Call LoadRooms() before Check().");

        var result = new RoomMatchResult { MatchType = "NoMatch" };

        if (targetLevelId == null)
            return result;

        var num = (number ?? string.Empty).Trim();
        var nm  = (name   ?? string.Empty).Trim();

        // Cannot do a meaningful identity match without at least a room number
        if (string.IsNullOrEmpty(num))
            return result;

        long levelId = targetLevelId.Value;

        foreach (var room in _rooms)
        {
            if (room.LevelId != levelId) continue;

            bool numberMatch = string.Equals(room.Number, num, StringComparison.OrdinalIgnoreCase);
            if (!numberMatch) continue;

            // Same Number on same Level — check Name
            bool nameMatch = !string.IsNullOrEmpty(nm)
                && string.Equals(room.Name, nm, StringComparison.OrdinalIgnoreCase);

            if (nameMatch)
            {
                // Exact match: Number + Name + Level
                result.HasStrictMatch  = true;
                result.ExistingRoomId  = room.RoomId;
                result.MatchType       = "ExactMatch";
                return result; // no need to search further
            }
            else
            {
                // Number + Level match but Name differs — flag as warning match
                // Continue searching in case an exact match exists later in the list
                if (!result.HasWarningMatch)
                {
                    result.HasWarningMatch = true;
                    result.ExistingRoomId  = room.RoomId;
                    result.MatchType       = "NumberConflict";
                    result.Warning         =
                        $"Room #{num} already exists on level (id {levelId}) with name " +
                        $"'{room.Name}' — incoming name is '{nm}'.";
                }
            }
        }

        return result;
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private static long GetRoomLevelId(Room room)
    {
        try { return room.LevelId.Value; }
        catch { return -1L; }
    }
}
