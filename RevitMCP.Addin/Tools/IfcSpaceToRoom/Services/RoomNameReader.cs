using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>Reads a room's name as the Name parameter holds it, not Revit's "Name Number" display name.</summary>
internal static class RoomNameReader
{
    public static string GetName(Room room)
    {
        try
        {
            var value = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
            if (value != null) return value.Trim();
        }
        catch { /* fall back to the display name below */ }

        string? display;
        string? number;
        try { display = room.Name; } catch { display = null; }
        try { number = room.Number; } catch { number = null; }
        return RoomNames.FromDisplayName(display, number);
    }
}
