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
    public IfcSpaceMetadata ReadMetadata(
        Element element,
        IfcMetadataMappingOptions? options = null)
    {
        var resolved = IfcSpaceConventionResolver.ResolveMetadata(ReadParameters(element), options);

        return new IfcSpaceMetadata
        {
            IfcGuid      = resolved.IfcGuid,
            Number       = resolved.Number,
            NumberSource = resolved.NumberSource,
            Name         = resolved.Name,
            NameSource   = resolved.NameSource,
            StoreyName   = resolved.StoreyName,
            StoreySource = resolved.StoreySource,
            AreaM2       = resolved.AreaM2,
            AreaSource   = resolved.AreaSource
        };
    }

    /// <summary>Snapshots all named element parameters without changing their text or Unicode data.</summary>
    public static IReadOnlyList<KeyValuePair<string, string?>> ReadParameters(Element element)
    {
        var result = new List<KeyValuePair<string, string?>>();
        try
        {
            foreach (Parameter parameter in element.Parameters)
            {
                var name = parameter.Definition?.Name;
                if (string.IsNullOrEmpty(name)) continue;
                result.Add(new KeyValuePair<string, string?>(name, ReadValue(parameter)));
            }
        }
        catch { /* malformed imported parameters are ignored */ }
        return result;
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

    private static string? ReadValue(Parameter parameter)
    {
        try
        {
            if (parameter.StorageType == StorageType.None) return null;
            return parameter.StorageType switch
            {
                StorageType.String => parameter.AsString(),
                StorageType.Integer => parameter.AsValueString() ?? parameter.AsInteger().ToString(),
                StorageType.Double => parameter.AsValueString() ?? parameter.AsDouble().ToString("G", System.Globalization.CultureInfo.InvariantCulture),
                _ => parameter.AsValueString()
            };
        }
        catch { return null; }
    }
}
