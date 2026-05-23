using Newtonsoft.Json;

namespace RevitMCP.Addin.Skills.Models;

public class SkillIssue
{
    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    [JsonProperty("severity")]
    public string Severity { get; set; } = "Medium";

    [JsonProperty("category")]
    public string Category { get; set; } = string.Empty;

    [JsonProperty("description")]
    public string Description { get; set; } = string.Empty;

    [JsonProperty("hostElementId")]
    public long? HostElementId { get; set; }

    [JsonProperty("linkedElementId")]
    public long? LinkedElementId { get; set; }

    [JsonProperty("linkedModelName")]
    public string? LinkedModelName { get; set; }

    [JsonProperty("distanceMm")]
    public double? DistanceMm { get; set; }

    [JsonProperty("suggestedAction")]
    public string? SuggestedAction { get; set; }
}
