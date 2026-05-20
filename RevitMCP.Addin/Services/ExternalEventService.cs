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
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Message = "Revit external event timed out. Revit may be busy or in an invalid state."
            };
        }

        using var cts = new CancellationTokenSource(timeoutMs);
        cts.Token.Register(() => tcs.TrySetResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = false,
            Message = "Request timed out waiting for Revit API thread."
        }));

        return await tcs.Task;
    }
}
