namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

/// <summary>
/// Caller-supplied specification for one Room to update in the
/// <c>sync_ifc_space_room_data</c> endpoint.
/// The IFC Space's current Number and/or Name will be used as the target value(s).
/// </summary>
public class RoomSyncItemRequest
{
    /// <summary>
    /// Revit element id of the IFC Space in the linked document.
    /// Used to look up the current IFC metadata (Number, Name) as the target values.
    /// </summary>
    public long LinkedElementId { get; set; }

    /// <summary>Revit element id of the native Room to update in the host document.</summary>
    public long RoomId { get; set; }

    /// <summary>
    /// When true, update <c>Room.Name</c> to match the IFC Space's current Name.
    /// Default false.
    /// </summary>
    public bool UpdateName { get; set; }

    /// <summary>
    /// When true, update <c>Room.Number</c> to match the IFC Space's current Number.
    /// Default false.
    /// </summary>
    public bool UpdateNumber { get; set; }
}
