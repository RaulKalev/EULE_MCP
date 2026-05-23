using Newtonsoft.Json;

namespace RevitMCP.Addin.Skills.Models;

public class SkillTaskDefinition
{
    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    [JsonProperty("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonProperty("settings")]
    public Dictionary<string, object?> Settings { get; set; } = new();
}
