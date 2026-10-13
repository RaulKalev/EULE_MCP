using System.Collections.Concurrent;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Services;
using RevitMCP.Addin.Tools;
using RevitMCP.Village;

namespace RevitMCP.Addin.ModelPages.Hosting;

/// <summary>
/// The Revit side of a model page: selects and zooms to elements, and reads current parameter values.
/// Runs on its own <see cref="ExternalEvent"/>, so a click on a page never enters the MCP dispatcher,
/// the approval queue or the activity log, and costs no tokens. It changes the UI selection and the
/// view only — never the model — and refuses when the active document is not the page's model.
/// </summary>
public sealed class ModelPageRevitHandler : IExternalEventHandler, IModelPageBackend
{
    private readonly ConcurrentQueue<Work> _queue = new();
    private ExternalEvent? _event;

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

    private static object Values(UIApplication app, ModelPageInfo page, IReadOnlyList<long> ids, IReadOnlyList<string> parameters)
    {
        var uidoc = PageDocument(app, page, out var refusal);
        if (uidoc == null) return refusal!;
        var doc = uidoc.Document;

        // Per element: parameter name → display value. A parameter the element does not have is left out,
        // one without a value is null, so the page can tell "empty" from "no such parameter".
        var values = new Dictionary<string, Dictionary<string, string?>>();
        var missing = new List<long>();
        foreach (var id in ids)
        {
            var element = doc.GetElement(new ElementId(id));
            if (element == null) { missing.Add(id); continue; }

            Element? type = null;
            var typeId = element.GetTypeId();
            if (typeId != null && typeId != ElementId.InvalidElementId) type = doc.GetElement(typeId);

            var row = new Dictionary<string, string?>();
            foreach (var name in parameters)
            {
                var parameter = element.LookupParameter(name) ?? type?.LookupParameter(name);
                if (parameter != null) row[name] = ParameterResolver.DisplayValue(parameter);
            }
            values[id.ToString(System.Globalization.CultureInfo.InvariantCulture)] = row;
        }
        return new Dictionary<string, object?> { ["ok"] = true, ["status"] = "read", ["values"] = values, ["missing"] = missing };
    }
}
