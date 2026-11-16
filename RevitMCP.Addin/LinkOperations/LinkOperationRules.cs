namespace RevitMCP.Addin.LinkOperations;

/// <summary>How a request for a source relates to the operations already on record.</summary>
public enum AdmissionOutcome
{
    /// <summary>No open operation for this source and document: a new one may be created.</summary>
    New,
    /// <summary>An operation for the same source and document is queued or running: return its id, start nothing.</summary>
    AlreadyInProgress,
    /// <summary>An earlier operation ended without a known outcome: a person (or a reconcile) decides first.</summary>
    NeedsReview,
    /// <summary>Too many operations are open for this Revit process.</summary>
    QueueFull
}

public sealed class AdmissionDecision
{
    public AdmissionOutcome Outcome { get; set; }
    public LinkOperation? Existing { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>What the status route reports about an operation's liveness; computed from the file and the process list only.</summary>
public sealed class LinkOperationLiveness
{
    public string EffectiveState { get; set; } = string.Empty;
    public bool OwnerAlive { get; set; } = true;
    public double? HeartbeatAgeSeconds { get; set; }
    public bool HeartbeatStale { get; set; }
    public string? Reason { get; set; }
}

public enum DispatchFollowUp
{
    /// <summary>The run tool finished the operation itself (or someone else settled it): nothing to do.</summary>
    None,
    /// <summary>Revit never started the run tool (busy): the operation goes back to the queue. No conversion ran.</summary>
    Requeue,
    /// <summary>Revit refused the dispatch too often: the operation fails; nothing was started.</summary>
    FailNotStarted,
    /// <summary>The run tool did not start for a reason that is not "busy" (invalid, unknown tool): the operation fails.</summary>
    FailRefused,
    /// <summary>The caller stopped waiting while Revit was (or may have been) converting: outcome_unknown. Never a retry.</summary>
    MarkUnknown
}

/// <summary>
/// The decisions behind durable, non-retrying link operations (#163): the state machine, exactly-once admission,
/// what a dispatch result means, what a restart leaves behind and how alive a running operation looks. The one rule
/// everything here serves: nothing that reached the conversion is ever started again by the connector. Pure.
/// </summary>
public static class LinkOperationRules
{
    public const int MaxOpenOperations = 25;
    public const int MaxHistory = 40;

    /// <summary>Statuses of an internal dispatch that Revit never started: nothing ran, so the dispatch may be offered again.</summary>
    public static bool IsNotStartedStatus(string? status) => status == "revit_busy" || status == "queue_full";

    public static bool CanTransition(LinkOperation op, string to)
    {
        var from = op.State;
        switch (from)
        {
            case LinkOperationStates.Queued:
                return to is LinkOperationStates.Starting or LinkOperationStates.Cancelled or LinkOperationStates.Failed;
            case LinkOperationStates.Starting:
                // Back to the queue and cancelling are only possible while no conversion has begun.
                if (to is LinkOperationStates.Queued or LinkOperationStates.Cancelled) return op.ConversionStartedAt == null;
                return to is LinkOperationStates.Converting or LinkOperationStates.Failed or LinkOperationStates.OutcomeUnknown;
            case LinkOperationStates.Converting:
                return to is LinkOperationStates.Verifying or LinkOperationStates.Failed or LinkOperationStates.OutcomeUnknown;
            case LinkOperationStates.Verifying:
                return to is LinkOperationStates.Completed or LinkOperationStates.Failed or LinkOperationStates.OutcomeUnknown;
            case LinkOperationStates.OutcomeUnknown:
                // Only a verification settles it; it never goes back to a state that starts work.
                return to is LinkOperationStates.Completed or LinkOperationStates.Failed;
            default:
                return false;   // completed, failed, cancelled: final
        }
    }

    /// <summary>Applies a legal transition, with its timestamps and history entry. False (and no change) for an illegal one.</summary>
    public static bool Transition(LinkOperation op, string to, string note, DateTimeOffset now)
    {
        if (!CanTransition(op, to)) return false;
        var from = op.State;
        op.State = to;
        op.StateChangedAt = now;
        op.Message = note;
        switch (to)
        {
            case LinkOperationStates.Starting:
                op.StartedAt ??= now;
                break;
            case LinkOperationStates.Converting:
                op.ConversionStartedAt ??= now;
                break;
            case LinkOperationStates.Queued:
                op.StartedAt = null;
                op.DispatchToken = string.Empty;
                break;
        }
        if (LinkOperationStates.IsTerminal(to)) op.FinishedAt = now;
        if (to == LinkOperationStates.OutcomeUnknown) op.NeedsReview = true;
        if (from == LinkOperationStates.OutcomeUnknown)
        {
            op.NeedsReview = false;
            op.ReviewHint = null;
        }
        op.History.Add(new LinkOperationEvent { At = now, State = to, Note = note });
        if (op.History.Count > MaxHistory) op.History.RemoveRange(0, op.History.Count - MaxHistory);
        return true;
    }

    /// <summary>
    /// Exactly-once admission: decides whether a request for <paramref name="source"/> in <paramref name="target"/> may
    /// create an operation. An open operation for the same pair (queued, running or outcome_unknown) answers with that
    /// operation, whatever its type, so two clients racing, a reconnecting agent and a refresh next to a link all end
    /// up with one operation.
    /// </summary>
    public static AdmissionDecision Admit(IEnumerable<LinkOperation> all, string canonicalSource, string targetKey, string type, int maxOpen = MaxOpenOperations)
    {
        var open = all.Where(o => LinkOperationStates.BlocksNewRequests(o.State)).ToList();
        var same = open.Where(o => o.CanonicalSource == canonicalSource && o.TargetKey == targetKey)
            .OrderBy(o => o.CreatedAt).FirstOrDefault();
        if (same != null)
        {
            if (same.State == LinkOperationStates.OutcomeUnknown)
                return new AdmissionDecision
                {
                    Outcome = AdmissionOutcome.NeedsReview,
                    Existing = same,
                    Message = $"Operation {same.Id} for this file ended without a known outcome (Revit stopped answering or restarted while it ran). " +
                              "Nothing was started again. Run revit_reconcile_link_operation to check the document; ask the user if it cannot settle it."
                };
            return new AdmissionDecision
            {
                Outcome = AdmissionOutcome.AlreadyInProgress,
                Existing = same,
                Message = same.Type == type
                    ? $"Operation {same.Id} already {Describe(same.State)} this file; nothing new was started. Follow it with revit_link_operation_status."
                    : $"Operation {same.Id} ({same.Type}) already {Describe(same.State)} this file; nothing new was started."
            };
        }

        if (open.Count(o => o.State != LinkOperationStates.OutcomeUnknown) >= maxOpen)
            return new AdmissionDecision
            {
                Outcome = AdmissionOutcome.QueueFull,
                Message = $"{maxOpen} link operations are already queued or running. Wait for them to finish (revit_list_link_operations)."
            };

        return new AdmissionDecision { Outcome = AdmissionOutcome.New };
    }

    private static string Describe(string state) => state switch
    {
        LinkOperationStates.Queued => "has queued",
        LinkOperationStates.Starting => "is starting",
        LinkOperationStates.Converting => "is converting",
        LinkOperationStates.Verifying => "is verifying",
        _ => "has"
    };

    /// <summary>What an internal dispatch's result means for an operation whose run tool may not have finished it. Never schedules a retry of a conversion.</summary>
    public static DispatchFollowUp ClassifyDispatch(LinkOperation current, bool success, string? status, int maxDispatchAttempts)
    {
        if (LinkOperationStates.IsTerminal(current.State) || current.State == LinkOperationStates.OutcomeUnknown ||
            current.State == LinkOperationStates.Queued)
            return DispatchFollowUp.None;

        // The tool got as far as converting or verifying but did not finish: the caller's wait ended first.
        if (current.State == LinkOperationStates.Converting || current.State == LinkOperationStates.Verifying)
            return DispatchFollowUp.MarkUnknown;

        // Still "starting": the run tool wrote nothing yet.
        if (current.ConversionStartedAt != null) return DispatchFollowUp.MarkUnknown;
        if (!success && IsNotStartedStatus(status))
            return current.DispatchAttempts + 1 >= maxDispatchAttempts ? DispatchFollowUp.FailNotStarted : DispatchFollowUp.Requeue;
        if (!success && status is "request_timeout" or "request_cancelled") return DispatchFollowUp.MarkUnknown;
        if (!success) return DispatchFollowUp.FailRefused;
        return DispatchFollowUp.MarkUnknown;
    }

    /// <summary>Seconds to wait before offering a refused dispatch again: 5, 10, 20, 40, then 60.</summary>
    public static TimeSpan DispatchBackoff(int attempts)
    {
        var seconds = 5 * Math.Pow(2, Math.Max(0, attempts - 1));
        return TimeSpan.FromSeconds(Math.Min(60, seconds));
    }

    /// <summary>
    /// What a restart (or the loss of the owning process) leaves behind, by state: a queued operation never began, so it
    /// is cancelled; one that never reached the conversion fails; one that did is outcome_unknown. Null for a state
    /// that needs no recovery. Never "queued again".
    /// </summary>
    public static string? RecoveryTarget(LinkOperation op)
    {
        switch (op.State)
        {
            case LinkOperationStates.Queued:
                return LinkOperationStates.Cancelled;
            case LinkOperationStates.Starting when op.ConversionStartedAt == null:
                return LinkOperationStates.Failed;
            case LinkOperationStates.Starting:
            case LinkOperationStates.Converting:
            case LinkOperationStates.Verifying:
                return LinkOperationStates.OutcomeUnknown;
            default:
                return null;
        }
    }

    public static string RecoveryNote(LinkOperation op) => RecoveryTarget(op) switch
    {
        LinkOperationStates.Cancelled => "Revit closed before this operation began. Nothing was done and nothing will start by itself; request it again if it is still wanted.",
        LinkOperationStates.Failed => "Revit closed before the conversion began. Nothing was done and nothing will start by itself.",
        LinkOperationStates.OutcomeUnknown => "Revit closed or restarted while this operation was running. The link may or may not exist. " +
                                              "It is not started again; run revit_reconcile_link_operation once the model is open, or review the model.",
        _ => string.Empty
    };

    /// <summary>
    /// How alive an operation looks from the files and the process list alone (no Revit API): the effective state, whether
    /// its owner process still exists and how old the add-in's last heartbeat is. A running operation whose process is
    /// gone is outcome_unknown; a stale heartbeat is only a warning (the add-in may simply be unloaded or Revit hung).
    /// </summary>
    public static LinkOperationLiveness Assess(LinkOperation op, DateTimeOffset now, Func<int, bool> processAlive, TimeSpan staleAfter)
    {
        var result = new LinkOperationLiveness { EffectiveState = op.State };
        if (!LinkOperationStates.IsActive(op.State)) return result;

        var alive = op.ProcessId <= 0 || processAlive(op.ProcessId);
        result.OwnerAlive = alive;
        var last = op.HeartbeatAt ?? op.StateChangedAt;
        result.HeartbeatAgeSeconds = Math.Max(0, (now - last).TotalSeconds);

        if (!alive)
        {
            result.EffectiveState = op.State == LinkOperationStates.Queued
                ? LinkOperationStates.Cancelled
                : op.State == LinkOperationStates.Starting && op.ConversionStartedAt == null
                    ? LinkOperationStates.Failed
                    : LinkOperationStates.OutcomeUnknown;
            result.Reason = $"The Revit process that owned this operation (pid {op.ProcessId}) is no longer running. " +
                            (result.EffectiveState == LinkOperationStates.OutcomeUnknown
                                ? "The link may or may not exist; reconcile after Revit is open again."
                                : "Nothing was started.");
            return result;
        }

        if (LinkOperationStates.IsRunning(op.State) && now - last > staleAfter)
        {
            result.HeartbeatStale = true;
            result.Reason = $"No heartbeat from the add-in for {(int)(now - last).TotalSeconds} s. Revit may be hung or the add-in unloaded; " +
                            "do not start the link again. The operation is only treated as unknown once Revit is gone.";
        }
        return result;
    }

    /// <summary>One line naming the operation that currently holds Revit, for busy messages; null when none is running.</summary>
    public static string? BusyNote(IEnumerable<LinkOperation> all, DateTimeOffset now)
    {
        var running = all.Where(o => LinkOperationStates.IsRunning(o.State)).OrderBy(o => o.StartedAt ?? o.CreatedAt).FirstOrDefault();
        if (running == null) return null;
        var queued = all.Count(o => o.State == LinkOperationStates.Queued);
        return $"A model link operation is running ({running.Id}: {System.IO.Path.GetFileName(running.SourcePath)}, {running.State}, " +
               $"{FormatElapsed(running.Elapsed(now))} so far); {queued} more queued. Revit does not answer until it finishes: " +
               "do not retry or link again; follow it with revit_link_operation_status (it answers without Revit).";
    }

    public static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds < 90) return $"{(int)elapsed.TotalSeconds} s";
        if (elapsed.TotalMinutes < 120) return $"{(int)elapsed.TotalMinutes} min {elapsed.Seconds} s";
        return $"{(int)elapsed.TotalHours} h {elapsed.Minutes} min";
    }
}
