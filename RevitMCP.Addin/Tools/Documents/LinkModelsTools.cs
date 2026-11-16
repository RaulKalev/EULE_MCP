using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.LinkOperations;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Documents;

/// <summary>revit_preview_link_models (#163): what linking IFC / RVT files would do, without changing anything.</summary>
public sealed class PreviewLinkModelsTool : IRevitMcpTool
{
    public string Name => "revit_preview_link_models";

    public string Description =>
        "Previews linking IFC (first choice) and RVT files into a document WITHOUT changing anything. files: [{path, kind?, " +
        "positioning? (origin|shared), pin?, ifExists? (skip|refresh), regenerateIfcCache?}] or paths [...]. Per file: link | refresh | " +
        "skip (already linked) | invalid (reason) | already_in_progress (the existing operation id), the IFC cache state " +
        "(.ifc.RVT fresh = no conversion), and the operations already open for the document. Run before revit_link_models.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Coordination;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(LinkModelsPlanning.Execute(uiapp, request, apply: false));
}

/// <summary>
/// revit_link_models (#163): admits IFC / RVT link requests as durable operations and returns their ids at once. Revit
/// converts and links them one at a time in the background of the conversation; status comes from
/// revit_link_operation_status, which does not need Revit.
/// </summary>
public sealed class LinkModelsTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_link_models";

    public string Description =>
        "Links IFC (first choice) and RVT files into a document as DURABLE operations. Requires approval. files: [{path, kind?, " +
        "positioning? (origin|shared), pin?, ifExists? (skip|refresh), regenerateIfcCache?}]. Returns an operation id per file at once; " +
        "never wait for the conversion here. A large IFC converts for minutes while Revit does not answer: follow it with " +
        "revit_link_operation_status (answers without Revit), never repeat this call for the same file (a repeat returns the existing " +
        "operation, status already_in_progress) and never retry after a timeout (a lost answer is outcome_unknown, not a failure). " +
        "Operations run one at a time. ifExists=refresh reloads an existing link through the same path as revit_reload_links_from. " +
        "Run revit_preview_link_models first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Coordination;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(LinkModelsPlanning.Execute(uiapp, request, apply: true));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request) => LinkModelsPlanning.Summary(uiapp, request);
}

/// <summary>
/// revit_link_operation_run (#163): the connector's internal carrier of an admitted operation. Not advertised by the
/// bridge; the link operation service dispatches it with the approval of revit_link_models. It refuses anything that is
/// not a queued operation handed to it (state starting and its one-time token), so calling it by hand does nothing.
/// </summary>
public sealed class LinkOperationRunTool : IRevitMcpTool
{
    public string Name => "revit_link_operation_run";

    public string Description =>
        "Internal: runs one admitted link operation on the Revit API thread (conversion, link, verification). Only the connector calls it.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Coordination;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(LinkModelsExecutor.Run(uiapp, request));
}

/// <summary>revit_reconcile_link_operation (#163): settles an outcome_unknown operation from the live document.</summary>
public sealed class ReconcileLinkOperationTool : IRevitMcpTool
{
    public string Name => "revit_reconcile_link_operation";

    public string Description =>
        "Settles a link operation whose outcome is unknown (Revit stopped answering or restarted while it ran): looks in the open " +
        "document for the link (type exists, points at the file, loaded, instance present, content) and records completed or failed. " +
        "Nothing is linked, retried or deleted (cache files are kept). If the document is not open or the result is unclear it stays " +
        "outcome_unknown for a person to review. operationId (from revit_list_link_operations).";

    // Changes the connector's record, not the model.
    public ToolPermission Permission => ToolPermission.DirectEdit;
    public ToolCategory Category => ToolCategory.Coordination;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(LinkModelsExecutor.Reconcile(uiapp, request));
}

internal static class LinkModelsPlanning
{
    public static string Summary(UIApplication uiapp, McpToolRequest request)
    {
        var errors = new List<string>();
        var requests = LinkModelsPlanner.Parse(request.Arguments, errors);
        var target = ToolDocument.Resolve(uiapp, request, out _);
        var plan = target == null ? null : LinkModelsPlanner.Plan(requests, LinkModelsExecutor.BuildContext(target.Document, LinkOperationService.Current));
        var runs = plan?.Where(p => p.WillRun).ToList();
        var count = runs?.Count ?? requests.Count;
        var names = (runs ?? new List<LinkPlanItem>()).Select(p => System.IO.Path.GetFileName(p.Request.Path)).Take(5).ToList();
        var ifc = runs?.Count(p => p.Kind == LinkKinds.Ifc) ?? requests.Count;
        var skipped = plan?.Count(p => p.Action is LinkPlanActions.Skip or LinkPlanActions.InProgress) ?? 0;
        var first = requests.FirstOrDefault() ?? new LinkModelRequest();
        return $"Link {count} model file(s) ({string.Join(", ", names)}{(runs != null && runs.Count > 5 ? ", …" : string.Empty)})" +
               $"{(target != null ? $" into '{target.Document.Title}'" : string.Empty)}: placement {first.Positioning}, one at a time in the background" +
               $"{(ifc > 0 ? "; an IFC takes minutes to convert and Revit does not answer meanwhile" : string.Empty)}" +
               $"{(skipped > 0 ? $"; {skipped} already linked or running (skipped)" : string.Empty)}.";
    }

    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply)
    {
        var sw = Stopwatch.StartNew();
        var target = DocumentToolSupport.ResolveTarget(uiapp, request, out var failure);
        if (target == null) return failure!;
        var doc = target.Document;
        if (doc.IsFamilyDocument)
            return DocumentToolSupport.Fail(request, "The target is a family document; link models into a project.");

        var errors = new List<string>();
        var requests = LinkModelsPlanner.Parse(request.Arguments, errors);
        if (errors.Count > 0) return DocumentToolSupport.Fail(request, string.Join(" ", errors), "invalid_arguments");

        var service = LinkOperationService.Current;
        if (apply && service == null)
            return DocumentToolSupport.Fail(request, "The link operation service is not available in this Revit session.", "not_available");

        var plan = LinkModelsPlanner.Plan(requests, LinkModelsExecutor.BuildContext(doc, service));
        var now = DateTimeOffset.Now;
        var open = (service?.List() ?? new List<LinkOperation>()).Where(o => o.IsOpen).ToList();

        if (!apply)
        {
            sw.Stop();
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"Preview: {plan.Count(p => p.Action == LinkPlanActions.Link)} to link, {plan.Count(p => p.Action == LinkPlanActions.Refresh)} to refresh, " +
                          $"{plan.Count(p => p.Action == LinkPlanActions.Skip)} already linked, {plan.Count(p => p.Action is LinkPlanActions.InProgress or LinkPlanActions.NeedsReview)} " +
                          $"already in progress, {plan.Count(p => p.Action == LinkPlanActions.Invalid)} invalid.",
                Data = new
                {
                    documentTitle = doc.Title,
                    files = plan.Select(p => Describe(p, null, now)).ToList(),
                    openOperations = open.Select(o => LinkOperationStatusView.Row(o, now, new LinkOperationLiveness { EffectiveState = o.State }, null)).ToList(),
                    rules = new[]
                    {
                        "Files run one at a time, in order; never in parallel.",
                        "A request for a file that has an open operation returns that operation; it never starts a second conversion.",
                        "A timeout or lost answer is outcome_unknown, never an automatic retry.",
                        "Status: revit_link_operation_status (answers without Revit)."
                    }
                },
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        var admissions = new Dictionary<LinkPlanItem, LinkAdmission>();
        var withdraw = new List<string>();
        foreach (var item in plan.Where(p => p.WillRun))
        {
            var admission = service!.Admit(new LinkAdmitInput
            {
                Type = item.Action == LinkPlanActions.Refresh ? LinkOperationTypes.Refresh : LinkOperationTypes.Link,
                Kind = item.Kind,
                SourcePath = item.Request.Path,
                TargetSelector = LinkModelsExecutor.TargetSelectorOf(doc),
                TargetTitle = doc.Title,
                TargetKey = LinkModelsExecutor.TargetKeyOf(doc),
                Positioning = item.Request.Positioning,
                Pin = item.Request.Pin,
                RegenerateCache = item.Request.RegenerateCache,
                ExistingLinkTypeId = item.ExistingLink?.TypeId,
                RequestedBy = request.ClientName,
                RequestId = request.RequestId
            });
            admissions[item] = admission;
        }

        if (admissions.Values.Any(a => a.Accepted)) service!.Kick();

        sw.Stop();
        var accepted = admissions.Values.Count(a => a.Accepted);
        var inProgress = admissions.Values.Count(a => a.Status is "already_in_progress" or "needs_review") +
                         plan.Count(p => p.Action is LinkPlanActions.InProgress or LinkPlanActions.NeedsReview);
        var skipped = plan.Count(p => p.Action == LinkPlanActions.Skip);
        var invalid = plan.Count(p => p.Action == LinkPlanActions.Invalid || p.Action == LinkPlanActions.QueueFull) +
                      admissions.Values.Count(a => a.Status == "queue_full");
        var nothingStarted = accepted == 0;
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = accepted > 0 || inProgress > 0 || skipped > 0,
            Status = accepted == 0 && inProgress > 0 && skipped == 0 && invalid == 0 ? "already_in_progress"
                : accepted == 0 && skipped == 0 && inProgress == 0 ? "validation_failed" : null,
            Message = $"{accepted} link operation(s) accepted, {inProgress} already in progress, {skipped} already linked, {invalid} refused. " +
                      (accepted > 0
                          ? "Revit converts them one at a time and may not answer other calls meanwhile. Follow each with revit_link_operation_status operationId=…; do not repeat this request."
                          : nothingStarted ? "Nothing was started." : string.Empty),
            Data = new
            {
                documentTitle = doc.Title,
                accepted,
                alreadyInProgress = inProgress,
                skipped,
                refused = invalid,
                operations = plan.Select(p => Describe(p, admissions.TryGetValue(p, out var a) ? a : null, now)).ToList(),
                statusTool = "revit_link_operation_status",
                note = "Status is read from the connector's files; it works while Revit converts. A timeout or disconnect never means failure and never justifies a second request."
            },
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static object Describe(LinkPlanItem item, LinkAdmission? admission, DateTimeOffset now)
    {
        var op = admission?.Operation ?? item.Operation;
        var status = admission?.Status ?? item.Action;
        return new
        {
            path = item.Request.Path,
            kind = item.Kind.Length == 0 ? null : item.Kind,
            status,
            action = item.Action,
            operationId = op?.Id,
            state = op?.State,
            positioning = item.Request.Positioning,
            pin = item.Request.Pin,
            existingLink = item.ExistingLink == null ? null : new { typeId = item.ExistingLink.TypeId, name = item.ExistingLink.Name, path = item.ExistingLink.Path, status = item.ExistingLink.Status, instances = item.ExistingLink.InstanceCount },
            cache = item.Cache == null ? null : new { path = item.Cache.Path, exists = item.Cache.Exists, bytes = item.Cache.Bytes, fresh = item.Cache.Fresh },
            reason = admission != null && !admission.Accepted ? admission.Message : item.Reason,
            notes = item.Notes
        };
    }
}
