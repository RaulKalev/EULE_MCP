using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>
/// Places a native Revit Room element using <c>doc.Create.NewRoom(Level, UV)</c>.
/// Caller must supply an open Transaction; this class does not open or close transactions.
/// Phase 3.
/// </summary>
public class NativeRoomCreator
{
    /// <summary>
    /// Places a Room at <paramref name="placementPoint"/> on <paramref name="level"/>.
    /// </summary>
    /// <param name="doc">Host document (transaction must be open).</param>
    /// <param name="level">The Level the Room should be associated with.</param>
    /// <param name="placementPoint">
    /// A point known to lie inside the Room boundary (XYZ in host coordinates, feet).
    /// Only X and Y are used as the UV placement; Z is ignored.
    /// </param>
    /// <returns>A <see cref="NativeRoomCreationResult"/> with the new Room's element id.</returns>
    public NativeRoomCreationResult Create(Document doc, Level level, XYZ placementPoint)
    {
        var result = new NativeRoomCreationResult();

        try
        {
            var uv   = new UV(placementPoint.X, placementPoint.Y);
            var room = doc.Create.NewRoom(level, uv);

            if (room == null)
            {
                result.Error = "doc.Create.NewRoom returned null.";
                return result;
            }

            result.RoomId  = room.Id.Value;
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Error = $"Exception placing Room: {ex.Message}";
        }

        return result;
    }
}
