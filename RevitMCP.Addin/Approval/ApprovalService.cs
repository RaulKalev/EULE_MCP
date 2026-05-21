using System.Collections.Concurrent;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Approval;

/// <summary>
/// Manages the queue of pending approval requests for RequiresApproval tools.
/// When a tool requires approval, its execution is deferred until the user
/// clicks Approve or Reject in the MCP window.
/// </summary>
public class ApprovalService
{
    private readonly ConcurrentDictionary<string, PendingApprovalRequest> _pending = new();
    private Action<McpToolRequest, TaskCompletionSource<McpToolResult>>? _redispatch;

    /// <summary>
    /// Raised when the pending queue changes (add, approve, reject).
    /// Subscribe from the ViewModel to refresh the UI.
    /// </summary>
    public event Action? PendingChanged;

    /// <summary>
    /// Sets the callback used to re-dispatch approved requests to the Revit API thread.
    /// Called once during startup with ExternalEventService.Redispatch.
    /// </summary>
    public void SetRedispatch(Action<McpToolRequest, TaskCompletionSource<McpToolResult>> redispatch)
    {
        _redispatch = redispatch;
    }

    /// <summary>
    /// Adds a new pending approval request. Called from ExternalEventHandler
    /// when a RequiresApproval tool is intercepted.
    /// </summary>
    public void Add(PendingApprovalRequest request)
    {
        _pending[request.ApprovalId] = request;
        PendingChanged?.Invoke();
    }

    public IReadOnlyList<PendingApprovalRequest> GetPending()
        => _pending.Values.OrderBy(p => p.CreatedAt).ToList();

    public int Count => _pending.Count;

    /// <summary>
    /// Approves a pending request. Marks it as approved and re-dispatches
    /// to the Revit API thread for execution.
    /// </summary>
    public void Approve(string approvalId)
    {
        if (!_pending.TryRemove(approvalId, out var request)) return;

        request.OriginalRequest.IsApproved = true;
        _redispatch?.Invoke(request.OriginalRequest, request.Completion);
        PendingChanged?.Invoke();
    }

    /// <summary>
    /// Rejects a pending request. Resolves the pipe response with a failure message.
    /// </summary>
    public void Reject(string approvalId)
    {
        if (!_pending.TryRemove(approvalId, out var request)) return;

        request.Completion.TrySetResult(new McpToolResult
        {
            RequestId = request.OriginalRequest.RequestId,
            Success = false,
            Status = "approval_rejected",
            Message = "Action rejected by user."
        });
        PendingChanged?.Invoke();
    }

    /// <summary>
    /// Rejects all pending requests. Called by PanicStop.
    /// </summary>
    public void RejectAll()
    {
        foreach (var key in _pending.Keys.ToList())
            Reject(key);
    }
}
