using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Services;
using RevitMCP.Village;

namespace RevitMCP.Addin.Village.Hosting;

/// <summary>
/// Selects and zooms to a flyer's elements when the village page asks ("Show in Revit"). Runs on
/// its own <see cref="ExternalEvent"/>, so it never enters the MCP dispatcher, the approval queue
/// or the activity log, and it costs no tokens: the AI is not involved. It only changes the UI
/// selection and the view — never the model — and refuses when the active document is not the
/// model the flyer was captured in. One request at a time.
/// </summary>
public sealed class VillageShowHandler : IExternalEventHandler
{
    private readonly object _gate = new();
    private ExternalEvent? _event;
    private Pending? _pending;

    private sealed class Pending
    {
        public VillageShowRequest Request = new();
        public TaskCompletionSource<VillageShowResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Must be called in a valid Revit API context (App.OnStartup).</summary>
    public static VillageShowHandler Create()
    {
        var handler = new VillageShowHandler();
        handler._event = ExternalEvent.Create(handler);
        return handler;
    }

    public string GetName() => "RevitMCP.Village.ShowInRevit";

    /// <summary>Queues the selection for the Revit thread and waits for it.</summary>
    public async Task<VillageShowResult> ShowAsync(VillageShowRequest request, CancellationToken ct)
    {
        var ev = _event;
        if (ev == null) return VillageShowResult.Fail(VillageShowStatus.Unavailable, "Show in Revit is not available.");

        Pending pending;
        lock (_gate)
        {
            if (_pending != null) return VillageShowResult.Fail(VillageShowStatus.Busy, "Revit is still showing the previous selection.");
            pending = new Pending { Request = request };
            _pending = pending;
        }

        using var registration = ct.Register(() =>
        {
            lock (_gate) if (ReferenceEquals(_pending, pending)) _pending = null;
            pending.Completion.TrySetResult(VillageShowResult.Fail(VillageShowStatus.Busy, "Revit did not respond. Close any open dialog in Revit and try again."));
        });

        // Denied and Pending both mean an Execute is already on its way, which picks this up too.
        if (ev.Raise() == ExternalEventRequest.TimedOut)
        {
            lock (_gate) if (ReferenceEquals(_pending, pending)) _pending = null;
            return VillageShowResult.Fail(VillageShowStatus.Busy, "Revit is busy. Try again in a moment.");
        }
        return await pending.Completion.Task.ConfigureAwait(false);
    }

    public void Execute(UIApplication app)
    {
        Pending? pending;
        lock (_gate)
        {
            pending = _pending;
            _pending = null;
        }
        if (pending == null) return;

        try
        {
            pending.Completion.TrySetResult(Show(app, pending.Request));
        }
        catch (Exception ex)
        {
            pending.Completion.TrySetResult(VillageShowResult.Fail(VillageShowStatus.Error, "Show in Revit failed: " + ex.Message));
        }
    }

    private static VillageShowResult Show(UIApplication app, VillageShowRequest request)
    {
        var uidoc = app.ActiveUIDocument;
        var doc = uidoc?.Document;
        if (uidoc == null || doc == null)
            return VillageShowResult.Fail(VillageShowStatus.NoDocument, "No document is open in Revit.");

        // Element ids are per document: selecting them in another model would pick unrelated elements.
        var info = RevitContextService.Read(app);
        var activeModel = VillageModelId.Compute(info.CentralModelPath, info.LocalModelPath, info.DocumentTitle);
        if (!string.Equals(activeModel, request.ModelId, StringComparison.Ordinal))
            return VillageShowResult.Fail(VillageShowStatus.DifferentModel,
                $"This flyer belongs to another model. Switch to it in Revit (the active document is '{doc.Title}') and try again.");

        var valid = new List<ElementId>();
        var missing = 0;
        foreach (var id in request.ElementIds)
        {
            var eid = new ElementId(id);
            if (doc.GetElement(eid) != null) valid.Add(eid);
            else missing++;
        }
        if (valid.Count == 0)
            return new VillageShowResult
            {
                Ok = false, Status = VillageShowStatus.NotFound, Missing = missing,
                Message = "None of these elements exist in the model any more."
            };

        uidoc.Selection.SetElementIds(valid);
        var zoomed = true;
        try { uidoc.ShowElements(valid); }
        catch { zoomed = false; }

        var message = $"Selected {valid.Count:N0} element{(valid.Count == 1 ? "" : "s")} in Revit" +
                      (zoomed ? "" : " (the active view cannot zoom to them)") +
                      (missing > 0 ? $"; {missing:N0} no longer exist." : ".");
        return new VillageShowResult { Ok = true, Status = VillageShowStatus.Shown, Selected = valid.Count, Missing = missing, Message = message };
    }
}
