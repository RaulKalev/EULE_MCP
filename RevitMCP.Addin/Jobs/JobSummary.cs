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
