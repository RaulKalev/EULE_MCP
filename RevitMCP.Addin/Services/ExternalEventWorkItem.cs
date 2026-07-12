using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Services;

/// <summary>
/// A single queued request and its cooperative cancellation state.
/// The queue owns and disposes the work item after it is processed or drained.
/// </summary>
public sealed class ExternalEventWorkItem : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private int _disposed;

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

    public void Cancel(string message, string status)
    {
        try { _cancellation.Cancel(); } catch (ObjectDisposedException) { }

        var result = new McpToolResult
        {
            RequestId = Request.RequestId,
            Success = false,
            Status = status,
            Message = message
        };
        Completion.TrySetResult(result);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _cancellation.Dispose();
    }
}
