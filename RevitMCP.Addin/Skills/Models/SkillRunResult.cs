using Newtonsoft.Json;

namespace RevitMCP.Addin.Skills.Models;

public class SkillRunResult
{
    [JsonProperty("runId")]
    public string RunId { get; set; } = string.Empty;

    [JsonProperty("skillId")]
    public string SkillId { get; set; } = string.Empty;

    [JsonProperty("skillName")]
    public string SkillName { get; set; } = string.Empty;

    [JsonProperty("projectId")]
    public string ProjectId { get; set; } = string.Empty;

    [JsonProperty("usedProjectOverride")]
    public bool UsedProjectOverride { get; set; }

    [JsonProperty("startedAt")]
    public DateTime StartedAt { get; set; }

    [JsonProperty("finishedAt")]
    public DateTime FinishedAt { get; set; }

    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonProperty("summary")]
    public SkillRunSummary Summary { get; set; } = new();

    [JsonProperty("taskResults")]
    public List<SkillTaskResult> TaskResults { get; set; } = new();

    [JsonProperty("warnings")]
    public List<string> Warnings { get; set; } = new();

    [JsonProperty("agentSuggestions")]
    public List<AgentSuggestion> AgentSuggestions { get; set; } = new();
}

public class SkillRunSummary
{
    [JsonProperty("totalIssues")]
    public int TotalIssues { get; set; }

    [JsonProperty("criticalIssues")]
    public int CriticalIssues { get; set; }

    [JsonProperty("warnings")]
    public int Warnings { get; set; }

    [JsonProperty("tasksRun")]
    public int TasksRun { get; set; }

    [JsonProperty("tasksFailed")]
    public int TasksFailed { get; set; }

    [JsonProperty("tasksSkipped")]
    public int TasksSkipped { get; set; }
}

public class AgentSuggestion
{
    [JsonProperty("type")]
    public string Type { get; set; } = string.Empty;

    [JsonProperty("description")]
    public string Description { get; set; } = string.Empty;

    [JsonProperty("proposedOverride")]
    public object? ProposedOverride { get; set; }
}
