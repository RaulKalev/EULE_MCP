namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

/// <summary>Configurable IFC parameter precedence shared by every Space-to-Room endpoint.</summary>
public class IfcMetadataMappingOptions
{
    public string? RoomNameParameter { get; set; }
    public string? RoomNumberParameter { get; set; }
    public string? StoreyParameter { get; set; }
    public string? AreaParameter { get; set; }
    public bool EnableArRuumDefaults { get; set; } = true;

    public IReadOnlyList<string> RoomNamePrecedence => Build(
        RoomNameParameter,
        EnableArRuumDefaults ? "AR_Ruum.100_Nimi" : null,
        "IfcLongName", "LongName", "Room Name", "Name", "IfcName", "ObjectType");

    public IReadOnlyList<string> RoomNumberPrecedence => Build(
        RoomNumberParameter,
        EnableArRuumDefaults ? "AR_Ruum.105_Number" : null,
        "IfcName", "Number", "Room Number", "Reference", "Pset_SpaceCommon.Reference", "Name");

    public IReadOnlyList<string> StoreyPrecedence => Build(
        StoreyParameter,
        EnableArRuumDefaults ? "IfcDecomposes" : null,
        "Building Storey", "IfcBuildingStorey", "Storey", "Level",
        "IfcSpatialStructureElement", "Building Story");

    public IReadOnlyList<string> AreaPrecedence => Build(
        AreaParameter,
        EnableArRuumDefaults ? "AR_Ruum.120_Pindala" : null);

    private static IReadOnlyList<string> Build(params string?[] names)
    {
        var result = new List<string>();
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var trimmed = name.Trim();
            if (!result.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                result.Add(trimmed);
        }
        return result;
    }
}
