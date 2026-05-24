namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

/// <summary>
/// Raw IFC space metadata read from parameters on a linked Generic Model / DirectShape.
/// All fields are nullable — not every IFC exporter writes every field.
/// Phase 1: read-only, no parameter writes.
/// </summary>
public class IfcSpaceMetadata
{
    /// <summary>IFC GlobalId / GUID of the original IfcSpace entity.</summary>
    public string? IfcGuid { get; set; }

    /// <summary>Room number / reference (from Number, IfcName, Reference, etc.).</summary>
    public string? Number { get; set; }

    /// <summary>Room long name (from LongName, IfcLongName, Name, etc.).</summary>
    public string? Name { get; set; }

    /// <summary>Name of the building storey the space belongs to (from IfcBuildingStorey, Storey, Level, etc.).</summary>
    public string? StoreyName { get; set; }
}
