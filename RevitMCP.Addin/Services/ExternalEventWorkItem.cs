using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Services;

/// <summary>
/// A single queued request and its cooperative cancellation state.
/// The queue owns and disposes the work item after it is processed or drained.
/// </summary>
public sealed class ExternalEventWorkItem : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private const int NotStarted = 0, Started = 1, Abandoned = 2;
    private int _disposed;
    private int _started;

    public ExternalEventWorkItem(
        McpToolRequest request,
        TaskCompletionSource<McpToolResult> completion,
        bool isDocumentBound = false,
        object? expectedDocument = null,
        string expectedDocumentTitle = "",
        long expectedDocumentVersion = 0,
        bool isSelectionBound = false,
        IReadOnlyList<long>? expectedSelectionIds = null)
    {
        Request = request;
        Completion = completion;
        IsDocumentBound = isDocumentBound;
        ExpectedDocument = expectedDocument;
        ExpectedDocumentTitle = expectedDocumentTitle;
        ExpectedDocumentVersion = expectedDocumentVersion;
        IsSelectionBound = isSelectionBound;
        ExpectedSelectionIds = expectedSelectionIds ?? Array.Empty<long>();
        CancellationToken = _cancellation.Token;
    }

    public McpToolRequest Request { get; }
    public TaskCompletionSource<McpToolResult> Completion { get; }
    public CancellationToken CancellationToken { get; }
    public bool IsDocumentBound { get; }
    public object? ExpectedDocument { get; }
    public string ExpectedDocumentTitle { get; }
    public long ExpectedDocumentVersion { get; }
    public bool IsSelectionBound { get; }
    public IReadOnlyList<long> ExpectedSelectionIds { get; }

    /// <summary>True once the Revit API thread picked the item up (it is no longer just waiting in the queue).</summary>
    public bool IsStarted => Volatile.Read(ref _started) == Started;

    /// <summary>
    /// Marks the item as picked up by the Revit API thread. Returns false when it was already started or
    /// was abandoned by <see cref="TryCancelBeforeStart"/>, in which case it must not run.
    /// </summary>
    public bool MarkStarted() => Interlocked.CompareExchange(ref _started, Started, NotStarted) == NotStarted;

    /// <summary>
    /// Cancels the item only if the Revit API thread has not picked it up yet. Atomic with
    /// <see cref="MarkStarted"/>, so a request is never cancelled halfway through execution.
    /// </summary>
    public bool TryCancelBeforeStart(string message, string status, object? data = null)
    {
        if (Interlocked.CompareExchange(ref _started, Abandoned, NotStarted) != NotStarted)
            return false;
        Cancel(message, status, data);
        return true;
    }

    public void Cancel(string message, string status, object? data = null)
    {
        try { _cancellation.Cancel(); } catch (ObjectDisposedException) { }

        var result = new McpToolResult
        {
            RequestId = Request.RequestId,
            Success = false,
            Status = status,
            Message = message,
            Data = data
        };
        Completion.TrySetResult(result);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _cancellation.Dispose();
    }
}
