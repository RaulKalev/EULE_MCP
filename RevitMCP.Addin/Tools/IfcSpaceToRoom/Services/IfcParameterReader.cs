using Autodesk.Revit.DB;
using RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>
/// Reads IFC-related room metadata from Revit element parameters.
/// Uses flexible multi-name lookup because different IFC exporters use different parameter names.
/// Phase 1: read-only, no parameter writes.
/// </summary>
public class IfcParameterReader
{
    // ── Well-known parameter name lists ────────────────────────────────────────

    private static readonly string[] GuidParamNames =
    [
        "IfcGUID", "IFC GUID", "GUID", "GlobalId", "IfcGlobalId"
    ];

    private static readonly string[] NumberParamNames =
    [
        "Number", "Room Number", "IfcName", "Name", "Reference",
        "Pset_SpaceCommon.Reference", "LongName", "IfcLongName"
    ];

    private static readonly string[] NameParamNames =
    [
        "LongName", "IfcLongName", "Room Name", "Name", "IfcName", "ObjectType"
    ];

    private static readonly string[] StoreyParamNames =
    [
        "Building Storey", "IfcBuildingStorey", "Storey", "Level",
        "IfcSpatialStructureElement", "Building Story"
    ];

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads all IFC room metadata from <paramref name="element"/> using flexible parameter lookup.
    /// </summary>
    public IfcSpaceMetadata ReadMetadata(Element element)
    {
        return new IfcSpaceMetadata
        {
            IfcGuid    = GetFirstString(element, GuidParamNames),
            Number     = GetFirstString(element, NumberParamNames),
            Name       = GetFirstString(element, NameParamNames),
            StoreyName = GetFirstString(element, StoreyParamNames)
        };
    }

    /// <summary>
    /// Returns the first non-empty string value found for any of the given parameter names,
    /// or null if none matched.
    /// </summary>
    public string? GetFirstString(Element element, params string[] parameterNames)
    {
        foreach (var pName in parameterNames)
        {
            var value = TryGetString(element, pName);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private static string? TryGetString(Element element, string parameterName)
    {
        try
        {
            var param = element.LookupParameter(parameterName);
            if (param == null || param.StorageType == StorageType.None) return null;

            return param.StorageType switch
            {
                StorageType.String  => param.AsString(),
                StorageType.Integer => param.AsInteger().ToString(),
                StorageType.Double  => param.AsDouble().ToString("G"),
                _                   => null
            };
        }
        catch
        {
            return null;
        }
    }
}
