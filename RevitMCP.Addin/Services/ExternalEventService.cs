using Autodesk.Revit.UI;
using RevitMCP.Addin.Approval;
using RevitMCP.Core.Models;
using RevitMCP.Core.Safety;

namespace RevitMCP.Addin.Services;

/// <summary>
/// Wraps an ExternalEvent and provides a thread-safe way to dispatch tool requests
/// to the Revit API thread and await their results.
/// </summary>
public class ExternalEventService
{
    private readonly ExternalEventHandler _handler;
    private readonly ExternalEvent _externalEvent;

    public ExternalEventService(ExternalEventHandler handler)
    {
        _handler = handler;
        _externalEvent = ExternalEvent.Create(handler);
        _handler.SetRequestNextDispatch(RequestNextDispatch);
    }

    public RevitDocumentContext? GetLastContext() => _handler.GetLastContext();

    public void CancelAllPending(string reason) => _handler.CancelAllPending(reason);

    /// <summary>
    /// Re-enqueues a previously intercepted request (with its original TCS) and raises ExternalEvent.
    /// Used by ApprovalService to dispatch approved requests back to the Revit API thread.
    /// </summary>
    public void Redispatch(PendingApprovalRequest pending)
    {
        var item = new ExternalEventWorkItem(
            pending.OriginalRequest,
            pending.Completion,
            pending.IsDocumentBound,
            pending.OriginDocumentToken,
            pending.OriginDocumentTitle,
            pending.OriginDocumentVersion,
            pending.IsSelectionBound,
            pending.OriginSelectionIds);

        if (!TryQueue(item))
            return;

        ScheduleTimeout(item, LongRunningTools.TimeoutMsFor(
            pending.ToolName, Math.Max(1, QueryLimits.Default.TimeoutSeconds) * 1000));
        RaiseOrCancel(item);
    }

    /// <summary>
    /// Enqueues a tool request and raises ExternalEvent. Returns when Revit executes it.
    /// Times out after <paramref name="timeoutMs"/> if Revit never processes the event.
    /// If Raise() returns Denied, the item is still in the queue and will be processed
    /// when the already-pending Execute() fires — so we always await the TCS.
    /// </summary>
    public async Task<McpToolResult> DispatchAsync(
        McpToolRequest request,
        int timeoutMs = 30_000,
        CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<McpToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new ExternalEventWorkItem(request, tcs);
        if (!TryQueue(item))
            return await tcs.Task;

        using var timeoutCts = new CancellationTokenSource(Math.Max(1, timeoutMs));
        using var timeoutRegistration = timeoutCts.Token.Register(() =>
        {
            AbandonIfNotStarted(item);
            item.Cancel(
                $"Request timed out after {timeoutMs / 1000}s executing on the Revit API thread.",
                "request_timeout");
        });
        using var cancellationRegistration = cancellationToken.Register(() =>
            item.Cancel("Request was cancelled by the client.", "request_cancelled"));

        // A request still waiting for the Revit API thread after the busy timeout is answered with the
        // reason instead of silently running into the overall timeout (#87). Running requests are left alone.
        var busyTimeoutMs = Math.Max(1, QueryLimits.Default.BusyTimeoutSeconds) * 1000;
        using var busyCts = new CancellationTokenSource(busyTimeoutMs);
        using var busyRegistration = busyTimeoutMs < timeoutMs
            ? busyCts.Token.Register(() => AbandonIfNotStarted(item))
            : default(CancellationTokenRegistration);

        // Raise the event. Denied means an Execute() is already pending — that's fine,
        // it will drain our queued item too. TimedOut means Revit can't process events at all.
        var status = _externalEvent.Raise();
        var raiseLog = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitMCP_startup.log");
        try { System.IO.File.AppendAllText(raiseLog, $"[EVNT {DateTime.Now:HH:mm:ss.fff}] Raise() returned {status} for {request.ToolName}{Environment.NewLine}"); } catch { }
        if (status == ExternalEventRequest.TimedOut)
            AbandonIfNotStarted(item);

        return await tcs.Task;
    }

    /// <summary>Busy state of the Revit API thread; safe to call from any thread.</summary>
    public RevitBusySnapshot GetBusySnapshot() => _handler.GetBusySnapshot();

    private void AbandonIfNotStarted(ExternalEventWorkItem item)
    {
        if (item.IsStarted || item.Completion.Task.IsCompleted)
            return;

        var snapshot = _handler.GetBusySnapshot();
        item.TryCancelBeforeStart(
            $"Revit is busy and did not start {item.Request.ToolName}. " +
            $"{RevitBusyDiagnosis.Describe(snapshot)} {RevitBusyDiagnosis.Hint(snapshot)} Nothing was executed.",
            RevitBusyDiagnosis.BusyStatus,
            BusyData(snapshot));
    }

    /// <summary>Structured busy diagnostics shared by busy results and the connection status fallback.</summary>
    public static object BusyData(RevitBusySnapshot s) => new
    {
        revitBusy = true,
        busyCause = RevitBusyDiagnosis.Cause(s),
        busyReason = RevitBusyDiagnosis.Describe(s),
        hint = RevitBusyDiagnosis.Hint(s),
        modalDialog = s.ModalDialogTitle,
        mainWindowEnabled = s.MainWindowEnabled,
        mainWindowResponding = s.MainWindowResponding,
        executingTool = s.ActiveTool,
        executingForSeconds = s.ActiveSeconds.HasValue ? Math.Round(s.ActiveSeconds.Value, 1) : (double?)null,
        queuedRequests = s.QueueLength,
        secondsSinceLastProcessed = s.SecondsSinceLastExecute.HasValue ? Math.Round(s.SecondsSinceLastExecute.Value, 1) : (double?)null
    };

    private bool TryQueue(ExternalEventWorkItem item)
    {
        if (_handler.TryEnqueue(item))
            return true;

        item.Cancel(
            "The Revit request queue is full. Wait for current work to finish and retry.",
            "queue_full");
        item.Dispose();
        return false;
    }

    private void RaiseOrCancel(ExternalEventWorkItem item)
    {
        var status = _externalEvent.Raise();
        if (status == ExternalEventRequest.TimedOut)
        {
            var snapshot = _handler.GetBusySnapshot();
            item.TryCancelBeforeStart(
                $"Revit is busy — the approved request could not be dispatched. {RevitBusyDiagnosis.Describe(snapshot)}",
                RevitBusyDiagnosis.BusyStatus,
                BusyData(snapshot));
        }
    }

    private static void ScheduleTimeout(ExternalEventWorkItem item, int timeoutMs)
    {
        _ = Task.Delay(timeoutMs).ContinueWith(
            _ =>
            {
                if (!item.Completion.Task.IsCompleted)
                    item.Cancel("Approved request timed out before completion.", "request_timeout");
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void RequestNextDispatch()
    {
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(25);

                var status = _externalEvent.Raise();
                if (status != ExternalEventRequest.Denied)
                    return;
            }
        });
    }
}
