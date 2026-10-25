using System.Text;
using RevitMCP.Addin.Plans;

namespace RevitMCP.Addin.Jobs;

/// <summary>The approval text of a job (#100): the job line, its steps and stop conditions, then the plan summary. Pure.</summary>
public static class JobSummary
{
    public const int MaxStepsShown = 15;

    public static string Build(JobDefinition def, string? documentTitle = null)
    {
        var job = def.Job;
        var writes = job.Steps.Count(s => !s.IsReadOnly);
        var sb = new StringBuilder();
        sb.Append("JOB: ").Append(job.Title).Append(" — ").Append(job.Steps.Count).Append(job.Steps.Count == 1 ? " step, " : " steps, ")
          .Append(writes).Append(" of them write. ").AppendLine(StopLine(job.Stop));

        var shown = job.Steps.Take(MaxStepsShown).Select(s => $"{s.Index + 1}. {s.DisplayTitle}" + (s.DisplayTitle == s.Tool ? string.Empty : $" ({s.Tool})"));
        sb.Append("Steps: ").Append(string.Join("; ", shown));
        if (job.Steps.Count > MaxStepsShown) sb.Append("; … ").Append(job.Steps.Count - MaxStepsShown).Append(" more");
        sb.AppendLine();
        sb.Append(PlanSummary.Build(def.Plan, documentTitle));
        sb.AppendLine().Append("The connector runs the steps by itself, one at a time, between your own work in Revit; a report page opens when it stops.");
        return sb.ToString();
    }

    /// <summary>
    /// A job as revit_job_status returns it: state, progress, compact steps and, with <paramref name="results"/> &gt; 0,
    /// the stored results of the last finished steps.
    /// </summary>
    public static Dictionary<string, object?> Describe(Job job, DateTimeOffset now, int results = 0)
    {
        var running = job.Steps.FirstOrDefault(s => s.State is JobStepState.Running or JobStepState.WaitingApproval);
        var elapsedMs = job.ElapsedMs + (job.SegmentStartedAt is { } since ? (long)(now - since).TotalMilliseconds : 0);
        var dto = new Dictionary<string, object?>
        {
            ["jobId"] = job.Id,
            ["title"] = job.Title,
            ["state"] = Camel(job.State.ToString()),
            ["stopReason"] = job.StopReason,
            ["document"] = job.DocumentTitle,
            ["client"] = job.ClientName,
            ["planId"] = job.PlanId,
            ["checkpointId"] = job.CheckpointId,
            ["progress"] = new Dictionary<string, object?>
            {
                ["done"] = job.DoneCount,
                ["total"] = job.Steps.Count,
                ["runningStep"] = running == null ? null : running.Index + 1
            },
            ["steps"] = job.Steps.Select(s => new Dictionary<string, object?>
            {
                ["index"] = s.Index + 1,
                ["title"] = s.DisplayTitle,
                ["tool"] = s.Tool,
                ["state"] = Camel(s.State.ToString()) + (s.Tolerated ? " (continued)" : string.Empty),
                ["status"] = s.Status,
                ["message"] = s.Message is { Length: > 200 } m ? m.Substring(0, 200) + "…" : s.Message,
                ["ms"] = s.DurationMs
            }).ToList(),
            ["newWarnings"] = new Dictionary<string, object?> { ["total"] = job.NewWarnings, ["errors"] = job.NewErrors },
            ["elapsedMinutes"] = Math.Round(elapsedMs / 60000.0, 1),
            ["createdAt"] = job.CreatedAt,
            ["finishedAt"] = job.FinishedAt,
            ["reportUrl"] = job.ReportUrl,
            ["resumable"] = job.IsResumable
        };
        if (results > 0)
        {
            dto["results"] = job.Steps.Where(s => s.Result != null).OrderByDescending(s => s.Index).Take(results)
                .Select(s => new Dictionary<string, object?> { ["index"] = s.Index + 1, ["result"] = s.Result }).ToList();
        }
        return dto;
    }

    private static string Camel(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);

    /// <summary>"Stops on the first failure or the first new Revit error." and the like.</summary>
    public static string StopLine(JobStopConditions stop)
    {
        var parts = new List<string>();
        if (stop.StopOnFailure) parts.Add("the first failure");
        var tolerance = stop.MaxNewWarnings > 0 ? $"more than {stop.MaxNewWarnings} new" : "the first new";
        if (stop.StopOnNewWarnings == "error") parts.Add($"{tolerance} Revit error");
        else if (stop.StopOnNewWarnings == "any") parts.Add($"{tolerance} Revit warning");
        parts.Add($"{stop.MaxDurationMinutes} min");
        return "Stops on " + string.Join(", ", parts.Take(parts.Count - 1)) + (parts.Count > 1 ? " or after " : "after ") + parts[parts.Count - 1] + ".";
    }
}
