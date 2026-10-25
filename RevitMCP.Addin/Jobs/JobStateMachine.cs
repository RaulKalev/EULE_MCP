namespace RevitMCP.Addin.Jobs;

public enum StepOutcome
{
    Succeeded,
    Failed,
    Skipped,
    WaitingApproval,
    Busy
}

/// <summary>What the runner does after a step.</summary>
public sealed class JobDecision
{
    public bool Continue { get; set; }
    public JobState? Stop { get; set; }
    public string? Reason { get; set; }

    public static JobDecision Next() => new() { Continue = true };
    public static JobDecision Halt(JobState state, string? reason) => new() { Stop = state, Reason = reason };
}

/// <summary>
/// The job runner's decisions (#100), without timers or Revit: how a step result counts, whether the job goes on,
/// pauses on new Revit warnings, runs out of time, or backs off while Revit is busy. Pure.
/// </summary>
public static class JobStateMachine
{
    public static readonly TimeSpan FirstBusyDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxBusyDelay = TimeSpan.FromSeconds(60);

    public static StepOutcome Classify(JobStep step, bool success, string? status)
    {
        if (success) return StepOutcome.Succeeded;
        return status switch
        {
            "approval_required" => StepOutcome.WaitingApproval,
            "revit_busy" => StepOutcome.Busy,
            "validation_failed" when step.Optional => StepOutcome.Skipped,
            _ => StepOutcome.Failed
        };
    }

    /// <summary>
    /// After a step finished (succeeded, skipped or failed): stop on a failure unless tolerated, honour a pending
    /// cancel or pause, complete after the last step. Updates the step's state for a tolerated failure.
    /// </summary>
    public static JobDecision AfterStep(Job job, JobStep step, StepOutcome outcome)
    {
        switch (outcome)
        {
            case StepOutcome.Succeeded:
                step.State = JobStepState.Succeeded;
                break;
            case StepOutcome.Skipped:
                step.State = JobStepState.Skipped;
                break;
            case StepOutcome.Failed:
                step.State = JobStepState.Failed;
                if (!step.ContinueOnError && job.Stop.StopOnFailure)
                    return JobDecision.Halt(JobState.Failed, $"step {step.Index + 1} ({step.DisplayTitle}) failed: {step.Message}");
                // Tolerated: the job goes on, and a resume does not run the step again.
                step.Tolerated = true;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Only finished outcomes are decided here.");
        }

        if (job.PendingControl == "cancel") return JobDecision.Halt(JobState.Cancelled, "cancelled");
        if (job.PendingControl == "pause") return JobDecision.Halt(JobState.Paused, "paused by the user");
        return job.NextStep == null ? JobDecision.Halt(JobState.Completed, null) : JobDecision.Next();
    }

    /// <summary>
    /// The reason to pause after a write step, given the warnings that are new since the job's checkpoint; null to
    /// go on. Warnings the user accepted by resuming (<see cref="Job.AcceptedNewErrors"/>) do not count again.
    /// </summary>
    public static string? WarningStop(Job job, int newErrors, int newTotal, string? firstNewText)
    {
        var stop = job.Stop;
        var detail = string.IsNullOrEmpty(firstNewText) ? string.Empty : ": " + firstNewText;
        return stop.StopOnNewWarnings switch
        {
            "error" when newErrors - job.AcceptedNewErrors > stop.MaxNewWarnings
                => $"new Revit error{(newErrors - job.AcceptedNewErrors == 1 ? string.Empty : "s")}{detail}",
            "any" when newTotal - job.AcceptedNewWarnings > stop.MaxNewWarnings
                => $"new Revit warning{(newTotal - job.AcceptedNewWarnings == 1 ? string.Empty : "s")}{detail}",
            _ => null
        };
    }

    /// <summary>Resuming accepts the warnings that made the job pause.</summary>
    public static void AcceptWarnings(Job job)
    {
        job.AcceptedNewErrors = Math.Max(job.AcceptedNewErrors, job.NewErrors);
        job.AcceptedNewWarnings = Math.Max(job.AcceptedNewWarnings, job.NewWarnings);
    }

    /// <summary>5 s, 10 s, 20 s, 40 s, then 60 s.</summary>
    public static TimeSpan BusyDelay(int attempt)
    {
        var seconds = FirstBusyDelay.TotalSeconds * Math.Pow(2, Math.Max(0, attempt - 1));
        return TimeSpan.FromSeconds(Math.Min(MaxBusyDelay.TotalSeconds, seconds));
    }

    public static bool BusyExhausted(Job job, int attempts) => attempts >= job.Stop.MaxConsecutiveBusy;

    public static bool DurationExceeded(Job job, long elapsedMs) => elapsedMs >= job.Stop.MaxDurationMinutes * 60_000L;
}
