using Autodesk.Revit.UI;
using RevitMCP.Core.Models;

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
    }

    public RevitDocumentContext? GetLastContext() => _handler.GetLastContext();

    public void CancelAllPending(string reason) => _handler.CancelAllPending(reason);

    /// <summary>
    /// Re-enqueues a previously intercepted request (with its original TCS) and raises ExternalEvent.
    /// Used by ApprovalService to dispatch approved requests back to the Revit API thread.
    /// </summary>
    public void Redispatch(McpToolRequest request, TaskCompletionSource<McpToolResult> existingTcs)
    {
        _handler.Enqueue(request, existingTcs);
        _externalEvent.Raise();
    }

    /// <summary>
    /// Enqueues a tool request and raises ExternalEvent. Returns when Revit executes it.
    /// Times out after <paramref name="timeoutMs"/> if Revit never processes the event.
    /// If Raise() returns Denied, the item is still in the queue and will be processed
    /// when the already-pending Execute() fires — so we always await the TCS.
    /// </summary>
    public async Task<McpToolResult> DispatchAsync(McpToolRequest request, int timeoutMs = 30_000)
    {
        var tcs = new TaskCompletionSource<McpToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.Enqueue(request, tcs);

        // Raise the event. Denied means an Execute() is already pending — that's fine,
        // it will drain our queued item too. TimedOut means Revit can't process events at all.
        var status = _externalEvent.Raise();
        if (status == ExternalEventRequest.TimedOut)
        {
            _handler.CancelPending(request.RequestId);
            var busyResult = new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Message = "Revit is busy — external event timed out. Revit may be in a modal dialog or processing another operation. Try again when Revit is idle."
            };
            // Set Status separately; defensive try/catch guards against assembly version
            // mismatch during hot-reload (MissingMethodException on set_Status).
            try { busyResult.Status = "revit_busy"; } catch (MissingMethodException) { }
            return busyResult;
        }

        using var cts = new CancellationTokenSource(timeoutMs);
        cts.Token.Register(() =>
        {
            var timeoutResult = new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Message = $"Request timed out after {timeoutMs / 1000}s waiting for the Revit API thread. Revit may be unresponsive."
            };
            try { timeoutResult.Status = "revit_busy"; } catch (MissingMethodException) { }
            tcs.TrySetResult(timeoutResult);
        });

        return await tcs.Task;
    }
}
