using Newtonsoft.Json;

namespace RevitMCP.Addin.Skills.Models;

/// <summary>
/// Project-specific override for a company skill.
/// Stored at %AppData%\RKTools\RevitMCP\ProjectSkillOverrides\{projectId}\{skillId}.override.skill.json
/// </summary>
public class SkillOverride
{
    [JsonProperty("schemaVersion")]
    public string SchemaVersion { get; set; } = "1.0";

    [JsonProperty("baseSkillId")]
    public string BaseSkillId { get; set; } = string.Empty;

    [JsonProperty("baseSkillVersion")]
    public string BaseSkillVersion { get; set; } = string.Empty;

    [JsonProperty("projectId")]
    public string ProjectId { get; set; } = string.Empty;

    [JsonProperty("projectName")]
    public string ProjectName { get; set; } = string.Empty;

    [JsonProperty("createdBy")]
    public string CreatedBy { get; set; } = string.Empty;

    [JsonProperty("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonProperty("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    [JsonProperty("overrides")]
    public SkillOverrideData Overrides { get; set; } = new();

    [JsonProperty("notes")]
    public List<string> Notes { get; set; } = new();
}

public class SkillOverrideData
{
    /// <summary>Key = task ID, value = partial task override (enabled flag + settings dict).</summary>
    [JsonProperty("tasks")]
    public Dictionary<string, SkillTaskOverride> Tasks { get; set; } = new();

    [JsonProperty("defaultSettings")]
    public Dictionary<string, object?> DefaultSettings { get; set; } = new();
}

public class SkillTaskOverride
{
    /// <summary>null means "do not override the enabled flag".</summary>
    [JsonProperty("enabled")]
    public bool? Enabled { get; set; }

    [JsonProperty("settings")]
    public Dictionary<string, object?> Settings { get; set; } = new();
}
