using System.Globalization;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Plans;

namespace RevitMCP.Addin.Jobs;

[JsonConverter(typeof(StringEnumConverter))]
public enum JobState
{
    Proposed,
    Running,
    WaitingApproval,
    Paused,
    Suspended,
    Completed,
    Failed,
    Cancelled
}

[JsonConverter(typeof(StringEnumConverter))]
public enum JobStepState
{
    Pending,
    Running,
    WaitingApproval,
    Succeeded,
    Failed,
    Skipped,
    Cancelled
}

/// <summary>One tool call of a job (#100). Arguments may hold "{{var}}" placeholders bound by earlier steps.</summary>
public sealed class JobStep
{
    [JsonProperty("index")] public int Index { get; set; }
    [JsonProperty("tool")] public string Tool { get; set; } = string.Empty;
    [JsonProperty("arguments")] public JObject Arguments { get; set; } = new();

    /// <summary>Variable → JSON path into this step's result, e.g. "ids": "$.data.items[*].id".</summary>
    [JsonProperty("bind", NullValueHandling = NullValueHandling.Ignore)] public Dictionary<string, string>? Bind { get; set; }
    [JsonProperty("title", NullValueHandling = NullValueHandling.Ignore)] public string? Title { get; set; }

    /// <summary>A failure of this step does not stop the job.</summary>
    [JsonProperty("continueOnError")] public bool ContinueOnError { get; set; }

    /// <summary>A refusal (validation_failed) skips the step instead of failing it.</summary>
    [JsonProperty("optional")] public bool Optional { get; set; }

    /// <summary>The tool is read-only: no warning check after it.</summary>
    [JsonProperty("readOnly")] public bool IsReadOnly { get; set; }

    [JsonProperty("state")] public JobStepState State { get; set; } = JobStepState.Pending;
    [JsonProperty("status", NullValueHandling = NullValueHandling.Ignore)] public string? Status { get; set; }
    [JsonProperty("message", NullValueHandling = NullValueHandling.Ignore)] public string? Message { get; set; }
    [JsonProperty("durationMs")] public long DurationMs { get; set; }
    [JsonProperty("attempts")] public int Attempts { get; set; }
    [JsonProperty("requestId", NullValueHandling = NullValueHandling.Ignore)] public string? RequestId { get; set; }
    [JsonProperty("startedAt", NullValueHandling = NullValueHandling.Ignore)] public DateTimeOffset? StartedAt { get; set; }
    [JsonProperty("finishedAt", NullValueHandling = NullValueHandling.Ignore)] public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>The step's result (success, status, message, data, warnings), trimmed for the journal.</summary>
    [JsonProperty("result", NullValueHandling = NullValueHandling.Ignore)] public JToken? Result { get; set; }

    /// <summary>The step failed and the job went on (continueOnError or stopOnFailure=false).</summary>
    [JsonProperty("tolerated")] public bool Tolerated { get; set; }

    [JsonIgnore] public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Tool : Title!;
    [JsonIgnore] public bool IsFinished => State is JobStepState.Succeeded or JobStepState.Skipped || (State == JobStepState.Failed && Tolerated);
}

/// <summary>When a running job stops by itself.</summary>
public sealed class JobStopConditions
{
    [JsonProperty("stopOnFailure")] public bool StopOnFailure { get; set; } = true;

    /// <summary>none | error | any — checked after every write step against the job's checkpoint.</summary>
    [JsonProperty("stopOnNewWarnings")] public string StopOnNewWarnings { get; set; } = "error";

    /// <summary>New warnings tolerated (counted from the last resume) before the job pauses.</summary>
    [JsonProperty("maxNewWarnings")] public int MaxNewWarnings { get; set; }

    [JsonProperty("maxDurationMinutes")] public int MaxDurationMinutes { get; set; } = 120;

    /// <summary>revit_busy retries of one step before the job is suspended.</summary>
    [JsonProperty("maxConsecutiveBusy")] public int MaxConsecutiveBusy { get; set; } = 20;
}

/// <summary>A job: ordered steps run by the connector under one plan, journaled to disk. Pure — no Revit API.</summary>
public sealed class Job
{
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;
    [JsonProperty("title")] public string Title { get; set; } = string.Empty;
    [JsonProperty("client")] public string ClientName { get; set; } = string.Empty;
    [JsonProperty("modelId")] public string ModelId { get; set; } = string.Empty;
    [JsonProperty("document")] public string DocumentTitle { get; set; } = string.Empty;

    /// <summary>In-session document identity; not persisted (resume re-binds by <see cref="ModelId"/>).</summary>
    [JsonIgnore] public object? DocumentToken { get; set; }

    [JsonProperty("planId", NullValueHandling = NullValueHandling.Ignore)] public string? PlanId { get; set; }
    [JsonProperty("checkpointId", NullValueHandling = NullValueHandling.Ignore)] public string? CheckpointId { get; set; }

    /// <summary>The approved envelope, kept so a resume can activate a new plan with it.</summary>
    [JsonProperty("envelope")] public JObject Envelope { get; set; } = new();
    [JsonProperty("planSteps")] public List<string> PlanSteps { get; set; } = new();
    [JsonProperty("stop")] public JobStopConditions Stop { get; set; } = new();
    [JsonProperty("steps")] public List<JobStep> Steps { get; set; } = new();
    [JsonProperty("variables")] public Dictionary<string, JToken> Variables { get; set; } = new(StringComparer.Ordinal);

    [JsonProperty("state")] public JobState State { get; set; } = JobState.Proposed;
    [JsonProperty("stopReason", NullValueHandling = NullValueHandling.Ignore)] public string? StopReason { get; set; }
    [JsonProperty("createdAt")] public DateTimeOffset CreatedAt { get; set; }
    [JsonProperty("startedAt", NullValueHandling = NullValueHandling.Ignore)] public DateTimeOffset? StartedAt { get; set; }
    [JsonProperty("finishedAt", NullValueHandling = NullValueHandling.Ignore)] public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Time spent running before the current run segment (pauses and suspensions do not count).</summary>
    [JsonProperty("elapsedMs")] public long ElapsedMs { get; set; }

    /// <summary>When the current run segment started; null while the job does not run.</summary>
    [JsonIgnore] public DateTimeOffset? SegmentStartedAt { get; set; }

    /// <summary>New warnings (since the checkpoint) the user accepted by resuming: errors and all.</summary>
    [JsonProperty("acceptedNewErrors")] public int AcceptedNewErrors { get; set; }
    [JsonProperty("acceptedNewWarnings")] public int AcceptedNewWarnings { get; set; }
    [JsonProperty("newWarnings")] public int NewWarnings { get; set; }
    [JsonProperty("newErrors")] public int NewErrors { get; set; }

    [JsonProperty("reportPageId", NullValueHandling = NullValueHandling.Ignore)] public string? ReportPageId { get; set; }
    [JsonProperty("reportUrl", NullValueHandling = NullValueHandling.Ignore)] public string? ReportUrl { get; set; }

    /// <summary>auto (publish when the job stops) | never.</summary>
    [JsonProperty("report")] public string Report { get; set; } = "auto";
    [JsonProperty("openReport")] public bool OpenReport { get; set; } = true;

    /// <summary>Set by cancel/pause requests; honoured after the running step.</summary>
    [JsonProperty("pendingControl", NullValueHandling = NullValueHandling.Ignore)] public string? PendingControl { get; set; }

    [JsonIgnore] public bool IsTerminal => State is JobState.Completed or JobState.Failed or JobState.Cancelled;
    [JsonIgnore] public bool IsResumable => State is JobState.Paused or JobState.Suspended or JobState.Failed;
    [JsonIgnore] public int DoneCount => Steps.Count(s => s.IsFinished);

    /// <summary>The first step that is not succeeded or skipped; null when every step is.</summary>
    [JsonIgnore] public JobStep? NextStep => Steps.FirstOrDefault(s => !s.IsFinished);
}

/// <summary>Job ids: "job-" + 6 hex (also the report's page id). Pure.</summary>
public static class JobIds
{
    public const string Prefix = "job-";

    public static string NewId()
    {
        var bytes = new byte[3];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        return Prefix + string.Concat(bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
    }

    public static bool IsValid(string? id)
    {
        if (id == null || id.Length != Prefix.Length + 6 || !id.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        for (var i = Prefix.Length; i < id.Length; i++)
        {
            var c = id[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        }
        return true;
    }

    /// <summary>The client name a job's step requests carry; the Pending tab shows the job's title for it.</summary>
    public static string ClientFor(string jobId) => "Job " + jobId;

    public static string? JobIdOfClient(string? client)
        => client != null && client.StartsWith("Job ", StringComparison.Ordinal) && IsValid(client.Substring(4)) ? client.Substring(4) : null;
}
