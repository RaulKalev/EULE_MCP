using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>
/// Compares IFC Space candidates against existing Rooms in the host document
/// using built-in Room data only (Level, Number, Name).
/// Phase 1: preview-only, no document modifications, no shared parameter reads/writes.
/// </summary>
public class ExistingRoomPreviewMatcher
{
    // ── Room match status constants (Phase 1 subset) ──────────────────────────

    public const string NoMatch                        = "NoMatch";
    public const string ExactNumberNameLevelMatch      = "ExactNumberNameLevelMatch";
    public const string NumberLevelMatchNameDifferent  = "NumberLevelMatchNameDifferent";
    public const string Skipped                        = "Skipped";

    // ── Cached room snapshot ──────────────────────────────────────────────────

    private record RoomSnapshot(long LevelId, string Number, string Name);

    private List<RoomSnapshot>? _rooms;

    /// <summary>
    /// Pre-loads all placed Rooms from <paramref name="hostDoc"/> for efficient repeated matching.
    /// Call once before calling <see cref="Check"/> for each candidate.
    /// </summary>
    public void LoadRooms(Document hostDoc)
    {
        _rooms = new FilteredElementCollector(hostDoc)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .Cast<Room>()
            .Where(r => r.Area > 0) // placed rooms only (Area == 0 means unplaced)
            .Select(r => new RoomSnapshot(
                LevelId: GetRoomLevelId(r),
                Number:  r.Number ?? string.Empty,
                Name:    r.Name   ?? string.Empty
            ))
            .ToList();
    }

    /// <summary>
    /// Checks whether a Room matching the given identity already exists in the host document.
    /// </summary>
    /// <param name="targetLevelId">Matched host Level element id (from LevelMatcher).</param>
    /// <param name="number">IFC Space room number.</param>
    /// <param name="name">IFC Space room name.</param>
    /// <returns>One of the match status constants defined on this class.</returns>
    public string Check(long? targetLevelId, string? number, string? name)
    {
        if (_rooms == null)
            throw new InvalidOperationException("Call LoadRooms() before Check().");

        // Cannot compare without a resolved level
        if (targetLevelId == null) return Skipped;

        // Both number and name must be non-empty for an identity match
        var num  = (number ?? string.Empty).Trim();
        var nm   = (name   ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(num) && string.IsNullOrEmpty(nm)) return Skipped;

        var levelId = targetLevelId.Value;

        foreach (var room in _rooms)
        {
            if (room.LevelId != levelId) continue;

            bool numberMatch = !string.IsNullOrEmpty(num)
                && string.Equals(room.Number, num, StringComparison.OrdinalIgnoreCase);

            bool nameMatch = !string.IsNullOrEmpty(nm)
                && string.Equals(room.Name, nm, StringComparison.OrdinalIgnoreCase);

            if (numberMatch && nameMatch)
                return ExactNumberNameLevelMatch;

            if (numberMatch && !nameMatch && !string.IsNullOrEmpty(nm))
                return NumberLevelMatchNameDifferent;
        }

        return NoMatch;
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private static long GetRoomLevelId(Room room)
    {
        try { return room.LevelId.Value; }
        catch { return -1L; }
    }
}
