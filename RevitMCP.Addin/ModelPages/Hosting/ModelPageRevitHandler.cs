using System.Collections.Concurrent;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Approval;
using RevitMCP.Addin.Logging;
using RevitMCP.Addin.Services;
using RevitMCP.Addin.Tools;
using RevitMCP.Addin.Tools.ModelPages;
using RevitMCP.Core.Models;
using RevitMCP.Village;

namespace RevitMCP.Addin.ModelPages.Hosting;

/// <summary>
/// The Revit side of a model page. Selecting, zooming and reading values run on its own
/// <see cref="ExternalEvent"/>: they never enter the MCP dispatcher, the approval queue or the activity
/// log, cost no tokens and cannot change the model. Writing a value is different on purpose: it is handed
/// to the connector's dispatcher as <see cref="PageSetValuesTool"/>, so it needs the user's approval in
/// Revit (unless Direct Edit is on), runs in one transaction and is logged, like an agent's write.
/// Everything refuses when the active document is not the page's model.
/// </summary>
public sealed class ModelPageRevitHandler : IExternalEventHandler, IModelPageBackend
{
    private readonly ConcurrentQueue<Work> _queue = new();
    private ExternalEvent? _event;
    private ExternalEventService? _dispatcher;
    private ApprovalService? _approvals;
    private ActivityLogger? _logger;

    /// <summary>pages.allowEdits in the user config; asked on every request so a change applies at once.</summary>
    public Func<bool> EditsEnabled { get; set; } = () => true;

    /// <summary>Connects the write path. Without it pages stay read-only.</summary>
    public void SetWritePath(ExternalEventService dispatcher, ApprovalService approvals, ActivityLogger? logger)
    {
        _dispatcher = dispatcher;
        _approvals = approvals;
        _logger = logger;
    }

    private sealed class Work
    {
        public Func<UIApplication, object> Run = _ => new object();
        public TaskCompletionSource<object> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Must be called in a valid Revit API context (App.OnStartup).</summary>
    public static ModelPageRevitHandler Create()
    {
        var handler = new ModelPageRevitHandler();
        handler._event = ExternalEvent.Create(handler);
        return handler;
    }

    public string GetName() => "RevitMCP.ModelPages";

    public Task<object> StatusAsync(ModelPageInfo page, CancellationToken ct) =>
        RunAsync(app => Status(app, page), ct);

    public Task<object> SelectAsync(ModelPageInfo page, IReadOnlyList<long> ids, bool zoom, CancellationToken ct) =>
        RunAsync(app => Select(app, page, ids, zoom), ct);

    public Task<object> ValuesAsync(ModelPageInfo page, IReadOnlyList<long> ids, IReadOnlyList<string> parameters, CancellationToken ct) =>
        RunAsync(app => Values(app, page, ids, parameters), ct);

    public async Task<object> SetAsync(ModelPageInfo page, IReadOnlyList<ModelPageChange> changes, CancellationToken ct)
    {
        var dispatcher = _dispatcher;
        if (dispatcher == null || !EditsEnabled())
            return ModelPageServer.Fail("read_only", "Editing from pages is switched off.");

        var list = new JArray();
        foreach (var c in changes)
            list.Add(new JObject { ["id"] = c.Id, ["parameter"] = c.Parameter, ["value"] = c.Value, ["expected"] = c.Expected });
        var request = new McpToolRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            ToolName = PageSetValuesTool.ToolName,
            ClientName = "Model page",
            Arguments = new Dictionary<string, object?>
            {
                ["pageId"] = page.Id, ["pageTitle"] = page.Title, ["modelId"] = page.ModelId, ["changes"] = list
            }
        };

        var result = await dispatcher.DispatchAsync(request, cancellationToken: ct).ConfigureAwait(false);
        try { if (_logger != null) await _logger.WriteAsync(request, result, dispatcher.GetLastContext()).ConfigureAwait(false); }
        catch { /* logging never fails a write */ }

        if (result.Status == "approval_required")
        {
            var waiting = ModelPageServer.Fail("approval_required", "Approve the change in Revit: RevitMCP window, Pending tab.");
            waiting["requestId"] = request.RequestId;
            return waiting;
        }
        return Written(result);
    }

    /// <summary>
    /// A shared-memory review action from the memory page. Handed to the dispatcher as
    /// <see cref="Tools.Memory.MemoryReviewTool"/>: approval in Revit (unless Direct Edit is on) and the activity log.
    /// </summary>
    public async Task<object> MemoryAsync(ModelPageInfo page, ModelPageMemoryAction action, CancellationToken ct)
    {
        // Only the review page the connector itself generated may change memory, not a page an agent wrote.
        if (!RevitMCP.Addin.Memory.MemoryPageHtml.IsMemoryPage(page.Id))
            return ModelPageServer.Fail("forbidden", "Memory can only be changed from the memory review page (revit_memory_page).");
        var dispatcher = _dispatcher;
        if (dispatcher == null || !EditsEnabled())
            return ModelPageServer.Fail("read_only", "Changes from pages are switched off.");

        var request = new McpToolRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            ToolName = Tools.Memory.MemoryReviewTool.ToolName,
            ClientName = "Model page",
            Arguments = new Dictionary<string, object?>
            {
                ["pageId"] = page.Id,
                ["modelId"] = page.ModelId,
                ["action"] = action.Action,
                ["ids"] = new JArray(action.Ids),
                ["proposal"] = action.Proposal ?? string.Empty
            }
        };

        var result = await dispatcher.DispatchAsync(request, cancellationToken: ct).ConfigureAwait(false);
        try { if (_logger != null) await _logger.WriteAsync(request, result, dispatcher.GetLastContext()).ConfigureAwait(false); }
        catch { /* logging never fails a write */ }

        if (result.Status == "approval_required")
        {
            var waiting = ModelPageServer.Fail("approval_required", "Approve the change in Revit: RevitMCP window, Pending tab.");
            waiting["requestId"] = request.RequestId;
            return waiting;
        }
        return Written(result);
    }

    public Task<object> ApprovalAsync(ModelPageInfo page, string requestId, CancellationToken ct)
    {
        var outcome = _approvals?.GetOutcome(requestId);
        // Only this feature's own requests: a page must not read what an agent's approval returned.
        if (outcome == null || (outcome.ToolName != PageSetValuesTool.ToolName && outcome.ToolName != Tools.Memory.MemoryReviewTool.ToolName))
            return Task.FromResult<object>(ModelPageServer.Fail("unknown_request", "This request is no longer known. Refresh the page to see the current values."));

        object answer = outcome.State switch
        {
            ApprovalOutcome.Pending => ModelPageServer.Fail("pending", "Waiting for approval in Revit."),
            ApprovalOutcome.Running => ModelPageServer.Fail("pending", "Approved; Revit is writing the values."),
            ApprovalOutcome.Rejected => ModelPageServer.Fail("rejected", "The change was rejected in Revit."),
            _ => outcome.Result == null ? ModelPageServer.Fail("failed", "The change did not complete.") : Written(outcome.Result)
        };
        return Task.FromResult(answer);
    }

    /// <summary>A finished write as the page reads it: per value whether it was written, and the value now in the model.</summary>
    private static object Written(McpToolResult result)
    {
        JToken? rows = null;
        try { rows = result.Data == null ? null : (result.Data as JToken ?? JObject.FromObject(result.Data))["changes"]; }
        catch { /* a refusal carries no rows */ }

        var answer = result.Success
            ? new Dictionary<string, object?> { ["ok"] = true, ["status"] = "written", ["message"] = result.Message }
            : ModelPageServer.Fail(string.IsNullOrEmpty(result.Status) ? "failed" : result.Status!, result.Message);
        if (rows != null) answer["changes"] = rows;
        return answer;
    }

    /// <summary>The identity a page is bound to; the same value the village uses for a model.</summary>
    public static string ModelIdOf(UIApplication app)
    {
        var info = RevitContextService.Read(app);
        return VillageModelId.Compute(info.CentralModelPath, info.LocalModelPath, info.DocumentTitle);
    }

    private async Task<object> RunAsync(Func<UIApplication, object> run, CancellationToken ct)
    {
        var ev = _event;
        if (ev == null) return ModelPageServer.Fail("unavailable", "Revit is not available.");

        var work = new Work { Run = run };
        _queue.Enqueue(work);
        using var registration = ct.Register(() => work.Completion.TrySetCanceled());
        // Denied and Pending both mean an Execute is already on its way, which drains the queue.
        if (ev.Raise() == ExternalEventRequest.TimedOut)
            work.Completion.TrySetResult(ModelPageServer.Fail("busy", "Revit is busy. Try again in a moment."));
        return await work.Completion.Task.ConfigureAwait(false);
    }

    public void Execute(UIApplication app)
    {
        while (_queue.TryDequeue(out var work))
        {
            if (work.Completion.Task.IsCompleted) continue;   // the page stopped waiting
            try { work.Completion.TrySetResult(work.Run(app)); }
            catch (Exception ex) { work.Completion.TrySetResult(ModelPageServer.Fail("error", "The request failed in Revit: " + ex.Message)); }
        }
    }

    /// <summary>The active document when it is the page's model; otherwise the refusal to send back.</summary>
    private static UIDocument? PageDocument(UIApplication app, ModelPageInfo page, out object? refusal)
    {
        refusal = null;
        var uidoc = app.ActiveUIDocument;
        if (uidoc?.Document == null)
        {
            refusal = ModelPageServer.Fail("no_document", "No document is open in Revit.");
            return null;
        }
        // Element ids are per document: acting on them in another model would hit unrelated elements.
        if (!string.Equals(ModelIdOf(app), page.ModelId, StringComparison.Ordinal))
        {
            var fail = ModelPageServer.Fail("different_model",
                $"This page was made from '{page.ProjectName}'. Switch to that model in Revit (the active document is '{uidoc.Document.Title}').");
            fail["activeDocument"] = uidoc.Document.Title;
            refusal = fail;
            return null;
        }
        return uidoc;
    }

    private static object Status(UIApplication app, ModelPageInfo page)
    {
        var uidoc = PageDocument(app, page, out var refusal);
        if (uidoc == null) return refusal!;
        return new Dictionary<string, object?>
        {
            ["ok"] = true, ["status"] = "ready", ["activeDocument"] = uidoc.Document.Title, ["activeView"] = uidoc.ActiveView?.Name
        };
    }

    private static object Select(UIApplication app, ModelPageInfo page, IReadOnlyList<long> ids, bool zoom)
    {
        var uidoc = PageDocument(app, page, out var refusal);
        if (uidoc == null) return refusal!;
        var doc = uidoc.Document;

        var valid = new List<ElementId>();
        var missing = new List<long>();
        foreach (var id in ids)
        {
            var eid = new ElementId(id);
            if (doc.GetElement(eid) != null) valid.Add(eid);
            else missing.Add(id);
        }
        if (valid.Count == 0)
        {
            var fail = ModelPageServer.Fail("not_found", ids.Count == 1
                ? "This element no longer exists in the model."
                : "None of these elements exist in the model any more.");
            fail["missing"] = missing;
            return fail;
        }

        uidoc.Selection.SetElementIds(valid);
        var zoomed = false;
        if (zoom)
        {
            try { uidoc.ShowElements(valid); zoomed = true; }
            catch { /* the active view cannot show them; the selection still stands */ }
        }
        return new Dictionary<string, object?>
        {
            ["ok"] = true, ["status"] = "selected", ["selected"] = valid.Count, ["missing"] = missing, ["zoomed"] = zoomed
        };
    }

    private object Values(UIApplication app, ModelPageInfo page, IReadOnlyList<long> ids, IReadOnlyList<string> parameters)
    {
        var uidoc = PageDocument(app, page, out var refusal);
        if (uidoc == null) return refusal!;
        var doc = uidoc.Document;

        // Per element: parameter name → display value. A parameter the element does not have is left out,
        // one without a value is null, so the page can tell "empty" from "no such parameter".
        var values = new Dictionary<string, Dictionary<string, string?>>();
        // Per element: the parameters a page may change — writable instance parameters holding text or a number.
        var editable = new Dictionary<string, List<string>>();
        var canEdit = _dispatcher != null && EditsEnabled();
        var missing = new List<long>();
        foreach (var id in ids)
        {
            var element = doc.GetElement(new ElementId(id));
            if (element == null) { missing.Add(id); continue; }

            Element? type = null;
            var typeId = element.GetTypeId();
            if (typeId != null && typeId != ElementId.InvalidElementId) type = doc.GetElement(typeId);

            var key = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var row = new Dictionary<string, string?>();
            var open = new List<string>();
            foreach (var name in parameters)
            {
                var own = element.LookupParameter(name);
                var parameter = own ?? type?.LookupParameter(name);
                if (parameter == null) continue;
                row[name] = ParameterResolver.DisplayValue(parameter);
                if (canEdit && own != null && !own.IsReadOnly &&
                    own.StorageType is StorageType.String or StorageType.Integer or StorageType.Double)
                    open.Add(name);
            }
            values[key] = row;
            if (open.Count > 0) editable[key] = open;
        }
        return new Dictionary<string, object?>
        {
            ["ok"] = true, ["status"] = "read", ["values"] = values, ["editable"] = editable, ["missing"] = missing
        };
    }
}
