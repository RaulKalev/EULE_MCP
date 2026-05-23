using Newtonsoft.Json;

namespace RevitMCP.Addin.Skills.Models;

public class SkillDefinition
{
    [JsonProperty("schemaVersion")]
    public string SchemaVersion { get; set; } = "1.0";

    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("description")]
    public string Description { get; set; } = string.Empty;

    [JsonProperty("version")]
    public string Version { get; set; } = "1.0.0";

    [JsonProperty("author")]
    public string Author { get; set; } = string.Empty;

    [JsonProperty("isCompanyMaster")]
    public bool IsCompanyMaster { get; set; }

    [JsonProperty("tasks")]
    public List<SkillTaskDefinition> Tasks { get; set; } = new();

    [JsonProperty("defaultSettings")]
    public SkillDefaultSettings DefaultSettings { get; set; } = new();

    /// <summary>File path this skill was loaded from. Not serialized.</summary>
    [JsonIgnore]
    public string? SourcePath { get; set; }
}

public class SkillDefaultSettings
{
    [JsonProperty("stopOnCriticalFailure")]
    public bool StopOnCriticalFailure { get; set; } = false;

    [JsonProperty("allowProjectOverride")]
    public bool AllowProjectOverride { get; set; } = true;

    [JsonProperty("requiresUserConfirmationBeforeModelChanges")]
    public bool RequiresUserConfirmationBeforeModelChanges { get; set; } = true;
}
