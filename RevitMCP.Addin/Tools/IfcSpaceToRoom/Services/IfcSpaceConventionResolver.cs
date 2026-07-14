using System.Globalization;
using System.Text.RegularExpressions;
using RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>Pure parameter convention logic, separated from the Revit API for unit testing.</summary>
public static class IfcSpaceConventionResolver
{
    private static readonly string[] ExplicitTypeParameters =
        ["IfcExportAs", "IfcEntity", "IFC Entity", "IfcType", "ObjectType"];
    private static readonly string[] GuidParameters =
        ["IfcGUID", "IFC GUID", "GUID", "GlobalId", "IfcGlobalId"];
    private static readonly string[] IfcPresenceParameters =
        ["IfcGUID", "IFC GUID", "GlobalId", "IfcName"];
    private static readonly string[] ArRuumProperties =
        ["AR_Ruum.100_Nimi", "AR_Ruum.105_Number", "AR_Ruum.120_Pindala"];

    public static IfcConventionDetection Detect(
        IReadOnlyList<KeyValuePair<string, string?>> parameters,
        string? elementName = null)
    {
        foreach (var name in ExplicitTypeParameters)
        {
            var value = Resolve(parameters, [name]).Value;
            if (Contains(value, "IfcSpace"))
                return new("Confirmed", "ExplicitIfcSpace");
        }

        if (Contains(elementName, "IfcSpace"))
            return new("Confirmed", "ExplicitIfcSpace");

        var classification = Resolve(parameters, ["ClassificationCode"]).Value;
        if (Contains(classification, "IfcSpaceType"))
            return new("Confirmed", "ClassificationCodeIfcSpaceType");
        if (Contains(classification, "IfcSpace") && Contains(classification, "ruum"))
            return new("Confirmed", "ClassificationCodeIfcSpaceRuum");

        var propertySetList = Resolve(parameters, ["IfcPropertySetList"]).Value;
        if (Contains(propertySetList, "AR_Ruum") &&
            ArRuumProperties.Any(p => HasParameter(parameters, p)))
            return new("Confirmed", "AR_RuumPropertySet");

        foreach (var name in IfcPresenceParameters)
        {
            if (!string.IsNullOrWhiteSpace(Resolve(parameters, [name]).Value))
                return new("Probable", "ProbableIfcOrigin");
        }

        return new("Rejected", "NotIfcSpace");
    }

    public static IfcResolvedConventionMetadata ResolveMetadata(
        IReadOnlyList<KeyValuePair<string, string?>> parameters,
        IfcMetadataMappingOptions? options = null)
    {
        options ??= new IfcMetadataMappingOptions();
        var guid = Resolve(parameters, GuidParameters);
        var number = Resolve(parameters, options.RoomNumberPrecedence);
        var name = Resolve(parameters, options.RoomNamePrecedence);
        var storey = Resolve(parameters, options.StoreyPrecedence);
        var area = Resolve(parameters, options.AreaPrecedence);

        return new IfcResolvedConventionMetadata
        {
            IfcGuid = guid.Value,
            Number = number.Value,
            NumberSource = number.Source,
            Name = name.Value,
            NameSource = name.Source,
            StoreyName = storey.Value,
            StoreySource = storey.Source,
            AreaM2 = ParseAreaM2(area.Value),
            AreaSource = string.IsNullOrWhiteSpace(area.Value) ? null : area.Source
        };
    }

    public static (string? Value, string? Source) Resolve(
        IReadOnlyList<KeyValuePair<string, string?>> parameters,
        IEnumerable<string> precedence)
    {
        foreach (var requestedName in precedence)
        {
            var exact = parameters.FirstOrDefault(p =>
                string.Equals(p.Key, requestedName, StringComparison.Ordinal));
            if (exact.Key != null && !string.IsNullOrWhiteSpace(exact.Value))
                return (exact.Value!.Trim(), exact.Key);

            var insensitive = parameters.FirstOrDefault(p =>
                string.Equals(p.Key, requestedName, StringComparison.OrdinalIgnoreCase));
            if (insensitive.Key != null && !string.IsNullOrWhiteSpace(insensitive.Value))
                return (insensitive.Value!.Trim(), insensitive.Key);
        }
        return (null, null);
    }

    public static double? ParseAreaM2(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = Regex.Match(value, @"[-+]?\d+(?:[.,]\d+)?");
        if (!match.Success) return null;
        var normalized = match.Value.Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var area)
            ? area
            : null;
    }

    private static bool HasParameter(
        IReadOnlyList<KeyValuePair<string, string?>> parameters, string name) =>
        parameters.Any(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase));

    private static bool Contains(string? value, string fragment) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
}

public sealed record IfcConventionDetection(string Confidence, string Reason);

public sealed class IfcResolvedConventionMetadata
{
    public string? IfcGuid { get; init; }
    public string? Number { get; init; }
    public string? NumberSource { get; init; }
    public string? Name { get; init; }
    public string? NameSource { get; init; }
    public string? StoreyName { get; init; }
    public string? StoreySource { get; init; }
    public double? AreaM2 { get; init; }
    public string? AreaSource { get; init; }
}
