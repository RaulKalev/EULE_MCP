using Newtonsoft.Json;

namespace RevitMCP.Core.Logging;

public class LogEntry
{
    [JsonProperty("timestamp")]
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;

    [JsonProperty("user")]
    public string User { get; set; } = string.Empty;

    [JsonProperty("machine")]
    public string Machine { get; set; } = Environment.MachineName;

    [JsonProperty("client")]
    public string Client { get; set; } = string.Empty;

    [JsonProperty("revitVersion")]
    public string RevitVersion { get; set; } = string.Empty;

    [JsonProperty("model")]
    public string Model { get; set; } = string.Empty;

    [JsonProperty("centralPath")]
    public string CentralPath { get; set; } = string.Empty;

    [JsonProperty("activeView")]
    public string ActiveView { get; set; } = string.Empty;

    [JsonProperty("isWorkshared")]
    public bool IsWorkshared { get; set; }

    [JsonProperty("revitUsername")]
    public string RevitUsername { get; set; } = string.Empty;

    [JsonProperty("tool")]
    public string Tool { get; set; } = string.Empty;

    [JsonProperty("permission")]
    public string Permission { get; set; } = string.Empty;

    [JsonProperty("approvalStatus")]
    public string ApprovalStatus { get; set; } = "NotRequired";

    [JsonProperty("status")]
    public string Status { get; set; } = string.Empty;

    [JsonProperty("durationMs")]
    public long DurationMs { get; set; }

    [JsonProperty("modifiedElementIds")]
    public List<long> ModifiedElementIds { get; set; } = new();

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("warnings")]
    public List<string> Warnings { get; set; } = new();

    [JsonProperty("errors")]
    public List<string> Errors { get; set; } = new();

    [JsonProperty("querySafety", NullValueHandling = NullValueHandling.Ignore)]
    public QuerySafetyLogEntry? QuerySafety { get; set; }
}

/// <summary>
/// Structured query-safety diagnostics appended to log entries when element query tools run.
/// Does NOT appear in the MCP pipe response — logged to JSONL only.
/// </summary>
public sealed class QuerySafetyLogEntry
{
    [JsonProperty("candidateCount", NullValueHandling = NullValueHandling.Ignore)]
    public int? CandidateCount { get; set; }

    [JsonProperty("returnedCount", NullValueHandling = NullValueHandling.Ignore)]
    public int? ReturnedCount { get; set; }

    [JsonProperty("hasMore")]
    public bool HasMore { get; set; }

    [JsonProperty("summaryMode")]
    public bool SummaryMode { get; set; }

    [JsonProperty("truncated")]
    public bool Truncated { get; set; }

    [JsonProperty("responseSizeBytes", NullValueHandling = NullValueHandling.Ignore)]
    public long? ResponseSizeBytes { get; set; }
}
