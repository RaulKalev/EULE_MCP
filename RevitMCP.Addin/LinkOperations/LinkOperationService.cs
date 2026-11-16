using RevitMCP.Core.Models;

namespace RevitMCP.Addin.LinkOperations;

public sealed class LinkAdmitInput
{
    public string Type { get; set; } = LinkOperationTypes.Link;
    public string Kind { get; set; } = LinkKinds.Ifc;
    public string SourcePath { get; set; } = string.Empty;
    public string TargetSelector { get; set; } = string.Empty;
    public string TargetTitle { get; set; } = string.Empty;
    public string TargetKey { get; set; } = string.Empty;
    public string Positioning { get; set; } = "origin";
    public bool Pin { get; set; }
    public bool RegenerateCache { get; set; }
    public long? ExistingLinkTypeId { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
}

public sealed class LinkAdmission
{
    /// <summary>accepted | already_in_progress | needs_review | queue_full.</summary>
    public string Status { get; set; } = "accepted";
    public LinkOperation? Operation { get; set; }
    public string Message { get; set; } = string.Empty;
    public bool Accepted => Status == "accepted";
}

public sealed class LinkCancelOutcome
{
    /// <summary>cancelled | cancel_requested | not_found | already_finished.</summary>
    public string Status { get; set; } = "not_found";
    public LinkOperation? Operation { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// The connector side of durable link operations (#163), without the Revit API. It admits a request exactly once
/// (an open operation for the same source and document is returned instead), writes the record BEFORE Revit is
/// involved, runs queued operations strictly one at a time by dispatching the internal run tool through the
/// connector's own dispatcher (the way JobService does), and settles each operation from what the dispatch
/// returned. It never starts a conversion twice: a dispatch Revit never started (busy) is offered again, but once
/// the run tool reached the conversion every other outcome (timeout, disconnect, restart) is outcome_unknown and
/// waits for a verification. Status reads the store, never Revit.
/// </summary>
public sealed class LinkOperationService
{
    public const string RunTool = "revit_link_operation_run";

    private readonly LinkOperationStore _store;
    private readonly Func<McpToolRequest, int, Task<McpToolResult>> _dispatch;
    private readonly string _sessionId;
    private readonly int _processId;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<int, bool> _processAlive;
    private readonly Func<TimeSpan, Task> _delay;
    private readonly object _gate = new();
    private int _runnerActive;
    private Timer? _timer;

    public LinkOperationService(
        LinkOperationStore store,
        Func<McpToolRequest, int, Task<McpToolResult>> dispatch,
        string? sessionId = null,
        int? processId = null,
        Func<DateTimeOffset>? clock = null,
        Func<int, bool>? processAlive = null,
        Func<TimeSpan, Task>? delay = null)
    {
        _store = store;
        _dispatch = dispatch;
        _sessionId = sessionId ?? Guid.NewGuid().ToString("N");
        _processId = processId ?? System.Diagnostics.Process.GetCurrentProcess().Id;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _processAlive = processAlive ?? IsProcessAlive;
        _delay = delay ?? (span => Task.Delay(span));
    }

    public static LinkOperationService? Current { get; set; }

    public TimeSpan DispatchTimeout { get; set; } = TimeSpan.FromMinutes(120);
    public int MaxDispatchAttempts { get; set; } = 12;
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromSeconds(90);

    public LinkOperationStore Store => _store;
    public string SessionId => _sessionId;

    public static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;   // no such process
        }
        catch (Exception)
        {
            return true;    // cannot tell: assume alive, the safer side for "never start twice"
        }
    }

    /// <summary>Starts the heartbeat/cancel timer. Idempotent.</summary>
    public void Start()
    {
        lock (_gate)
        {
            _timer ??= new Timer(_ => Tick(), null, HeartbeatInterval, HeartbeatInterval);
        }
    }

    public void Tick()
    {
        try
        {
            Heartbeat();
            Sweep();
        }
        catch (Exception)
        {
            // A housekeeping tick never stops anything.
        }
    }

    // ----- recovery -------------------------------------------------------------------------------------------

    /// <summary>
    /// Settles operations whose owner is gone: another add-in load (a restart), or a process that no longer exists. A
    /// queued one is cancelled, one that never reached the conversion fails, one that did is outcome_unknown.
    /// Nothing is queued again and no cache file is touched. Returns the operations changed.
    /// </summary>
    public List<LinkOperation> RecoverOrphans()
    {
        var changed = new List<LinkOperation>();
        foreach (var op in _store.LoadAll())
        {
            if (!LinkOperationStates.IsActive(op.State) || op.SessionId == _sessionId) continue;
            var otherProcessAlive = op.ProcessId != _processId && op.ProcessId > 0 && _processAlive(op.ProcessId);
            if (otherProcessAlive) continue;   // another Revit instance is still working on it

            var updated = _store.Update(op.Id, fresh =>
            {
                if (!LinkOperationStates.IsActive(fresh.State) || fresh.SessionId == _sessionId) return false;
                var target = LinkOperationRules.RecoveryTarget(fresh);
                if (target == null) return false;
                var note = LinkOperationRules.RecoveryNote(fresh);
                if (!LinkOperationRules.Transition(fresh, target, note, _clock())) return false;
                if (target == LinkOperationStates.OutcomeUnknown)
                    fresh.ReviewHint = "Run revit_reconcile_link_operation with the model open. Do not link the file again before that.";
                return true;
            });
            if (updated != null && updated.State != op.State) changed.Add(updated);
        }
        return changed;
    }

    // ----- admission ------------------------------------------------------------------------------------------

    /// <summary>
    /// Admits one request. Under one lock: settle orphans, look for an open operation for the same source and
    /// document (returned as already_in_progress / needs_review) and otherwise write the new record as queued, or as
    /// starting when <paramref name="inline"/> (the reload path runs it on the calling thread).
    /// </summary>
    public LinkAdmission Admit(LinkAdmitInput input, bool inline = false)
    {
        lock (_gate)
        {
            RecoverOrphans();
            var canonical = LinkSourcePath.Canonical(input.SourcePath);
            var decision = LinkOperationRules.Admit(_store.LoadAll(), canonical, input.TargetKey, input.Type);
            switch (decision.Outcome)
            {
                case AdmissionOutcome.AlreadyInProgress:
                    return new LinkAdmission { Status = "already_in_progress", Operation = decision.Existing, Message = decision.Message };
                case AdmissionOutcome.NeedsReview:
                    return new LinkAdmission { Status = "needs_review", Operation = decision.Existing, Message = decision.Message };
                case AdmissionOutcome.QueueFull:
                    return new LinkAdmission { Status = "queue_full", Message = decision.Message };
            }

            var now = _clock();
            var op = new LinkOperation
            {
                Id = LinkOperationIds.New(),
                Type = input.Type,
                Kind = input.Kind,
                SourcePath = input.SourcePath,
                CanonicalSource = canonical,
                CachePath = input.Kind == LinkKinds.Ifc ? LinkSourcePath.CachePathOf(input.SourcePath) : string.Empty,
                TargetKey = input.TargetKey,
                TargetTitle = input.TargetTitle,
                TargetSelector = input.TargetSelector,
                Positioning = input.Positioning,
                Pin = input.Pin,
                RegenerateCache = input.RegenerateCache,
                ExistingLinkTypeId = input.ExistingLinkTypeId,
                RequestedBy = input.RequestedBy,
                RequestId = input.RequestId,
                CreatedAt = now,
                StateChangedAt = now,
                HeartbeatAt = now,
                ProcessId = _processId,
                SessionId = _sessionId,
                State = LinkOperationStates.Queued,
                Message = "Accepted; waiting for its turn (operations run one at a time)."
            };
            op.History.Add(new LinkOperationEvent { At = now, State = op.State, Note = op.Message });
            if (inline)
            {
                op.DispatchToken = "inline";
                LinkOperationRules.Transition(op, LinkOperationStates.Starting, "Started on the Revit thread by the calling tool.", now);
            }
            if (!_store.Create(op))
                return new LinkAdmission { Status = "queue_full", Message = "The operation record could not be written; nothing was started." };
            return new LinkAdmission { Status = "accepted", Operation = op, Message = op.Message };
        }
    }

    /// <summary>Withdraws an inline operation that was admitted but never began (a later file of the same call was refused).</summary>
    public void WithdrawInline(string id) =>
        _store.Update(id, op => LinkOperationRules.Transition(op, LinkOperationStates.Cancelled,
            "Not started: another file of the same call could not be admitted.", _clock()));

    // ----- queue and dispatch ---------------------------------------------------------------------------------

    /// <summary>Starts the runner if it is not running. Safe to call as often as wanted.</summary>
    public void Kick()
    {
        if (Interlocked.CompareExchange(ref _runnerActive, 1, 0) != 0) return;
        _ = Task.Run(RunLoopAsync);
    }

    /// <summary>For tests: runs the queue to its end on the calling task.</summary>
    public async Task RunUntilIdleAsync()
    {
        if (Interlocked.CompareExchange(ref _runnerActive, 1, 0) != 0) return;
        await RunLoopAsync().ConfigureAwait(false);
    }

    private async Task RunLoopAsync()
    {
        try
        {
            while (true)
            {
                Sweep();
                var owned = _store.LoadAll().Where(o => o.SessionId == _sessionId).ToList();
                // One conversion at a time per Revit process: an inline reload (or an earlier dispatch) holds the turn.
                if (owned.Any(o => LinkOperationStates.IsRunning(o.State)))
                {
                    await _delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    continue;
                }

                var now = _clock();
                var queued = owned.Where(o => o.State == LinkOperationStates.Queued).OrderBy(o => o.CreatedAt).ToList();
                if (queued.Count == 0) break;
                var next = queued.FirstOrDefault(o => o.NotBefore == null || o.NotBefore <= now);
                if (next == null)
                {
                    var wait = queued.Min(o => o.NotBefore!.Value) - now;
                    await _delay(wait > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : wait).ConfigureAwait(false);
                    continue;
                }

                var claimed = Claim(next.Id);
                if (claimed == null) continue;
                await DispatchAsync(claimed).ConfigureAwait(false);
            }
        }
        finally
        {
            Volatile.Write(ref _runnerActive, 0);
            // Something may have been queued between the last look and the flag reset.
            if (_store.LoadAll().Any(o => o.SessionId == _sessionId && o.State == LinkOperationStates.Queued)) Kick();
        }
    }

    private LinkOperation? Claim(string id)
    {
        var token = Guid.NewGuid().ToString("N");
        LinkOperation? claimed = null;
        _store.Update(id, op =>
        {
            if (op.State != LinkOperationStates.Queued) return false;
            if (_store.IsCancelRequested(id))
            {
                op.CancelRequested = true;
                LinkOperationRules.Transition(op, LinkOperationStates.Cancelled, "Cancelled before it started, as requested.", _clock());
                return true;
            }
            op.DispatchToken = token;
            op.NotBefore = null;
            if (!LinkOperationRules.Transition(op, LinkOperationStates.Starting, "Handed to Revit.", _clock())) return false;
            claimed = op;
            return true;
        });
        return claimed;
    }

    private async Task DispatchAsync(LinkOperation op)
    {
        var request = new McpToolRequest
        {
            ToolName = RunTool,
            ClientName = "LinkOperations",
            // The approval was given for revit_link_models; this is the connector carrying it out.
            IsApproved = true,
            Arguments = new Dictionary<string, object?>
            {
                ["operationId"] = op.Id,
                ["dispatchToken"] = op.DispatchToken
            }
        };

        McpToolResult result;
        try
        {
            result = await _dispatch(request, (int)Math.Min(int.MaxValue, DispatchTimeout.TotalMilliseconds)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = new McpToolResult { Success = false, Status = "request_cancelled", Message = $"The wait for Revit ended: {ex.Message}" };
        }
        ApplyDispatchResult(op.Id, result.Success, result.Status, result.Message);
    }

    /// <summary>Settles an operation after its dispatch returned: see <see cref="LinkOperationRules.ClassifyDispatch"/>.</summary>
    public LinkOperation? ApplyDispatchResult(string id, bool success, string? status, string? message)
    {
        return _store.Update(id, op =>
        {
            var follow = LinkOperationRules.ClassifyDispatch(op, success, status, MaxDispatchAttempts);
            var now = _clock();
            switch (follow)
            {
                case DispatchFollowUp.Requeue:
                    op.DispatchAttempts++;
                    if (!LinkOperationRules.Transition(op, LinkOperationStates.Queued,
                            $"Revit was busy and did not start it (offer {op.DispatchAttempts}); nothing ran. Offered again shortly.", now))
                        return false;
                    op.NotBefore = now + LinkOperationRules.DispatchBackoff(op.DispatchAttempts);
                    return true;
                case DispatchFollowUp.FailNotStarted:
                    op.DispatchAttempts++;
                    return LinkOperationRules.Transition(op, LinkOperationStates.Failed,
                        $"Revit stayed busy for {op.DispatchAttempts} offers and never started this operation. Nothing was converted or linked. Request it again when Revit is idle.", now);
                case DispatchFollowUp.FailRefused:
                    return LinkOperationRules.Transition(op, LinkOperationStates.Failed,
                        "Revit did not run the operation" + (string.IsNullOrWhiteSpace(message) ? "." : ": " + message), now);
                case DispatchFollowUp.MarkUnknown:
                    var was = op.State;
                    if (!LinkOperationRules.Transition(op, LinkOperationStates.OutcomeUnknown,
                            $"The connector stopped waiting for Revit ({status ?? "no status"}) while the operation was {was}. " +
                            "Revit may still be converting; it is NOT started again. If it finishes, the record updates itself.", now))
                        return false;
                    op.ReviewHint = "Wait for Revit to answer again, then check revit_link_operation_status; if it stays unknown, run revit_reconcile_link_operation.";
                    return true;
                default:
                    return false;
            }
        });
    }

    // ----- run-tool side (called on the Revit API thread) -----------------------------------------------------

    /// <summary>The operation the run tool may execute: state starting and the dispatch token matches. Null otherwise.</summary>
    public LinkOperation? ClaimForRun(string id, string token)
    {
        LinkOperation? claimed = null;
        _store.Update(id, op =>
        {
            if (op.State != LinkOperationStates.Starting || string.IsNullOrEmpty(token) || op.DispatchToken != token) return false;
            if (_store.IsCancelRequested(id))
            {
                op.CancelRequested = true;
                LinkOperationRules.Transition(op, LinkOperationStates.Cancelled, "Cancelled before the conversion began, as requested.", _clock());
                return true;
            }
            claimed = op;
            return false;
        });
        return claimed;
    }

    /// <summary>Persists "converting" BEFORE the blocking Revit call. False when the state does not allow it.</summary>
    public bool BeginConversion(string id, string phase)
    {
        var ok = false;
        _store.Update(id, op =>
        {
            ok = LinkOperationRules.Transition(op, LinkOperationStates.Converting, phase, _clock());
            if (ok) op.Phase = phase;
            return ok;
        });
        return ok;
    }

    public void SetPhase(string id, string phase) =>
        _store.Update(id, op =>
        {
            if (!LinkOperationStates.IsActive(op.State)) return false;
            op.Phase = phase;
            op.HeartbeatAt = _clock();
            return true;
        });

    /// <summary>Converting becomes verifying; from any other state nothing changes (a late finisher keeps its unknown state).</summary>
    public void BeginVerification(string id) =>
        _store.Update(id, op =>
        {
            if (op.State != LinkOperationStates.Converting) return false;
            op.Phase = "verifying the link in the document";
            return LinkOperationRules.Transition(op, LinkOperationStates.Verifying, "Conversion returned; checking the link in the document.", _clock());
        });

    /// <summary>
    /// Settles an operation from a verification verdict. From converting/verifying it completes or fails; from
    /// outcome_unknown it resolves (late completion or reconcile), the only way out of that state.
    /// </summary>
    public LinkOperation? Finish(string id, LinkVerdict verdict, Action<LinkOperationResult>? fill = null, string? resolvedBy = null)
    {
        return _store.Update(id, op =>
        {
            var now = _clock();
            if (op.State == LinkOperationStates.Converting)
                LinkOperationRules.Transition(op, LinkOperationStates.Verifying, "Conversion returned; checking the link in the document.", now);

            fill?.Invoke(op.Result);
            op.Result.Checks = verdict.Checks;
            op.Result.Notes.AddRange(verdict.Notes.Where(n => !op.Result.Notes.Contains(n)));
            op.Result.Verified = verdict.Verified;

            var wasUnknown = op.State == LinkOperationStates.OutcomeUnknown;
            var target = verdict.Outcome;
            if (target == LinkOperationStates.OutcomeUnknown)
            {
                if (wasUnknown) { op.Message = verdict.Reason; return true; }
                return LinkOperationRules.Transition(op, LinkOperationStates.OutcomeUnknown, verdict.Reason, now);
            }
            if (!LinkOperationRules.Transition(op, target, verdict.Reason, now)) return false;
            op.Phase = string.Empty;
            if (wasUnknown) op.ResolvedBy = resolvedBy ?? "late_completion";
            return true;
        });
    }

    /// <summary>Ends an operation as failed from converting, verifying, starting or (late) outcome_unknown.</summary>
    public LinkOperation? Fail(string id, string reason, Action<LinkOperationResult>? fill = null, string? resolvedBy = null)
    {
        return _store.Update(id, op =>
        {
            var wasUnknown = op.State == LinkOperationStates.OutcomeUnknown;
            fill?.Invoke(op.Result);
            if (!LinkOperationRules.Transition(op, LinkOperationStates.Failed, reason, _clock())) return false;
            op.Phase = string.Empty;
            if (wasUnknown) op.ResolvedBy = resolvedBy ?? "late_completion";
            return true;
        });
    }

    /// <summary>The caller can no longer tell what happened (an exception while converting): outcome_unknown, never a retry.</summary>
    public LinkOperation? MarkUnknown(string id, string reason)
    {
        return _store.Update(id, op =>
        {
            if (!LinkOperationRules.Transition(op, LinkOperationStates.OutcomeUnknown, reason, _clock())) return false;
            op.ReviewHint = "Run revit_reconcile_link_operation to check the document.";
            return true;
        });
    }

    // ----- cancel, heartbeat, listing -------------------------------------------------------------------------

    /// <summary>
    /// A conservative cancel: a queued (or just handed-over) operation is cancelled; one that is running is only
    /// marked. The Revit call is never interrupted, no thread is aborted and Revit is not touched.
    /// </summary>
    public LinkCancelOutcome RequestCancel(string id)
    {
        var op = _store.Get(id);
        if (op == null) return new LinkCancelOutcome { Status = "not_found", Message = $"No link operation '{id}'." };
        if (LinkOperationStates.IsTerminal(op.State))
            return new LinkCancelOutcome { Status = "already_finished", Operation = op, Message = $"Operation {id} already ended ({op.State})." };
        _store.RequestCancel(id);
        Sweep();
        op = _store.Get(id) ?? op;
        return op.State == LinkOperationStates.Cancelled
            ? new LinkCancelOutcome { Status = "cancelled", Operation = op, Message = "Cancelled before it started; nothing was done." }
            : new LinkCancelOutcome
            {
                Status = "cancel_requested",
                Operation = op,
                Message = $"Cancel requested. Operation {id} is {op.State}: a conversion that is already running is not interrupted " +
                          "(that is not safe); it finishes and is verified. Only work that has not started is dropped."
            };
    }

    /// <summary>Applies cancel markers: queued operations of this session are cancelled, running ones only marked.</summary>
    public void Sweep()
    {
        foreach (var op in _store.LoadAll())
        {
            if (op.SessionId != _sessionId || !LinkOperationStates.IsActive(op.State) || !_store.IsCancelRequested(op.Id)) continue;
            _store.Update(op.Id, fresh =>
            {
                if (fresh.State == LinkOperationStates.Queued)
                {
                    fresh.CancelRequested = true;
                    fresh.CancelRequestedAt ??= _clock();
                    return LinkOperationRules.Transition(fresh, LinkOperationStates.Cancelled, "Cancelled before it started, as requested.", _clock());
                }
                if (fresh.CancelRequested || !LinkOperationStates.IsActive(fresh.State)) return false;
                fresh.CancelRequested = true;
                fresh.CancelRequestedAt = _clock();
                fresh.History.Add(new LinkOperationEvent { At = _clock(), State = fresh.State, Note = "Cancel requested; the running conversion is not interrupted." });
                return true;
            });
        }
    }

    /// <summary>Writes the add-in's pulse into every active operation it owns. Off the Revit API thread; never touches Revit.</summary>
    public void Heartbeat()
    {
        foreach (var op in _store.LoadAll())
        {
            if (op.SessionId != _sessionId || !LinkOperationStates.IsActive(op.State)) continue;
            _store.Update(op.Id, fresh =>
            {
                if (!LinkOperationStates.IsActive(fresh.State)) return false;
                fresh.HeartbeatAt = _clock();
                return true;
            });
        }
    }

    public List<LinkOperation> List() => _store.LoadAll();

    public string? BusyNote() => LinkOperationRules.BusyNote(_store.LoadAll().Where(o => o.SessionId == _sessionId), _clock());
}
