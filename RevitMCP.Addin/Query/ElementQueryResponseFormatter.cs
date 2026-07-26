using Newtonsoft.Json;

namespace RevitMCP.Addin.Query;

/// <summary>
/// Shapes element-query responses without changing how the Revit model is queried.
/// The full DTO remains the default contract; compact mode is an opt-in projection
/// intended for agents that only need identity fields and parameter values.
/// </summary>
public static class ElementQueryResponseFormatter
{
    public static object FormatElements(IReadOnlyList<ElementInfoDto> elements, bool compact)
    {
        if (!compact)
            return elements;

        return elements.Select(element => new CompactElementInfoDto
        {
            ElementId = element.ElementId,
            Category = NullIfEmpty(element.Category),
            Family = NullIfEmpty(element.Family),
            Type = NullIfEmpty(element.Type),
            Name = NullIfEmpty(element.Name),
            Level = NullIfEmpty(element.Level),
            Parameters = element.Parameters.Count == 0
                ? null
                : element.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value.Value),
            Tags = element.Tags
        }).ToList();
    }

    private static string? NullIfEmpty(string value)
    {
        return string.IsNullOrEmpty(value) ? null : value;
    }
}

public sealed class CompactElementInfoDto
{
    public long ElementId { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Category { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Family { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Type { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Name { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Level { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<string, string>? Parameters { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<TagInfoDto>? Tags { get; set; }
}
