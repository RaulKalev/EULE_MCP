using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Checkpoints;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Plans;
using RevitMCP.Addin.Tools.Checkpoints;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Plans;

internal static class PlanToolSupport
{
    public static McpToolResult Fail(McpToolRequest request, string message, string status = "validation_failed", object? data = null)
        => new() { RequestId = request.RequestId, Success = false, Status = status, Message = message, Data = data };

    public static PlanService? Service(McpToolRequest request, out McpToolResult? failure)
    {
        var service = PlanService.Current;
        failure = service == null ? Fail(request, "Plans are not available: the connector's plan service did not start.", "tool_execution_failed") : null;
        return service;
    }

    /// <summary>The plan named by planId; failure plan_not_found otherwise.</summary>
    public static Plan? Find(PlanService service, McpToolRequest request, out McpToolResult? failure)
    {
        var id = ToolArguments.GetString(request.Arguments, "planId").Trim();
        var plan = service.Registry.Get(id);
        failure = plan == null
            ? Fail(request, id.Length == 0 ? "planId is required." : $"Plan {id} is not known in this Revit session (plans do not survive a restart).", PlanService.NotFoundStatus)
            : null;
        return plan;
    }
}

/// <summary>revit_plan_submit (#99): one approval for a whole plan of write calls.</summary>
public sealed class PlanSubmitTool : IRevitMcpTool, IConditionalApprovalTool, IApprovalSummaryTool
{
    public string Name => "revit_plan_submit";
    public string Description =>
        "Submits a plan for several write steps: title, steps (intent lines shown to the user) and an envelope " +
        "(tools, elementIds, categories, levelIds, viewIds, worksets, allowCreate, allowModify, maxElementsPerCall, " +
        "maxElementsTotal, maxCalls, expiresMinutes, strict). The user approves it once; afterwards every write call that " +
        "carries the planId and stays inside the envelope runs without a prompt, one undo entry per call. A call outside " +
        "it waits for its own approval; one whose changes break it is rolled back. Destructive tools are never covered. " +
        "Activation creates a checkpoint for review and rollback.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    /// <summary>An invalid plan is refused at once instead of being parked for an approval it could never use.</summary>
    public bool NeedsApproval(McpToolRequest request)
        => PlanService.Current?.Parse(request).IsValid ?? false;

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request)
    {
        var service = PlanService.Current;
        if (service == null) return null;
        var def = service.Parse(request);
        var doc = RevitDocumentResolver.ResolveApprovalDocument(uiapp, request);
        return PlanSummary.Build(def, doc?.Title);
    }

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var service = PlanToolSupport.Service(request, out var failure);
        if (service == null) return Task.FromResult(failure!);

        var def = service.Parse(request);
        if (!def.IsValid)
            return Task.FromResult(PlanToolSupport.Fail(request, "The plan was not submitted: " + string.Join(" ", def.Errors), data: new { errors = def.Errors, warnings = def.Warnings }));

        var target = RevitDocumentResolver.Resolve(uiapp, request, out var error);
        if (target == null) return Task.FromResult(PlanToolSupport.Fail(request, error ?? "No open project document."));

        var warnings = new List<string>(def.Warnings);
        var plan = service.Activate(uiapp, target, def, request, cancellationToken, warnings, out failure);
        if (plan == null) return Task.FromResult(failure!);

        var result = new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Plan {plan.Id} '{plan.Title}' is active on '{plan.DocumentTitle}' until {plan.ExpiresAt:HH:mm}. Write calls that carry " +
                      $"planId={plan.Id} (the bridge adds it) and stay inside the envelope run without a prompt. " +
                      $"Review with revit_checkpoint_diff checkpointId={plan.CheckpointId}; end with revit_plan_close.",
            Data = PlanSummary.Describe(plan, DateTimeOffset.Now),
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
        return Task.FromResult(result);
    }
}

/// <summary>revit_plan_status (#99): state, counters, remaining budget and violations of plans.</summary>
public sealed class PlanStatusTool : IRevitMcpTool, IBackgroundMcpTool
{
    public string Name => "revit_plan_status";
    public string Description =>
        "Shows a plan's state (active, exhausted, expired, revoked, closed), counters, remaining calls and elements, " +
        "violations so far and its checkpoint id. Without planId: every plan of this Revit session.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Reports;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var service = PlanToolSupport.Service(request, out var failure);
        if (service == null) return Task.FromResult(failure!);
        service.ExpireDue();
        var now = DateTimeOffset.Now;

        if (string.IsNullOrWhiteSpace(ToolArguments.GetString(request.Arguments, "planId")))
        {
            var plans = service.Registry.All();
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"{plans.Count} plan(s) in this Revit session, {plans.Count(p => p.IsActive)} active.",
                Data = new { plans = plans.Select(p => PlanSummary.Describe(p, now)).ToList() }
            });
        }

        var plan = PlanToolSupport.Find(service, request, out failure);
        if (plan == null) return Task.FromResult(failure!);
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Plan {plan.Id} is {plan.State.ToString().ToLowerInvariant()}: {plan.Calls}/{plan.Envelope.MaxCalls} calls, " +
                      $"{plan.Touched.Count}/{plan.Envelope.MaxElementsTotal} elements, {plan.Violations.Count} violation(s).",
            Data = PlanSummary.Describe(plan, now)
        });
    }
}

/// <summary>revit_plan_close (#99): ends a plan early; returns its counters and the checkpoint diff summary.</summary>
public sealed class PlanCloseTool : IRevitMcpTool
{
    public string Name => "revit_plan_close";
    public string Description =>
        "Ends a plan: later calls with its planId need their own approval again. Returns the final counters and a " +
        "summary of what changed since the plan's checkpoint (revit_checkpoint_diff for details). Does not change the model.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Reports;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var service = PlanToolSupport.Service(request, out var failure);
        if (service == null) return Task.FromResult(failure!);
        var plan = PlanToolSupport.Find(service, request, out failure);
        if (plan == null) return Task.FromResult(failure!);

        var closed = service.Finish(plan, PlanState.Closed, "closed by the agent");
        var result = new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = closed
                ? $"Plan {plan.Id} closed after {plan.Calls} call(s) and {plan.Touched.Count} element(s)."
                : $"Plan {plan.Id} had already ended ({plan.State.ToString().ToLowerInvariant()}).",
            Data = new Dictionary<string, object?>
            {
                ["plan"] = PlanSummary.Describe(plan, DateTimeOffset.Now),
                ["changes"] = ChangesSince(uiapp, plan)
            }
        };
        return Task.FromResult(result);
    }

    /// <summary>The checkpoint diff summary of the plan's document, or null when it is not open or has no checkpoint.</summary>
    internal static object? ChangesSince(UIApplication uiapp, Plan plan)
    {
        var store = CheckpointChangeTracker.Store;
        if (store == null || plan.CheckpointId == null) return null;
        var doc = RevitDocumentResolver.GetProjectDocuments(uiapp)
            .FirstOrDefault(d => RevitMCP.Addin.Approval.ApprovalContextGuard.IsValid(true, plan.DocumentToken, d));
        if (doc == null) return null;
        var checkpoint = store.Find(CheckpointChangeTracker.ModelIdOf(doc), plan.CheckpointId);
        if (checkpoint == null) return null;
        var (diff, warnings, _) = CheckpointSupport.ComputeDiff(doc, checkpoint);
        var report = CheckpointDiffReport.Build(checkpoint, diff, warnings, new DiffReportOptions(), DateTimeOffset.Now);
        return new { checkpointId = checkpoint.Id, counts = report["counts"], warnings = report.TryGetValue("warnings", out var w) ? w : null };
    }
}

/// <summary>
/// Not advertised by the bridge: the Pending tab's "Show changes" button. Selects the elements a plan changed
/// (the user's own click in Revit, so no approval).
/// </summary>
public sealed class PlanShowChangesTool : IRevitMcpTool
{
    public const string ToolName = "revit_plan_show_changes";
    public string Name => ToolName;
    public string Description => "Selects the elements a plan changed in the active document (used by the MCP window).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Selection;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var service = PlanToolSupport.Service(request, out var failure);
        if (service == null) return Task.FromResult(failure!);
        var plan = PlanToolSupport.Find(service, request, out failure);
        if (plan == null) return Task.FromResult(failure!);

        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc == null || !RevitMCP.Addin.Approval.ApprovalContextGuard.IsValid(true, plan.DocumentToken, uidoc.Document))
            return Task.FromResult(PlanToolSupport.Fail(request, $"Activate '{plan.DocumentTitle}' to see the plan's changes."));

        var ids = plan.Touched.Select(id => new ElementId(id)).Where(id => uidoc.Document.GetElement(id) != null).ToList();
        uidoc.Selection.SetElementIds(ids);
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Selected {ids.Count} element(s) changed by plan {plan.Id}."
        });
    }
}
