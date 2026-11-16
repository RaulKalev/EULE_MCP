namespace RevitMCP.Addin.LinkOperations;

/// <summary>
/// How an operation is reported (#163): by the bridge's status route (straight from the files, with the Revit API
/// thread possibly blocked) and by the add-in's tool results. Elapsed time is measured; there is no progress
/// percentage because Revit reports none. Pure.
/// </summary>
public static class LinkOperationStatusView
{
    public static object Describe(LinkOperation op, DateTimeOffset now, LinkOperationLiveness liveness, int? queuePosition, bool detailed = true)
    {
        var effective = liveness.EffectiveState;
        var elapsed = op.Elapsed(now);
        return new
        {
            operationId = op.Id,
            type = op.Type,
            kind = op.Kind,
            source = op.SourcePath,
            cachePath = op.CachePath.Length == 0 ? null : op.CachePath,
            targetDocument = op.TargetTitle,
            positioning = op.Positioning,
            state = op.State,
            effectiveState = effective,
            phase = op.Phase.Length == 0 ? null : op.Phase,
            message = op.Message,
            elapsed = LinkOperationRules.FormatElapsed(elapsed),
            elapsedSeconds = (long)elapsed.TotalSeconds,
            createdAt = op.CreatedAt,
            startedAt = op.StartedAt,
            finishedAt = op.FinishedAt,
            queuePosition,
            ready = effective == LinkOperationStates.Completed && op.Result.Verified,
            cancelRequested = op.CancelRequested,
            needsReview = op.NeedsReview || effective == LinkOperationStates.OutcomeUnknown,
            reviewHint = op.ReviewHint,
            resolvedBy = op.ResolvedBy,
            automaticRetry = "never",
            owner = new
            {
                revitProcessId = op.ProcessId,
                alive = liveness.OwnerAlive,
                heartbeatAgeSeconds = liveness.HeartbeatAgeSeconds.HasValue ? Math.Round(liveness.HeartbeatAgeSeconds.Value, 1) : (double?)null,
                heartbeatStale = liveness.HeartbeatStale
            },
            livenessNote = liveness.Reason,
            requestedBy = op.RequestedBy,
            requestId = op.RequestId,
            result = detailed ? op.Result : null,
            history = detailed ? op.History.Skip(Math.Max(0, op.History.Count - 10)).ToList() : null,
            next = NextStep(op, effective)
        };
    }

    public static string NextStep(LinkOperation op, string effectiveState) => effectiveState switch
    {
        LinkOperationStates.Queued =>
            "Waiting its turn (operations run one at a time). Do not request it again. Check again in 15-30 s; this status does not use Revit.",
        LinkOperationStates.Starting or LinkOperationStates.Converting or LinkOperationStates.Verifying =>
            "Running in Revit. Revit may not answer other calls until it finishes (minutes for a large IFC). Do NOT request the link again, " +
            "do not poll faster than every 15-30 s and do not start other Revit work. There is no progress percentage; elapsed time is measured.",
        LinkOperationStates.Completed =>
            "Done and verified in the document (link loaded, instance present). Safe to query the link, and to sync or save.",
        LinkOperationStates.Failed =>
            "It failed and nothing is retried automatically. Read message and result.checks. A cache file already written is kept. " +
            "A new revit_link_models request makes a new operation if the user wants another attempt.",
        LinkOperationStates.OutcomeUnknown =>
            "The outcome is unknown: Revit stopped answering or restarted while it ran. Do NOT link again. When Revit answers, run " +
            "revit_reconcile_link_operation; if that cannot settle it, ask the user to check the model.",
        LinkOperationStates.Cancelled =>
            "Cancelled before it started; nothing was done.",
        _ => string.Empty
    };

    /// <summary>The list row for revit_list_link_operations.</summary>
    public static object Row(LinkOperation op, DateTimeOffset now, LinkOperationLiveness liveness, int? queuePosition)
    {
        var elapsed = op.Elapsed(now);
        return new
        {
            operationId = op.Id,
            type = op.Type,
            kind = op.Kind,
            file = System.IO.Path.GetFileName(op.SourcePath),
            targetDocument = op.TargetTitle,
            state = op.State,
            effectiveState = liveness.EffectiveState,
            elapsed = LinkOperationRules.FormatElapsed(elapsed),
            queuePosition,
            needsReview = op.NeedsReview || liveness.EffectiveState == LinkOperationStates.OutcomeUnknown,
            message = op.Message
        };
    }

    /// <summary>Position of each queued operation (1 = next), oldest first.</summary>
    public static Dictionary<string, int> QueuePositions(IEnumerable<LinkOperation> all)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        var position = 0;
        foreach (var op in all.Where(o => o.State == LinkOperationStates.Queued).OrderBy(o => o.CreatedAt))
            map[op.Id] = ++position;
        return map;
    }
}
