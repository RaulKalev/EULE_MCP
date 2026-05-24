namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

/// <summary>
/// Internal result from <c>NativeRoomCreator</c> for one Room placement attempt.
/// Not serialized directly — used within the conversion transaction.
/// </summary>
public class NativeRoomCreationResult
{
    /// <summary>True when the Room element was placed successfully.</summary>
    public bool Success { get; set; }

    /// <summary>Revit element id of the newly created Room. Null when Success is false.</summary>
    public long? RoomId { get; set; }

    /// <summary>Error message when <see cref="Success"/> is false.</summary>
    public string? Error { get; set; }
}
