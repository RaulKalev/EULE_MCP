using Autodesk.Revit.DB;
using RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>
/// Reads IFC-related room metadata from Revit element parameters.
/// Uses flexible multi-name lookup because different IFC exporters use different parameter names.
/// Phase 1: read-only, no parameter writes.
///
/// Number priority: numeric reference fields first, then IfcName/Name as last resort.
/// Do NOT use LongName / IfcLongName as number sources — those are room descriptions.
///
/// Name priority: LongName first (it is the human-readable room description in IFC),
/// then fallback to Room Name / Name / IfcName / ObjectType.
/// </summary>
public class IfcParameterReader
{
    // ── Well-known parameter name lists ────────────────────────────────────────

    private static readonly string[] GuidParamNames =
    [
        "IfcGUID", "IFC GUID", "GUID", "GlobalId", "IfcGlobalId"
    ];

    /// <summary>
    /// Priority order for room number / reference.
    /// LongName and IfcLongName are intentionally excluded — they hold room descriptions,
    /// not room numbers. Using them as number sources would set e.g. "Office" as the number.
    /// </summary>
    private static readonly string[] NumberParamNames =
    [
        "Number", "Room Number", "Reference", "Pset_SpaceCommon.Reference", "IfcName", "Name"
    ];

    /// <summary>
    /// Priority order for room long name / description.
    /// LongName / IfcLongName intentionally first — they hold the human-readable room description
    /// in IFC models (e.g. "Open Plan Office").
    /// </summary>
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
    /// The returned metadata includes source-tracking so callers can explain where each value came from.
    /// </summary>
    public IfcSpaceMetadata ReadMetadata(Element element)
    {
        var (number, numberSource) = GetFirstStringWithSource(element, NumberParamNames);
        var (name,   nameSource)   = GetFirstStringWithSource(element, NameParamNames);

        return new IfcSpaceMetadata
        {
            IfcGuid      = GetFirstString(element, GuidParamNames),
            Number       = number,
            NumberSource = numberSource,
            Name         = name,
            NameSource   = nameSource,
            StoreyName   = GetFirstString(element, StoreyParamNames)
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

    /// <summary>
    /// Returns the first non-empty string value and the parameter name that produced it,
    /// or (null, null) if nothing matched.
    /// </summary>
    private static (string? Value, string? Source) GetFirstStringWithSource(
        Element element, string[] parameterNames)
    {
        foreach (var pName in parameterNames)
        {
            var value = TryGetString(element, pName);
            if (!string.IsNullOrWhiteSpace(value))
                return (value, pName);
        }
        return (null, null);
    }

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
