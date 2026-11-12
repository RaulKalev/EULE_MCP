using Newtonsoft.Json;
using RevitMCP.Addin.Geometry;
using RevitMCP.Addin.ViewElements;

namespace RevitMCP.Addin.Query;

/// <summary>
/// Shapes element-query responses without changing how the Revit model is queried.
/// Compact mode (the default of revit_get_elements_info and revit_find_elements_by_parameter) keeps identity
/// fields and parameter values; the full DTO adds per-parameter metadata and is asked for with compact=false.
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
            // #109: kept as given (empty = none) so a requested model state is visible in compact mode too.
            PhaseCreated = element.PhaseCreated,
            PhaseDemolished = element.PhaseDemolished,
            DesignOption = element.DesignOption,
            Workset = element.Workset,
            OwnedBy = element.OwnedBy,
            Parameters = element.Parameters.Count == 0
                ? null
                // X3: a measured value shows its unit here ("3200 mm"); the full DTO keeps Value and Unit apart.
                : CompactParameters(element),
            Tags = element.Tags,
            // #136: all null unless the call had view, and then left out. A view read keeps a line's style in compact mode.
            LineStyle = element.Location != null ? element.LineStyle : null,
            Location = element.Location,
            BoundingBox = element.BoundingBox,
            Host = element.Host,
            TaggedElementIds = element.TaggedElementIds,
            OwnerView = element.OwnerView,
            PartOfGroup = element.PartOfGroup,
            GroupId = element.GroupId,
            Room = element.Room
        }).ToList();
    }

    /// <summary>Parameter name → value as compact mode shows it: a measured value carries its unit ("3200 mm").</summary>
    public static Dictionary<string, string> CompactParameters(ElementInfoDto element) =>
        element.Parameters.ToDictionary(pair => pair.Key, pair => string.IsNullOrEmpty(pair.Value.Unit) || pair.Value.Value.Length == 0
            ? pair.Value.Value
            : pair.Value.Value + " " + pair.Value.Unit);

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
    public string? PhaseCreated { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? PhaseDemolished { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? DesignOption { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Workset { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? OwnedBy { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<string, string>? Parameters { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<TagInfoDto>? Tags { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? LineStyle { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ViewElementLocationDto? Location { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public BoxMmDto? BoundingBox { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ViewElementRefDto? Host { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<long>? TaggedElementIds { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ViewElementRefDto? OwnerView { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public bool? PartOfGroup { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public long? GroupId { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ViewElementRoomDto? Room { get; set; }
}
