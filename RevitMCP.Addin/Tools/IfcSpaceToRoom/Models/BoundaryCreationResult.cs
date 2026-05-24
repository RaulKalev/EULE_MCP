namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

/// <summary>
/// Internal result from <c>RoomBoundaryCreator</c> for one IFC Space footprint.
/// Contains Revit element ids of the newly created Room Separation Lines.
/// Not serialized directly — used within the conversion transaction.
/// </summary>
public class BoundaryCreationResult
{
    /// <summary>True when at least one boundary line was created successfully.</summary>
    public bool Success { get; set; }

    /// <summary>Number of Room Separation Line segments created.</summary>
    public int CreatedLineCount { get; set; }

    /// <summary>Revit element ids of all created boundary line elements.</summary>
    public List<long> CreatedLineIds { get; set; } = new();

    /// <summary>Error message when <see cref="Success"/> is false.</summary>
    public string? Error { get; set; }
}
