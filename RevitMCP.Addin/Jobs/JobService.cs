using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Approval;

using RevitMCP.Addin.Plans;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Jobs;

/// <summary>
/// Runs jobs (#100): one step per dispatch through the connector's own dispatcher — the same path as any client,
/// so plan authorization, approvals, the activity log and revit_busy all apply — never touching the Revit API
/// itself. Between steps the queue is empty and the user's own Revit commands and other clients interleave.
/// The job's state is journaled after every change; a Revit restart leaves it Suspended for revit_job_resume.
/// Wired in App; the plan and checkpoint for a job are created by the job tools on the API thread.
/// </summary>
public sealed class JobService
{
    public const string ReportTool = "revit_job_report";
    public const string WarningsTool = "revit_get_warnings";

    private readonly Func<McpToolRequest, int, Task<McpToolResult>> _dispatch;
    private readonly ApprovalService? _approvals;
    private readonly JobJournalStore _journal;
    private readonly Func<string, ToolPermission?> _permissionOf;
    private readonly Func<string, bool> _isBackground;
    private readonly Action<McpToolRequest, McpToolResult>? _log;
    private readonly Func<string?, Plan?> _planOf;
    private readonly Action<Plan, PlanState, string> _finishPlan;
    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Run> _runs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _changed = new(StringComparer.Ordinal);

    public JobService(
        Func<McpToolRequest, int, Task<McpToolResult>> dispatch,
        ApprovalService? approvals,
        JobJournalStore journal,
        Func<string, ToolPermission?> permissionOf,
        Func<string, bool> isBackground,
        Func<string?, Plan?> planOf,
        Action<Plan, PlanState, string> finishPlan,
        Action<McpToolRequest, McpToolResult>? log)
    {
        _dispatch = dispatch;
        _approvals = approvals;
        _journal = journal;
        _permissionOf = permissionOf;
        _isBackground = isBackground;
        _planOf = planOf;
        _finishPlan = finishPlan;
        _log = log;
    }

    public static JobService? Current { get; set; }

    /// <summary>Raised after any job change, from whatever thread made it.</summary>
    public event Action? JobsChanged;

    public JobDefinition Parse(McpToolRequest request)
        => JobDefinitionParser.Parse(request.Arguments, _permissionOf, _isBackground);

    public PlanDefinition PlanFor(Job job) => JobDefinitionParser.PlanFor(job, _permissionOf, _isBackground);

    public Job? Get(string? id) => id != null && _jobs.TryGetValue(id, out var job) ? job : null;

    /// <summary>Newest first.</summary>
    public List<Job> All() => _jobs.Values.OrderByDescending(j => j.CreatedAt).ToList();

    /// <summary>Loads the journals of earlier sessions; unfinished jobs come back Suspended. Never throws.</summary>
    public void LoadSuspended()
    {
        try
        {
            foreach (var job in _journal.LoadAll())
                _jobs.TryAdd(job.Id, job);
        }
        catch { }
    }

    /// <summary>Registers a job whose plan was activated and starts running it.</summary>
    public void Start(Job job)
    {
        job.State = JobState.Running;
        job.StartedAt ??= DateTimeOffset.Now;
        job.StopReason = null;
        job.PendingControl = null;
        _jobs[job.Id] = job;
        LogEvent(job, "revit_job_start", true, $"Job {job.Id} '{job.Title}' started: {job.Steps.Count} step(s), plan {job.PlanId}.");
        Kick(job);
    }

    /// <summary>Continues a paused, suspended or failed job whose plan the resume tool has (re)activated.</summary>
    public void Resume(Job job)
    {
        JobStateMachine.AcceptWarnings(job);
        foreach (var step in job.Steps.Where(s => s.State is JobStepState.Failed or JobStepState.Cancelled or JobStepState.Running or JobStepState.WaitingApproval && !s.Tolerated))
            step.State = JobStepState.Pending;
        job.State = JobState.Running;
        job.StopReason = null;
        job.PendingControl = null;
        job.FinishedAt = null;
        LogEvent(job, "revit_job_resume", true, $"Job {job.Id} resumed at step {(job.NextStep?.Index ?? job.Steps.Count) + 1}.");
        Kick(job);
    }

    /// <summary>Pause after the running step. False when the job is not running.</summary>
    public bool Pause(string jobId)
    {
        var job = Get(jobId);
        if (job == null || job.State is not (JobState.Running or JobState.WaitingApproval)) return false;
        job.PendingControl = "pause";
        Changed(job);
        return true;
    }

    /// <summary>
    /// Cancel: a running job stops after its running step, a waiting one at once (its parked approval is rejected),
    /// a paused or suspended one now. Nothing is undone. False when the job had already ended.
    /// </summary>
    public bool Cancel(string jobId, string reason = "cancelled")
    {
        var job = Get(jobId);
        if (job == null || job.IsTerminal) return false;
        if (_runs.TryGetValue(jobId, out var run))
        {
            job.PendingControl = "cancel";
            run.CancelWait();
            Changed(job);
            return true;
        }
        _ = FinishAsync(job, JobState.Cancelled, reason);
        return true;
    }

    public void CancelAll(string reason)
    {
        foreach (var job in _jobs.Values.Where(j => !j.IsTerminal).ToList()) Cancel(job.Id, reason);
    }

    public bool Remove(string jobId)
    {
        var job = Get(jobId);
        if (job == null || _runs.ContainsKey(jobId)) return false;
        _jobs.TryRemove(jobId, out _);
        var removed = _journal.Remove(job.ModelId, jobId);
        JobsChanged?.Invoke();
        return removed;
    }

    /// <summary>Completes when the job changes or the timeout passes (revit_job_status waitSeconds).</summary>
    public Task WaitForChangeAsync(string jobId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var signal = _changed.GetOrAdd(jobId, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        return Task.WhenAny(signal.Task, Task.Delay(timeout, cancellationToken));
    }

    /// <summary>Saves the job off the API thread and wakes long-polls and the window.</summary>
    public void Changed(Job job)
    {
        _ = Task.Run(() =>
        {
            try { _journal.Save(job); } catch { /* a journal write never stops a job */ }
        });
        if (_changed.TryRemove(job.Id, out var signal)) signal.TrySetResult(true);
        JobsChanged?.Invoke();
    }

    private void Kick(Job job)
    {
        var run = new Run();
        if (!_runs.TryAdd(job.Id, run)) return;   // already running
        Changed(job);
        _ = Task.Run(() => RunAsync(job, run));
    }

    private async Task RunAsync(Job job, Run run)
    {
        var segment = System.Diagnostics.Stopwatch.StartNew();
        job.SegmentStartedAt = DateTimeOffset.Now;
        long Elapsed() => job.ElapsedMs + segment.ElapsedMilliseconds;
        try
        {
            while (true)
            {
                if (job.PendingControl == "cancel") { await StopAsync(job, run, segment, JobState.Cancelled, "cancelled"); return; }
                if (job.PendingControl == "pause") { await StopAsync(job, run, segment, JobState.Paused, "paused by the user"); return; }

                var step = job.NextStep;
                if (step == null) { await StopAsync(job, run, segment, JobState.Completed, null); return; }

                if (JobStateMachine.DurationExceeded(job, Elapsed()))
                {
                    await StopAsync(job, run, segment, JobState.Paused, $"the job's time limit of {job.Stop.MaxDurationMinutes} min was reached");
                    return;
                }

                var plan = _planOf(job.PlanId);
                if (plan != null && !step.IsReadOnly && plan.State is PlanState.Expired or PlanState.Revoked or PlanState.Closed)
                {
                    await StopAsync(job, run, segment, JobState.Suspended, $"plan {plan.Id} is {plan.State.ToString().ToLowerInvariant()}");
                    return;
                }

                var outcome = await RunStepAsync(job, step, run);
                if (outcome == null) continue;                   // busy: same step again after the backoff
                if (outcome == StepOutcome.Busy)
                {
                    await StopAsync(job, run, segment, JobState.Suspended, $"Revit stayed busy for {job.Stop.MaxConsecutiveBusy} attempts at step {step.Index + 1}");
                    return;
                }

                var decision = JobStateMachine.AfterStep(job, step, outcome.Value);
                Changed(job);

                if (!step.IsReadOnly && outcome == StepOutcome.Succeeded && decision.Stop is null or JobState.Completed)
                {
                    var warningStop = await CheckWarningsAsync(job);
                    if (warningStop != null)
                    {
                        await StopAsync(job, run, segment, JobState.Paused, warningStop);
                        return;
                    }
                }

                if (decision.Stop is { } state)
                {
                    await StopAsync(job, run, segment, state, decision.Reason);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            await StopAsync(job, run, segment, JobState.Suspended, "the job runner failed: " + ex.Message);
        }
    }

    /// <summary>Runs one step to a finished outcome; null when Revit was busy and the step should be retried.</summary>
    private async Task<StepOutcome?> RunStepAsync(Job job, JobStep step, Run run)
    {
        var arguments = JobJsonPath.Substitute(step.Arguments, job.Variables, out var missing);
        if (arguments == null)
        {
            Record(step, false, "job_step_invalid", $"{{{{{missing}}}}} is not bound (the step that binds it found nothing)", null, 0);
            return StepOutcome.Failed;
        }
        if (job.PlanId != null && !step.IsReadOnly && arguments["planId"] == null) arguments["planId"] = job.PlanId;

        var request = new McpToolRequest
        {
            ToolName = step.Tool,
            ClientName = JobIds.ClientFor(job.Id),
            Arguments = arguments.Properties().ToDictionary(p => p.Name, p => (object?)p.Value, StringComparer.Ordinal)
        };
        step.State = JobStepState.Running;
        step.StartedAt ??= DateTimeOffset.Now;
        step.Attempts++;
        step.RequestId = request.RequestId;
        Changed(job);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = await _dispatch(request, RevitMCP.Addin.Services.LongRunningTools.TimeoutMsFor(step.Tool, 30_000)).ConfigureAwait(false);
        var outcome = JobStateMachine.Classify(step, result.Success, result.Status);

        if (outcome == StepOutcome.WaitingApproval)
        {
            step.State = JobStepState.WaitingApproval;
            job.State = JobState.WaitingApproval;
            step.Message = "waiting for approval in Revit: " + result.Message;
            Changed(job);
            result = await run.WaitForApprovalAsync(_approvals, request.RequestId).ConfigureAwait(false);
            if (job.State == JobState.WaitingApproval) job.State = JobState.Running;
            outcome = JobStateMachine.Classify(step, result.Success, result.Status);
            if (outcome is StepOutcome.WaitingApproval or StepOutcome.Busy) outcome = StepOutcome.Failed;
        }

        if (outcome == StepOutcome.Busy)
        {
            run.Busy++;
            step.State = JobStepState.Pending;
            step.Message = $"Revit is busy (attempt {run.Busy}); retrying";
            Changed(job);
            if (JobStateMachine.BusyExhausted(job, run.Busy)) return StepOutcome.Busy;
            await run.DelayAsync(JobStateMachine.BusyDelay(run.Busy)).ConfigureAwait(false);
            return null;
        }
        run.Busy = 0;

        var json = ToJson(result);
        Record(step, result.Success, result.Status, result.Message, json, started.ElapsedMilliseconds);
        if (result.Success && step.Bind != null)
        {
            foreach (var bind in step.Bind)
            {
                var value = JobJsonPath.Select(json, bind.Value);
                if (value != null) job.Variables[bind.Key] = JobJsonPath.AsIds(value);
                else job.Variables.Remove(bind.Key);
            }
        }
        return outcome;
    }

    /// <summary>New Revit warnings since the job's checkpoint, as an internal revit_get_warnings request.</summary>
    private async Task<string?> CheckWarningsAsync(Job job)
    {
        if (job.CheckpointId == null || job.Stop.StopOnNewWarnings == "none") return null;
        var request = new McpToolRequest
        {
            ToolName = WarningsTool,
            ClientName = JobIds.ClientFor(job.Id),
            Arguments = new Dictionary<string, object?>
            {
                ["sinceCheckpointId"] = job.CheckpointId,
                ["severity"] = "all",
                ["groupBy"] = "description",
                ["pageSize"] = 5
            }
        };
        McpToolResult result;
        try { result = await _dispatch(request, 30_000).ConfigureAwait(false); }
        catch { return null; }
        if (!result.Success) return null;   // warnings could not be read: do not stop a job for that

        var data = ToJson(result)["data"];
        job.NewWarnings = (int?)data?["total"] ?? 0;
        job.NewErrors = (int?)data?["bySeverity"]?["error"] ?? 0;
        var first = (string?)data?["rows"]?.FirstOrDefault()?["description"];
        return JobStateMachine.WarningStop(job, job.NewErrors, job.NewWarnings, first);
    }

    private async Task StopAsync(Job job, Run run, System.Diagnostics.Stopwatch segment, JobState state, string? reason)
    {
        job.ElapsedMs += segment.ElapsedMilliseconds;
        job.SegmentStartedAt = null;
        segment.Reset();
        _runs.TryRemove(job.Id, out _);
        foreach (var step in job.Steps.Where(s => s.State is JobStepState.Running or JobStepState.WaitingApproval))
            step.State = state == JobState.Cancelled ? JobStepState.Cancelled : JobStepState.Pending;
        await FinishAsync(job, state, reason).ConfigureAwait(false);
    }

    private async Task FinishAsync(Job job, JobState state, string? reason)
    {
        job.State = state;
        job.StopReason = reason;
        job.PendingControl = null;
        if (job.IsTerminal)
        {
            job.FinishedAt = DateTimeOffset.Now;
            foreach (var step in job.Steps.Where(s => s.State == JobStepState.Pending && state == JobState.Cancelled))
                step.State = JobStepState.Cancelled;
            if (_planOf(job.PlanId) is { } plan && !plan.IsEnded)
                _finishPlan(plan, state == JobState.Cancelled ? PlanState.Revoked : PlanState.Closed, $"job {job.Id} {state.ToString().ToLowerInvariant()}");
        }
        var verb = state switch
        {
            JobState.Completed => "revit_job_complete",
            JobState.Cancelled => "revit_job_cancel",
            JobState.Paused => "revit_job_pause",
            JobState.Suspended => "revit_job_suspend",
            _ => "revit_job_fail"
        };
        LogEvent(job, verb, state is JobState.Completed or JobState.Paused,
            $"Job {job.Id} '{job.Title}' {state.ToString().ToLowerInvariant()}{(reason != null ? ": " + reason : string.Empty)} ({job.DoneCount}/{job.Steps.Count} steps).");
        Changed(job);
        if (job.Report == "auto") await PublishReportAsync(job, open: job.OpenReport && job.ReportUrl == null).ConfigureAwait(false);
    }

    /// <summary>Publishes (or re-publishes) the handoff page through the internal revit_job_report request.</summary>
    public async Task<McpToolResult> PublishReportAsync(Job job, bool open)
    {
        var request = new McpToolRequest
        {
            ToolName = ReportTool,
            ClientName = JobIds.ClientFor(job.Id),
            Arguments = new Dictionary<string, object?> { ["jobId"] = job.Id, ["open"] = open }
        };
        try
        {
            var result = await _dispatch(request, 30_000).ConfigureAwait(false);
            if (result.Success) LogEvent(job, ReportTool, true, $"Report of job {job.Id} published: {job.ReportUrl}");
            Changed(job);
            return result;
        }
        catch (Exception ex)
        {
            return new McpToolResult { RequestId = request.RequestId, Success = false, Status = "tool_execution_failed", Message = ex.Message };
        }
    }

    private static void Record(JobStep step, bool success, string? status, string? message, JObject? json, long ms)
    {
        step.Status = status ?? (success ? "ok" : "failed");
        step.Message = message is { Length: > 500 } ? message.Substring(0, 500) + "…" : message;
        step.DurationMs = ms;
        step.FinishedAt = DateTimeOffset.Now;
        step.Result = json == null ? null : JobJournalStore.Trim(json);
    }

    /// <summary>The result as the bridge shows it (success, status, message, data, warnings): what bind paths address.</summary>
    private static JObject ToJson(McpToolResult result)
    {
        JToken? data = null;
        try { data = result.Data == null ? null : JToken.FromObject(result.Data); } catch { }
        return new JObject
        {
            ["success"] = result.Success,
            ["status"] = result.Status,
            ["message"] = result.Message,
            ["data"] = data,
            ["warnings"] = new JArray(result.Warnings)
        };
    }

    private void LogEvent(Job job, string tool, bool success, string message)
    {
        if (_log == null) return;
        var request = new McpToolRequest
        {
            ToolName = tool,
            ClientName = JobIds.ClientFor(job.Id),
            Arguments = new Dictionary<string, object?> { ["jobId"] = job.Id, ["planId"] = job.PlanId }
        };
        _log(request, new McpToolResult { RequestId = request.RequestId, Success = success, Message = message });
    }

    /// <summary>One run of a job: its approval wait and backoff, cancellable together.</summary>
    private sealed class Run
    {
        private readonly CancellationTokenSource _cancel = new();
        public int Busy;

        public async Task<McpToolResult> WaitForApprovalAsync(ApprovalService? approvals, string requestId)
        {
            var wait = new TaskCompletionSource<McpToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnOutcome(ApprovalOutcome outcome)
            {
                if (outcome.RequestId == requestId && outcome.IsFinal && outcome.Result != null) wait.TrySetResult(outcome.Result);
            }
            if (approvals == null)
                return new McpToolResult { RequestId = requestId, Success = false, Status = "approval_required", Message = "No approval service." };
            approvals.OutcomeRecorded += OnOutcome;
            try
            {
                // The decision may have landed before the subscription.
                if (approvals.GetOutcome(requestId) is { IsFinal: true, Result: { } done }) wait.TrySetResult(done);
                using (_cancel.Token.Register(() =>
                {
                    if (approvals.GetOutcome(requestId) is { } pending && !pending.IsFinal) approvals.Reject(pending.ApprovalId);
                    wait.TrySetResult(new McpToolResult { RequestId = requestId, Success = false, Status = "request_cancelled", Message = "The job was cancelled while the step waited for approval." });
                }))
                {
                    return await wait.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                approvals.OutcomeRecorded -= OnOutcome;
            }
        }

        public Task DelayAsync(TimeSpan delay) => Task.Delay(delay, _cancel.Token).ContinueWith(_ => { }, TaskScheduler.Default);

        /// <summary>Ends an approval wait or a busy backoff now; the loop then sees the pending cancel.</summary>
        public void CancelWait() => _cancel.Cancel();
    }
}
