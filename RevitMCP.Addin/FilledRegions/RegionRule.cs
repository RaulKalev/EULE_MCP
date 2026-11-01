using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Query;

namespace RevitMCP.Addin.FilledRegions;

public enum RuleSourceKind
{
    Rooms,
    Elements
}

/// <summary>From a parameter value of the source to a filled region type. A value not in the map has no type: nothing is guessed.</summary>
public sealed class TypeMap
{
    private readonly Dictionary<string, string> _entries = new(StringComparer.OrdinalIgnoreCase);

    public string Parameter { get; private set; } = string.Empty;
    public IEnumerable<string> Types => _entries.Values.Distinct(StringComparer.OrdinalIgnoreCase);
    public int Count => _entries.Count;

    public static TypeMap Parse(JObject token, List<string> errors)
    {
        var map = new TypeMap { Parameter = FilledRegionSpecParser.Text(token["parameter"])?.Trim() ?? string.Empty };
        if (map.Parameter.Length == 0) errors.Add("typeMap.parameter is required: the source parameter whose value picks the type.");

        if (token["map"] is not JObject entries || !entries.Properties().Any())
        {
            errors.Add("typeMap.map must be a non-empty object {parameter value: type name or id}.");
            return map;
        }
        foreach (var entry in entries.Properties())
        {
            var key = entry.Name.Trim();
            var type = FilledRegionSpecParser.Text(entry.Value)?.Trim();
            if (string.IsNullOrEmpty(type))
            {
                errors.Add($"typeMap.map['{entry.Name}'] has no type.");
                continue;
            }
            if (map._entries.ContainsKey(key))
            {
                errors.Add($"typeMap.map lists '{key}' twice (values are compared ignoring case and outer spaces).");
                continue;
            }
            map._entries[key] = type!;
        }
        return map;
    }

    /// <summary>The type for a value: exact after trimming, case-insensitive. Null when the value is not in the map.</summary>
    public string? TypeFor(string? value) =>
        value != null && _entries.TryGetValue(value.Trim(), out var type) ? type : null;
}

/// <summary>A rule: sources, then per source a shape and a type.</summary>
public sealed class RegionRule
{
    public int Index { get; set; }
    public string? View { get; set; }
    public RuleSourceKind SourceKind { get; set; }
    public List<string> Categories { get; } = new();
    public List<ParameterFilterDto> Filters { get; } = new();
    public string? InView { get; set; }
    public string? Level { get; set; }
    public long LinkInstanceId { get; set; }
    public List<long> ElementIds { get; } = new();
    public int Limit { get; set; } = FilledRegionSpecParser.MaxRegions;

    public RegionShapeKind ShapeKind { get; set; } = RegionShapeKind.Room;
    public double PaddingMm { get; set; }
    public string BoundaryLocation { get; set; } = "finish";

    public string? Type { get; set; }
    public TypeMap? TypeMap { get; set; }

    public string? LineStyle { get; set; }
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Errors { get; } = new();
}

/// <summary>One source the rule's selection found, with the value of the map parameter.</summary>
public sealed class RuleSourceItem
{
    public long Id { get; init; }
    public string Label { get; init; } = string.Empty;

    /// <summary>False when the source has no parameter of the map's name.</summary>
    public bool ParameterFound { get; init; } = true;
    public string? ParameterValue { get; init; }
}

/// <summary>A source a rule did not draw, and why.</summary>
public sealed class SkippedSource
{
    public int RuleIndex { get; init; }
    public long Id { get; init; }
    public string Label { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string? Value { get; init; }
}

/// <summary>What a rule expands to.</summary>
public sealed class RuleExpansion
{
    public List<RegionSpec> Regions { get; } = new();
    public List<SkippedSource> Skipped { get; } = new();

    /// <summary>Each value without a mapped type and how many sources have it, so a map can be completed in one go.</summary>
    public Dictionary<string, int> UnmappedValues { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Turns a rule's sources into ordinary region specs, one per source, so a rule goes through exactly the validation and
/// creation code of a direct spec. A source without a mapped type is listed and skipped, never given a guessed type. Pure.
/// </summary>
public static class RuleExpander
{
    public static RuleExpansion Expand(RegionRule rule, IEnumerable<RuleSourceItem> sources, int firstIndex = 0)
    {
        var expansion = new RuleExpansion();
        var index = firstIndex;
        foreach (var source in sources)
        {
            string? type;
            if (rule.TypeMap == null)
            {
                type = rule.Type;
            }
            else if (!source.ParameterFound)
            {
                expansion.Skipped.Add(new SkippedSource
                {
                    RuleIndex = rule.Index, Id = source.Id, Label = source.Label, Code = RegionCodes.ParameterNotFound,
                    Reason = $"The source has no parameter '{rule.TypeMap.Parameter}'."
                });
                continue;
            }
            else if (string.IsNullOrWhiteSpace(source.ParameterValue))
            {
                expansion.Skipped.Add(new SkippedSource
                {
                    RuleIndex = rule.Index, Id = source.Id, Label = source.Label, Code = RegionCodes.ValueEmpty,
                    Reason = $"Parameter '{rule.TypeMap.Parameter}' is empty."
                });
                continue;
            }
            else
            {
                type = rule.TypeMap.TypeFor(source.ParameterValue);
                if (type == null)
                {
                    var value = source.ParameterValue!.Trim();
                    expansion.UnmappedValues[value] = expansion.UnmappedValues.TryGetValue(value, out var count) ? count + 1 : 1;
                    expansion.Skipped.Add(new SkippedSource
                    {
                        RuleIndex = rule.Index, Id = source.Id, Label = source.Label, Code = RegionCodes.ValueNotMapped, Value = value,
                        Reason = $"'{value}' of parameter '{rule.TypeMap.Parameter}' has no type in the map."
                    });
                    continue;
                }
            }

            expansion.Regions.Add(new RegionSpec
            {
                Index = index++,
                View = rule.View,
                Type = type,
                LineStyle = rule.LineStyle,
                Parameters = rule.Parameters,
                RuleIndex = rule.Index,
                SourceId = source.Id,
                SourceLabel = source.Label,
                Shape = new RegionShapeSpec
                {
                    Kind = rule.ShapeKind,
                    RoomId = rule.ShapeKind == RegionShapeKind.Room ? source.Id : 0,
                    ElementId = rule.ShapeKind == RegionShapeKind.ElementBox ? source.Id : 0,
                    LinkInstanceId = rule.LinkInstanceId,
                    BoundaryLocation = rule.BoundaryLocation,
                    PaddingMm = rule.PaddingMm
                }
            });
        }
        return expansion;
    }
}
