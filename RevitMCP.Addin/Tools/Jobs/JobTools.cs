using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Approval;
using RevitMCP.Addin.Checkpoints;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Jobs;
using RevitMCP.Addin.ModelPages.Hosting;
using RevitMCP.Addin.Plans;
using RevitMCP.Addin.Tools.Checkpoints;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Jobs;

internal static class JobToolSupport
{
    public const string NotFound = "job_not_found";
    public const string NotResumable = "job_not_resumable";
    public const string StepInvalid = "job_step_invalid";

    public static McpToolResult Fail(McpToolRequest request, string message, string status = "validation_failed", object? data = null)
        => new() { RequestId = request.RequestId, Success = false, Status = status, Message = message, Data = data };

    public static JobService? Service(McpToolRequest request, out McpToolResult? failure)
    {
        var service = JobService.Current;
        failure = service == null ? Fail(request, "Jobs are not available: the connector's job service did not start.", "tool_execution_failed") : null;
        return service;
    }

    public static Job? Find(JobService service, McpToolRequest request, out McpToolResult? failure)
    {
        var id = ToolArguments.GetString(request.Arguments, "jobId").Trim();
        var job = service.Get(id);
        failure = job == null
            ? Fail(request, id.Length == 0 ? "jobId is required." : $"Job {id} was not found (revit_job_list shows the jobs of this and earlier sessions).", NotFound)
            : null;
        return job;
    }

    /// <summary>The open project document a job belongs to, by model id.</summary>
    public static Document? DocumentOf(UIApplication uiapp, Job job)
        => RevitDocumentResolver.GetProjectDocuments(uiapp).FirstOrDefault(d => CheckpointChangeTracker.ModelIdOf(d) == job.ModelId);

    /// <summary>
    /// Activates a plan for <paramref name="job"/> on <paramref name="target"/> (submit and resume): the job's plan
    /// id, checkpoint, document and model come from it. Null with <paramref name="failure"/> when it cannot start.
    /// </summary>
    public static Plan? ActivatePlan(
        UIApplication uiapp, RevitDocumentResolver.DocumentTarget target, Job job, PlanDefinition definition,
        McpToolRequest request, CancellationToken cancellationToken, List<string> warnings, out McpToolResult? failure)
    {
        var plans = PlanService.Current;
        if (plans == null)
        {
            failure = Fail(request, "Plans are not available: the connector's plan service did not start.", "tool_execution_failed");
            return null;
        }
        var plan = plans.Activate(uiapp, target, definition, request, cancellationToken, warnings, out failure);
        if (plan == null) return null;
        job.PlanId = plan.Id;
        job.CheckpointId = plan.CheckpointId;
        job.ModelId = plan.ModelId;
        job.DocumentTitle = plan.DocumentTitle;
        job.DocumentToken = target.Document;
        return plan;
    }
}

/// <summary>revit_job_submit (#100): a whole job of tool calls, approved once, run by the connector.</summary>
public sealed class JobSubmitTool : IRevitMcpTool, IConditionalApprovalTool, IApprovalSummaryTool
{
    public string Name => "revit_job_submit";
    public string Description =>
        "Submits a job: an ordered list of tool calls (steps) the connector runs by itself after ONE approval, while the " +
        "user works on. Steps: {tool, arguments, bind:{var:\"$.data.items[*].id\"}, title, continueOnError, optional}; " +
        "later steps use \"{{var}}\". The approval activates a plan (envelope) and a checkpoint. Stops on failures, new " +
        "Revit errors or time (stop{}); a report page opens at the end. Follow it with revit_job_status waitSeconds=25.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    /// <summary>An invalid job is refused at once instead of being parked for an approval it could never use.</summary>
    public bool NeedsApproval(McpToolRequest request) => JobService.Current?.Parse(request).IsValid ?? false;

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request)
    {
        var def = JobService.Current?.Parse(request);
        return def == null ? null : JobSummary.Build(def, RevitDocumentResolver.ResolveApprovalDocument(uiapp, request)?.Title);
    }

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var service = JobToolSupport.Service(request, out var failure);
        if (service == null) return Task.FromResult(failure!);

        var def = service.Parse(request);
        if (!def.IsValid)
            return Task.FromResult(JobToolSupport.Fail(request, "The job was not submitted: " + string.Join(" ", def.Errors),
                JobToolSupport.StepInvalid, new { errors = def.Errors, warnings = def.Warnings }));

        var target = RevitDocumentResolver.Resolve(uiapp, request, out var error);
        if (target == null) return Task.FromResult(JobToolSupport.Fail(request, error ?? "No open project document."));

        var job = def.Job;
        job.ClientName = request.ClientName;
        var warnings = new List<string>(def.Warnings);
        var plan = JobToolSupport.ActivatePlan(uiapp, target, job, def.Plan, request, cancellationToken, warnings, out failure);
        if (plan == null) return Task.FromResult(failure!);
        service.Start(job);

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Job {job.Id} '{job.Title}' started on '{job.DocumentTitle}': {job.Steps.Count} step(s) under plan {plan.Id}. " +
                      $"Poll with revit_job_status jobId={job.Id} waitSeconds=25; a report page opens when it stops.",
            Data = JobSummary.Describe(job, DateTimeOffset.Now),
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

/// <summary>revit_job_status (#100): progress, per-step results; waitSeconds long-polls until the job changes.</summary>
public sealed class JobStatusTool : IRevitMcpTool, IWaitingMcpTool
{
    public const int MaxWaitSeconds = 25;

    public string Name => "revit_job_status";
    public string Description =>
        "Shows a job's state (running, waitingApproval, paused, suspended, completed, failed, cancelled), stop reason, " +
        "progress, each step's state and message, new Revit warnings and the report url. waitSeconds (0–25) waits until " +
        "the job changes before answering, so polling costs one call per change. include=results adds the last steps' results.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Reports;

    public async Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var service = JobToolSupport.Service(request, out var failure);
        if (service == null) return failure!;
        var job = JobToolSupport.Find(service, request, out failure);
        if (job == null) return failure!;

        var wait = Math.Max(0, Math.Min(MaxWaitSeconds, ToolArguments.GetInt(request.Arguments, "waitSeconds", 0)));
        if (wait > 0 && !job.IsTerminal)
            await service.WaitForChangeAsync(job.Id, TimeSpan.FromSeconds(wait), cancellationToken).ConfigureAwait(false);

        var include = ToolArguments.GetString(request.Arguments, "include");
        var results = string.Equals(include, "results", StringComparison.OrdinalIgnoreCase)
            ? Math.Max(1, Math.Min(20, ToolArguments.GetInt(request.Arguments, "results", 3)))
            : 0;
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Job {job.Id} is {job.State.ToString().ToLowerInvariant()}: {job.DoneCount}/{job.Steps.Count} steps" +
                      (job.StopReason != null ? $" ({job.StopReason})" : string.Empty) + ".",
            Data = JobSummary.Describe(job, DateTimeOffset.Now, results)
        };
    }
}

/// <summary>revit_job_list (#100): jobs of this session and journaled earlier ones. Read-only: revit_job_remove deletes one.</summary>
public sealed class JobListTool : IRevitMcpTool, IBackgroundMcpTool
{
    public string Name => "revit_job_list";
    public string Description =>
        "Lists jobs: this session's and earlier ones from their journals (suspended by a Revit restart, finished), with " +
        "state, progress, document and report url. revit_job_remove deletes a finished job's journal.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Reports;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var service = JobToolSupport.Service(request, out var failure);
        if (service == null) return Task.FromResult(failure!);

        var now = DateTimeOffset.Now;
        var jobs = service.All();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{jobs.Count} job(s), {jobs.Count(j => !j.IsTerminal)} not finished.",
            Data = new
            {
                jobs = jobs.Select(j => new
                {
                    jobId = j.Id,
                    title = j.Title,
                    state = j.State.ToString().ToLowerInvariant(),
                    stopReason = j.StopReason,
                    document = j.DocumentTitle,
                    progress = $"{j.DoneCount}/{j.Steps.Count}",
                    createdAt = j.CreatedAt,
                    ageHours = Math.Round((now - j.CreatedAt).TotalHours, 1),
                    resumable = j.IsResumable,
                    reportUrl = j.ReportUrl
                }).ToList()
            }
        });
    }
}

/// <summary>
/// revit_job_remove (H1): deletes a finished job's journal, split off revit_job_list so the list stays read-only.
/// The model is not changed, so it needs no approval.
/// </summary>
public sealed class JobRemoveTool : IRevitMcpTool, IBackgroundMcpTool
{
    public string Name => "revit_job_remove";
    public string Description =>
        "Deletes a finished job's journal (jobId, from revit_job_list); not while it runs (cancel it first). The model is not changed.";
    public ToolPermission Permission => ToolPermission.DirectEdit;
    public ToolCategory Category => ToolCategory.Reports;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var service = JobToolSupport.Service(request, out var failure);
        if (service == null) return Task.FromResult(failure!);

        var id = ToolArguments.GetString(request.Arguments, "jobId").Trim();
        if (id.Length == 0) return Task.FromResult(JobToolSupport.Fail(request, "jobId is required."));
        if (!service.Remove(id))
            return Task.FromResult(JobToolSupport.Fail(request, $"Job {id} was not removed: it is unknown or still running (cancel it first).", JobToolSupport.NotFound));
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Removed job {id}. The model was not changed.",
            Data = new { removed = id }
        });
    }
}

/// <summary>revit_job_cancel (#100): stops a job after its running step. Nothing is undone.</summary>
public sealed class JobCancelTool : IRevitMcpTool, IBackgroundMcpTool
{
    public string Name => "revit_job_cancel";
    public string Description =>
        "Cancels a job: it stops after the running step (a step waiting for approval stops at once and its approval is " +
        "rejected). Nothing is undone — review with revit_checkpoint_diff and roll back with revit_checkpoint_rollback if needed. " +
        "The report page is published.";
    // Stops a job: not read-only, but it changes no model element and needs no approval.
    public ToolPermission Permission => ToolPermission.DirectEdit;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var service = JobToolSupport.Service(request, out var failure);
        if (service == null) return Task.FromResult(failure!);
        var job = JobToolSupport.Find(service, request, out failure);
        if (job == null) return Task.FromResult(failure!);

        var cancelled = service.Cancel(job.Id, "cancelled by the agent");
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = cancelled,
            Status = cancelled ? null : JobToolSupport.NotResumable,
            Message = cancelled
                ? $"Job {job.Id} is cancelled{(job.State is JobState.Running or JobState.WaitingApproval ? " after its running step" : string.Empty)}. Nothing was undone."
                : $"Job {job.Id} had already ended ({job.State.ToString().ToLowerInvariant()}).",
            Data = JobSummary.Describe(job, DateTimeOffset.Now)
        });
    }
}

/// <summary>revit_job_resume (#100): continues a paused, suspended or failed job from its first unfinished step.</summary>
public sealed class JobResumeTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_job_resume";
    public string Description =>
        "Resumes a paused, suspended (e.g. after a Revit restart) or failed job from its first unfinished step, with the " +
        "variables bound so far. Needs one approval, which also covers the new plan a suspended job gets. Warnings that " +
        "made the job pause are accepted and do not stop it again.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request)
    {
        var job = JobService.Current?.Get(ToolArguments.GetString(request.Arguments, "jobId").Trim());
        if (job == null) return null;
        var next = job.NextStep;
        return $"RESUME JOB: {job.Title} ({job.Id}, {job.State.ToString().ToLowerInvariant()}: {job.StopReason}) at step " +
               $"{(next?.Index ?? job.Steps.Count) + 1} of {job.Steps.Count}{(next != null ? $" ({next.DisplayTitle})" : string.Empty)}. " +
               "The remaining steps run without prompts inside the job's plan envelope; its warnings so far are accepted.";
    }

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var service = JobToolSupport.Service(request, out var failure);
        if (service == null) return Task.FromResult(failure!);
        var job = JobToolSupport.Find(service, request, out failure);
        if (job == null) return Task.FromResult(failure!);
        if (!job.IsResumable)
            return Task.FromResult(JobToolSupport.Fail(request, $"Job {job.Id} is {job.State.ToString().ToLowerInvariant()} and cannot be resumed.", JobToolSupport.NotResumable));

        var warnings = new List<string>();
        var plan = PlanService.Current?.Registry.Get(job.PlanId);
        var planUsable = plan != null && plan.IsActive && job.DocumentToken != null;
        if (!planUsable)
        {
            // Suspended, failed, or the plan ended: bind to the job's model again under a new plan and checkpoint.
            var target = RevitDocumentResolver.Resolve(uiapp, request, out var error);
            if (target == null) return Task.FromResult(JobToolSupport.Fail(request, error ?? "No open project document."));
            if (CheckpointChangeTracker.ModelIdOf(target.Document) != job.ModelId)
                return Task.FromResult(JobToolSupport.Fail(request,
                    $"Job {job.Id} belongs to '{job.DocumentTitle}', not '{target.Document.Title}'. Open it (and pass document=<its title>, or activate it) to resume."));
            var definition = service.PlanFor(job);
            if (plan != null && !plan.IsEnded) PlanService.Current!.Finish(plan, PlanState.Closed, "replaced on resume");
            if (JobToolSupport.ActivatePlan(uiapp, target, job, definition, request, cancellationToken, warnings, out failure) == null)
                return Task.FromResult(failure!);
            job.AcceptedNewErrors = job.AcceptedNewWarnings = job.NewErrors = job.NewWarnings = 0;   // new checkpoint, new baseline
        }

        service.Resume(job);
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Job {job.Id} resumed at step {(job.NextStep?.Index ?? job.Steps.Count) + 1} of {job.Steps.Count} under plan {job.PlanId}.",
            Data = JobSummary.Describe(job, DateTimeOffset.Now),
            Warnings = warnings
        });
    }
}

/// <summary>revit_job_report (#100): publishes (or re-publishes, same link) the job's handoff page.</summary>
public sealed class JobReportTool : IRevitMcpTool
{
    public string Name => JobService.ReportTool;
    public string Description =>
        "Publishes the handoff page of a job as a model page: steps, changed elements with before → now values (live), " +
        "added and deleted elements, new Revit warnings and how to undo. Re-publishing keeps the link; works for a running " +
        "job too (a snapshot). open=true opens it in the browser.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Reports;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var service = JobToolSupport.Service(request, out var failure);
        if (service == null) return Task.FromResult(failure!);
        var job = JobToolSupport.Find(service, request, out failure);
        if (job == null) return Task.FromResult(failure!);
        var pages = ModelPageService.Current;
        if (pages == null) return Task.FromResult(JobToolSupport.Fail(request, "Model pages are not available in this Revit session.", "tool_execution_failed"));

        var warnings = new List<string>();
        var doc = JobToolSupport.DocumentOf(uiapp, job);
        Checkpoint? checkpoint = null;
        CheckpointDiff? diff = null;
        WarningDiffResult? warningDiff = null;
        if (doc == null)
        {
            warnings.Add($"'{job.DocumentTitle}' is not open: the report lists the steps only.");
        }
        else if (job.CheckpointId != null && CheckpointChangeTracker.Store is { } store)
        {
            checkpoint = store.Find(job.ModelId, job.CheckpointId);
            if (checkpoint != null) (diff, warningDiff, _) = CheckpointSupport.ComputeDiff(doc, checkpoint);
        }
        var plan = PlanService.Current?.Registry.Get(job.PlanId);
        var html = JobReportHtml.Build(job, checkpoint, diff, warningDiff, plan, DateTimeOffset.Now);

        RevitMCP.Addin.ModelPages.ModelPageInfo info;
        try
        {
            info = pages.Store.Save(job.Id, "Töö: " + job.Title, html, job.ModelId, job.DocumentTitle,
                string.IsNullOrWhiteSpace(job.ClientName) ? null : job.ClientName, DateTimeOffset.Now);
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(JobToolSupport.Fail(request, ex.Message, "tool_execution_failed"));
        }
        var url = pages.UrlFor(info.Id, out var problem);
        if (url == null) return Task.FromResult(JobToolSupport.Fail(request, problem ?? "The page could not be served.", "tool_execution_failed"));
        job.ReportPageId = info.Id;
        job.ReportUrl = url;
        service.Changed(job);

        var opened = false;
        if (ToolArguments.GetBool(request.Arguments, "open", false))
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); opened = true; }
            catch (Exception ex) { warnings.Add("The browser could not be opened: " + ex.Message); }
        }

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Report of job {job.Id} published at {url}" + (opened ? " and opened." : "."),
            Data = new { jobId = job.Id, pageId = info.Id, url, bytes = info.Bytes, opened },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}
