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

    /// <summary>Room number / reference (e.g. "101"). Read from Reference, Number, etc.</summary>
    public string? Number { get; set; }

    /// <summary>
    /// Name of the parameter that produced <see cref="Number"/>.
    /// Null when Number is null. Helps callers understand where the value came from.
    /// </summary>
    public string? NumberSource { get; set; }

    /// <summary>Room long name (e.g. "Office"). Read from LongName, Room Name, etc.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// Name of the parameter that produced <see cref="Name"/>.
    /// Null when Name is null. Helps callers understand where the value came from.
    /// </summary>
    public string? NameSource { get; set; }

    /// <summary>Name of the building storey the space belongs to.</summary>
    public string? StoreyName { get; set; }
}
