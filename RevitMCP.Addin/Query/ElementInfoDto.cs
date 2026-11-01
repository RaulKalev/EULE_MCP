using Newtonsoft.Json;
using RevitMCP.Addin.Geometry;
using RevitMCP.Addin.ViewElements;

namespace RevitMCP.Addin.Query;

public class ElementInfoDto
{
    public long ElementId { get; set; }
    public string UniqueId { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Family { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public long? TypeElementId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Level { get; set; } = string.Empty;
    /// <summary>Line style name of a detail line, model line or other curve element; omitted for everything else.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? LineStyle { get; set; }

    // Model state (#109): filled only with includeModelState=true (OwnedBy with includeOwnership=true); null fields are
    // left out, so default payloads stay as they were. Empty string = none (main model, not demolished, not workshared).
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

    public Dictionary<string, ParameterValueDto> Parameters { get; set; } = new();

    // Populated only when the query sets IncludeTags; empty list = queried, no tags attached.
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<TagInfoDto>? Tags { get; set; }

    // View reads (#136): filled only for a call with view; null fields are left out, so other payloads stay as they were.
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
