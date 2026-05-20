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
}
