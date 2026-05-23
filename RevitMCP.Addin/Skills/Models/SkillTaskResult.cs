using Newtonsoft.Json;

namespace RevitMCP.Addin.Skills.Models;

public class SkillTaskResult
{
    [JsonProperty("taskId")]
    public string TaskId { get; set; } = string.Empty;

    [JsonProperty("taskName")]
    public string TaskName { get; set; } = string.Empty;

    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("criticalFailure")]
    public bool CriticalFailure { get; set; }

    [JsonProperty("wasSkipped")]
    public bool WasSkipped { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("issueCount")]
    public int IssueCount { get; set; }

    [JsonProperty("issues")]
    public List<SkillIssue> Issues { get; set; } = new();

    [JsonProperty("durationMs")]
    public long DurationMs { get; set; }

    [JsonProperty("warnings")]
    public List<string> Warnings { get; set; } = new();
}
