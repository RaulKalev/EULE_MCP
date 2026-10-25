using System.Collections.Concurrent;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Approval;
using RevitMCP.Addin.Checkpoints;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Logging;
using RevitMCP.Addin.Tools;
using RevitMCP.Addin.Tools.Checkpoints;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Plans;

/// <summary>
/// Plan-level approval (#99): activates approved plans and runs write calls under them. A call inside a plan's
/// envelope runs without a prompt inside a TransactionGroup; when what Revit changed breaks the envelope the
/// group is rolled back and the call fails with plan_envelope_violated. A call the pre-check refuses falls
/// back to the normal per-call approval with the reason in its warnings — never blocked harder than without a
/// plan. Wired in App; null (and every plan path inert) until then.
/// </summary>
public sealed class PlanService
{
    public const string ViolatedStatus = "plan_envelope_violated";
    public const string NotFoundStatus = "plan_not_found";
    public const string NotActiveStatus = "plan_not_active";
    private const int MaxCheckRecords = 500;

    private readonly Func<string, ToolPermission?> _permissionOf;
    private readonly Func<string, bool> _isBackground;
    private readonly ActivityLogger? _logger;
    private readonly Func<RevitDocumentContext?> _context;
    private readonly ConcurrentDictionary<string, (string PlanId, string Check)> _checks = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _checkOrder = new();

    public PlanService(Func<string, ToolPermission?> permissionOf, Func<string, bool> isBackground, ActivityLogger? logger, Func<RevitDocumentContext?> context)
    {
        _permissionOf = permissionOf;
        _isBackground = isBackground;
        _logger = logger;
        _context = context;
    }

    public static PlanService? Current { get; set; }

    public PlanRegistry Registry { get; } = new();

    public PlanDefinition Parse(McpToolRequest request)
        => PlanEnvelopeParser.Parse(request.Arguments, _permissionOf, _isBackground);

    /// <summary>The plan id and check outcome recorded for a request ("authorized", "fallback:…", "violated"), for the activity log.</summary>
    public (string PlanId, string Check)? CheckOf(string requestId)
        => _checks.TryGetValue(requestId, out var c) ? c : null;

    /// <summary>
    /// Activates an approved plan on <paramref name="target"/>: creates its checkpoint (scope = the envelope's ids
    /// and categories) and registers it. Null with <paramref name="failure"/> when the plan cannot start.
    /// </summary>
    public Plan? Activate(
        UIApplication uiapp,
        RevitDocumentResolver.DocumentTarget target,
        PlanDefinition definition,
        McpToolRequest request,
        CancellationToken cancellationToken,
        List<string> warnings,
        out McpToolResult? failure)
    {
        if (Registry.Contains(definition.Id))
        {
            failure = CheckpointSupport.Fail(request, $"Plan id {definition.Id} is already used in this session; submit without planId to get a new one.");
            return null;
        }

        var doc = target.Document;
        Checkpoint? checkpoint = null;
        var store = CheckpointChangeTracker.Store;
        if (store != null)
        {
            var scope = new SnapshotScope
            {
                ElementIds = definition.Envelope.ElementIds.OrderBy(i => i).ToList(),
                Categories = definition.Envelope.Categories.ToList()
            };
            var client = string.IsNullOrWhiteSpace(request.ClientName) ? "agent" : request.ClientName.Trim();
            checkpoint = CheckpointSupport.Create(uiapp, target, store, scope, "Plan: " + definition.Title, client, request, cancellationToken,
                out _, out _, out var checkpointFailure);
            if (checkpoint == null && checkpointFailure?.Status == CheckpointSupport.SnapshotTooLarge)
            {
                warnings.Add("The envelope is too large to snapshot; the plan's checkpoint tracks change ids and warnings only (no before/now values, no value rollback).");
                checkpoint = CheckpointSupport.Create(uiapp, target, store, new SnapshotScope(), "Plan: " + definition.Title, client, request, cancellationToken,
                    out _, out _, out checkpointFailure);
            }
            if (checkpoint == null)
            {
                failure = checkpointFailure;
                return null;
            }
        }
        else
        {
            warnings.Add("The checkpoint store is not available: the plan runs without a checkpoint (no diff or rollback).");
        }

        var now = DateTimeOffset.Now;
        var plan = new Plan
        {
            Id = definition.Id,
            Title = definition.Title,
            Steps = definition.Steps,
            Envelope = definition.Envelope,
            ClientName = request.ClientName,
            DocumentToken = doc,
            DocumentTitle = doc.Title,
            ModelId = CheckpointChangeTracker.ModelIdOf(doc),
            CheckpointId = checkpoint?.Id,
            State = PlanState.Active,
            CreatedAt = now,
            ActivatedAt = now,
            ExpiresAt = now.AddMinutes(definition.Envelope.ExpiresMinutes)
        };
        if (checkpoint != null)
        {
            checkpoint.PlanId = plan.Id;
            store!.MarkChanged(checkpoint);
        }
        Registry.Add(plan);
        failure = null;
        return plan;
    }

    /// <summary>
    /// For a RequiresApproval call that carries a planId: runs it under the plan and returns its result, or returns
    /// null with <paramref name="fallbackWarning"/> set when it must take the normal approval path. Null without a
    /// warning when the call has no plan. API thread only.
    /// </summary>
    public McpToolResult? TryRunUnderPlan(
        UIApplication app,
        IRevitMcpTool tool,
        McpToolRequest request,
        Func<McpToolResult> run,
        out string? fallbackWarning)
    {
        fallbackWarning = null;
        var planId = ToolArguments.GetString(request.Arguments, "planId").Trim();
        if (planId.Length == 0 || tool.Name.StartsWith("revit_plan_", StringComparison.Ordinal)) return null;
        if (tool is IBackgroundMcpTool || tool.Permission != ToolPermission.RequiresApproval) return null;

        var plan = Registry.Get(planId);
        if (plan == null)
            return Fallback(request, null, planId, tool.Name, $"{NotFoundStatus}: plan {planId} is not known in this Revit session", out fallbackWarning);

        var unusable = Registry.CheckUsable(plan, DateTimeOffset.Now);
        if (unusable != null)
            return Fallback(request, plan, planId, tool.Name, $"{NotActiveStatus}: {unusable}", out fallbackWarning);

        var targetDoc = RevitDocumentResolver.ResolveApprovalDocument(app, request);
        if (targetDoc == null || !ApprovalContextGuard.IsValid(true, plan.DocumentToken, targetDoc))
            return Fallback(request, plan, planId, tool.Name,
                $"{NotActiveStatus}: plan {plan.Id} was approved for '{plan.DocumentTitle}', this call targets '{targetDoc?.Title ?? "no document"}'", out fallbackWarning);

        var targets = PlanArgumentScope.Extract(request.Arguments);
        var refusal = PlanArgumentScope.Check(plan, tool.Name, targets, () =>
            RevitDocumentResolver.IsActive(app, targetDoc)
                ? app.ActiveUIDocument!.Selection.GetElementIds().Select(id => id.Value).ToList()
                : (IReadOnlyCollection<long>)Array.Empty<long>(),
            () => RevitDocumentResolver.IsActive(app, targetDoc) ? app.ActiveUIDocument!.ActiveView?.Id.Value : null);
        if (refusal != null)
            return Fallback(request, plan, planId, tool.Name, refusal, out fallbackWarning);

        return RunInGroup(targetDoc, plan, tool, request, run, out fallbackWarning);
    }

    private McpToolResult? RunInGroup(Document doc, Plan plan, IRevitMcpTool tool, McpToolRequest request, Func<McpToolResult> run, out string? fallbackWarning)
    {
        fallbackWarning = null;
        var store = CheckpointChangeTracker.Store;
        var modelId = CheckpointChangeTracker.ModelIdOf(doc);
        var mark = store?.MarkBatches(modelId);

        using var group = new TransactionGroup(doc, Shorten($"Plan {plan.Title}: {tool.Name}"));
        TransactionStatus started;
        try { started = group.Start(); }
        catch (Exception ex) { return Fallback(request, plan, plan.Id, tool.Name, "could not start the plan's transaction group: " + ex.Message, out fallbackWarning); }
        if (started != TransactionStatus.Started)
            return Fallback(request, plan, plan.Id, tool.Name, $"could not start the plan's transaction group ({started})", out fallbackWarning);

        // From here the call runs without a prompt: it is the plan's approval that covers it.
        request.IsApproved = true;
        Record(request.RequestId, plan.Id, "authorized");

        McpToolResult result;
        CallChanges changes;
        var recording = PlanChangeRecorder.Begin(doc);
        try
        {
            result = run();
        }
        catch (Exception ex)
        {
            PlanChangeRecorder.End();
            SafeRollBack(group);
            if (mark != null) store!.ResetBatches(modelId, mark);
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = ex is OperationCanceledException ? "request_cancelled" : "transaction_failed",
                Message = $"Tool execution failed under plan {plan.Id}; its changes were rolled back: {ex.Message}",
                Errors = new List<string> { ex.ToString() }
            };
        }
        finally
        {
            changes = recording;
            PlanChangeRecorder.End();
        }

        var check = PlanPostCheck.Check(plan, changes, id => FactsOf(doc, id));
        if (check.Violation != null)
        {
            SafeRollBack(group);
            if (mark != null) store!.ResetBatches(modelId, mark);
            Record(request.RequestId, plan.Id, "violated");
            Registry.RecordViolation(plan, new PlanViolation
            {
                At = DateTimeOffset.Now,
                Tool = tool.Name,
                Reason = check.Violation,
                Ids = check.OffendingIds,
                Kind = "violated"
            });
            LogEvent(plan, "revit_plan_violation", false, $"{tool.Name} broke plan {plan.Id} and was rolled back: {check.Violation}.");
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = ViolatedStatus,
                Message = $"{tool.Name} went outside plan {plan.Id} and was rolled back, the model is unchanged: {check.Violation}. " +
                          "Narrow the call, or submit a new plan that covers it.",
                Data = new
                {
                    planId = plan.Id,
                    tool = tool.Name,
                    reason = check.Violation,
                    offendingIds = check.OffendingIds,
                    categories = check.OffendingIds.Take(50).Select(id => FactsOf(doc, id)?.Category).Where(c => c != null).Distinct().ToList(),
                    limits = new
                    {
                        plan.Envelope.MaxElementsPerCall,
                        plan.Envelope.MaxElementsTotal,
                        touched = plan.Touched.Count,
                        plan.Envelope.AllowCreate,
                        plan.Envelope.AllowModify
                    },
                    toolMessage = result.Message
                },
                DurationMs = result.DurationMs
            };
        }

        try { group.Assimilate(); }
        catch (Exception ex)
        {
            SafeRollBack(group);
            if (mark != null) store!.ResetBatches(modelId, mark);
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = "transaction_failed",
                Message = $"The plan's transaction group could not be committed; the call was rolled back: {ex.Message}"
            };
        }

        var wasActive = plan.State == PlanState.Active;
        Registry.RecordCall(plan, check);
        if (wasActive && plan.State == PlanState.Exhausted)
        {
            result.Warnings.Add($"Plan {plan.Id} exhausted ({plan.StateReason}); later calls wait for approval again.");
            LogEvent(plan, "revit_plan_exhausted", true, $"Plan {plan.Id} exhausted: {plan.StateReason}.");
        }
        else if (plan.Calls * 5 >= plan.Envelope.MaxCalls * 4 || plan.Touched.Count * 5 >= plan.Envelope.MaxElementsTotal * 4)
        {
            result.Warnings.Add($"Plan {plan.Id}: {plan.Calls}/{plan.Envelope.MaxCalls} calls and {plan.Touched.Count}/{plan.Envelope.MaxElementsTotal} elements used.");
        }
        return result;
    }

    /// <summary>Ends a plan for the agent (Closed) or the user (Revoked) and logs it. False when it had ended already.</summary>
    public bool Finish(Plan plan, PlanState state, string reason)
    {
        if (!Registry.Finish(plan, state, reason, DateTimeOffset.Now)) return false;
        LogEvent(plan, state == PlanState.Closed ? "revit_plan_close" : "revit_plan_revoke", true,
            $"Plan {plan.Id} '{plan.Title}' {state.ToString().ToLowerInvariant()}: {reason}. {plan.Calls} call(s), {plan.Touched.Count} element(s).");
        return true;
    }

    /// <summary>Panic stop: revokes every plan.</summary>
    public void RevokeAll(string reason)
    {
        foreach (var plan in Registry.RevokeAll(reason, DateTimeOffset.Now))
            LogEvent(plan, "revit_plan_revoke", true, $"Plan {plan.Id} '{plan.Title}' revoked: {reason}.");
    }

    /// <summary>Expires plans whose time ran out; the window's timer calls this.</summary>
    public void ExpireDue()
    {
        foreach (var plan in Registry.ExpireDue(DateTimeOffset.Now))
            LogEvent(plan, "revit_plan_expire", true, $"Plan {plan.Id} '{plan.Title}' expired.");
    }

    private McpToolResult? Fallback(McpToolRequest request, Plan? plan, string planId, string tool, string reason, out string? warning)
    {
        // Without Direct Edit the call is now parked for approval; with it, it runs like a call without a plan.
        warning = $"Plan {planId}: {reason}; the plan does not cover this call, so it is handled like a call without a plan (approval unless Direct Edit is on).";
        Record(request.RequestId, planId, "fallback:" + reason);
        if (plan != null)
            Registry.RecordViolation(plan, new PlanViolation { At = DateTimeOffset.Now, Tool = tool, Reason = reason, Kind = "fallback" });
        return null;
    }

    private void Record(string requestId, string planId, string check)
    {
        _checks[requestId] = (planId, check);
        _checkOrder.Enqueue(requestId);
        while (_checkOrder.Count > MaxCheckRecords && _checkOrder.TryDequeue(out var old))
            _checks.TryRemove(old, out _);
    }

    private void LogEvent(Plan plan, string tool, bool success, string message)
    {
        if (_logger == null) return;
        var request = new McpToolRequest
        {
            ToolName = tool,
            ClientName = plan.ClientName,
            Arguments = new Dictionary<string, object?> { ["planId"] = plan.Id }
        };
        Record(request.RequestId, plan.Id, tool.Substring("revit_plan_".Length));
        _ = _logger.WriteAsync(request, new McpToolResult { RequestId = request.RequestId, Success = success, Message = message }, _context());
    }

    private static ElementFacts? FactsOf(Document doc, long id)
    {
        try
        {
            var element = doc.GetElement(new ElementId(id));
            if (element == null) return null;
            var facts = new ElementFacts();
            var category = element.Category;
            if (category != null)
            {
                facts.Category = category.Name;
                var bic = category.BuiltInCategory;
                facts.BuiltInCategory = bic == BuiltInCategory.INVALID ? null : bic.ToString();
            }
            if (element.LevelId is { } level && level != ElementId.InvalidElementId) facts.LevelId = level.Value;
            if (element.OwnerViewId is { } view && view != ElementId.InvalidElementId) facts.OwnerViewId = view.Value;
            if (doc.IsWorkshared)
            {
                try { facts.Workset = doc.GetWorksetTable().GetWorkset(element.WorksetId)?.Name; } catch { }
            }
            return facts;
        }
        catch
        {
            return null;
        }
    }

    private static void SafeRollBack(TransactionGroup group)
    {
        try
        {
            if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
        }
        catch { }
    }

    private static string Shorten(string name) => name.Length <= 120 ? name : name.Substring(0, 120);
}
