using System.Collections.Concurrent;
using RevitMCP.Core.Models;
using RevitMCP.Core.Safety;

namespace RevitMCP.Addin.Approval;

/// <summary>
/// Manages the queue of pending approval requests for RequiresApproval tools.
/// When a tool requires approval, its execution is deferred until the user
/// clicks Approve or Reject in the MCP window.
/// </summary>
public class ApprovalService
{
    private readonly ConcurrentDictionary<string, PendingApprovalRequest> _pending = new();
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly TimeSpan _approvalLifetime;
    private int _count;
    private Action<PendingApprovalRequest>? _redispatch;

    private const int MaxOutcomeHistory = 100;
    private readonly object _outcomeGate = new();
    private readonly Dictionary<string, ApprovalOutcome> _outcomes = new();
    private readonly Queue<string> _outcomeOrder = new();

    /// <summary>Raised when an approval-gated request reaches its final state (succeeded, failed, rejected).</summary>
    public event Action<ApprovalOutcome>? OutcomeRecorded;

    public ApprovalService()
        : this(QueryLimits.Default.MaxPendingApprovals, TimeSpan.FromMinutes(QueryLimits.Default.ApprovalTimeoutMinutes))
    {
    }

    internal ApprovalService(int capacity, TimeSpan approvalLifetime)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (approvalLifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(approvalLifetime));
        _capacity = capacity;
        _approvalLifetime = approvalLifetime;
    }

    /// <summary>
    /// Raised when the pending queue changes (add, approve, reject).
    /// Subscribe from the ViewModel to refresh the UI.
    /// </summary>
    public event Action? PendingChanged;

    /// <summary>
    /// Sets the callback used to re-dispatch approved requests to the Revit API thread.
    /// Called once during startup with ExternalEventService.Redispatch.
    /// </summary>
    public void SetRedispatch(Action<PendingApprovalRequest> redispatch)
    {
        _redispatch = redispatch;
    }

    /// <summary>
    /// Adds a new pending approval request. Called from ExternalEventHandler
    /// when a RequiresApproval tool is intercepted.
    /// </summary>
    public bool Add(PendingApprovalRequest request)
    {
        lock (_gate)
        {
            if (_count >= _capacity)
                return false;
            if (!_pending.TryAdd(request.ApprovalId, request))
                return false;
            _count++;
        }

        TrackOutcome(request);
        PendingChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// The outcome of an approval-gated request, looked up by approvalId or requestId. Null when unknown
    /// (never seen, or evicted from the bounded history).
    /// </summary>
    public ApprovalOutcome? GetOutcome(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (_outcomeGate)
        {
            if (!_outcomes.TryGetValue(id, out var outcome))
                outcome = _outcomes.Values.FirstOrDefault(o => o.RequestId == id);
            outcome?.Refresh();
            return outcome;
        }
    }

    /// <summary>Most recent outcomes first.</summary>
    public IReadOnlyList<ApprovalOutcome> GetRecentOutcomes(int max = 20)
    {
        lock (_outcomeGate)
        {
            foreach (var outcome in _outcomes.Values) outcome.Refresh();
            return _outcomeOrder.Reverse().Take(Math.Max(1, max)).Select(id => _outcomes[id]).ToList();
        }
    }

    private void TrackOutcome(PendingApprovalRequest request)
    {
        var outcome = new ApprovalOutcome
        {
            ApprovalId = request.ApprovalId,
            RequestId = request.OriginalRequest.RequestId,
            ToolName = request.ToolName,
            Summary = request.Summary,
            ClientName = request.ClientName,
            CreatedAt = request.CreatedAt
        };

        lock (_outcomeGate)
        {
            _outcomes[outcome.ApprovalId] = outcome;
            _outcomeOrder.Enqueue(outcome.ApprovalId);
            // Bounded history: drop the oldest finished outcomes; pending ones stay until decided.
            var scans = _outcomeOrder.Count;
            while (_outcomes.Count > MaxOutcomeHistory && scans-- > 0)
            {
                var oldest = _outcomeOrder.Dequeue();
                _outcomes[oldest].Refresh();
                if (_outcomes[oldest].IsFinal)
                    _outcomes.Remove(oldest);
                else
                    _outcomeOrder.Enqueue(oldest);
            }
        }

        outcome.Completion = request.Completion.Task;
        request.Completion.Task.ContinueWith(
            t =>
            {
                lock (_outcomeGate)
                    outcome.Complete(ApprovalOutcome.ResultOf(t, outcome.RequestId), DateTimeOffset.Now);
                OutcomeRecorded?.Invoke(outcome);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void MarkDecided(string approvalId, string state)
    {
        lock (_outcomeGate)
        {
            if (!_outcomes.TryGetValue(approvalId, out var outcome) || outcome.IsFinal) return;
            outcome.State = state;
            outcome.DecidedAt = DateTimeOffset.Now;
        }
    }

    public IReadOnlyList<PendingApprovalRequest> GetPending()
        => _pending.Values.OrderBy(p => p.CreatedAt).ToList();

    public int Count => Volatile.Read(ref _count);

    /// <summary>
    /// Approves a pending request. Marks it as approved and re-dispatches
    /// to the Revit API thread for execution.
    /// </summary>
    public void Approve(string approvalId)
    {
        if (!TryRemove(approvalId, out var request)) return;

        if (DateTimeOffset.Now - request.CreatedAt > _approvalLifetime)
        {
            request.Completion.TrySetResult(new McpToolResult
            {
                RequestId = request.OriginalRequest.RequestId,
                Success = false,
                Status = "approval_expired",
                Message = "Approval expired. Run the preview again and request a new approval."
            });
            PendingChanged?.Invoke();
            return;
        }

        request.OriginalRequest.IsApproved = true;
        MarkDecided(request.ApprovalId, ApprovalOutcome.Running);
        _redispatch?.Invoke(request);
        PendingChanged?.Invoke();
    }

    /// <summary>
    /// Rejects a pending request. Resolves the pipe response with a failure message.
    /// </summary>
    public void Reject(string approvalId)
    {
        if (!TryRemove(approvalId, out var request)) return;

        var rejResult = new McpToolResult
        {
            RequestId = request.OriginalRequest.RequestId,
            Success = false,
            Message = "Action rejected by user."
        };
        try { rejResult.Status = "approval_rejected"; } catch (MissingMethodException) { }
        request.Completion.TrySetResult(rejResult);
        PendingChanged?.Invoke();
    }

    /// <summary>
    /// Rejects all pending requests. Called by PanicStop.
    /// </summary>
    public void RejectAll()
    {
        List<PendingApprovalRequest> rejected;
        lock (_gate)
        {
            rejected = _pending.Values.ToList();
            _pending.Clear();
            _count = 0;
        }

        foreach (var request in rejected)
            CompleteRejected(request);
        PendingChanged?.Invoke();
    }

    private bool TryRemove(string approvalId, out PendingApprovalRequest request)
    {
        lock (_gate)
        {
            if (!_pending.TryRemove(approvalId, out request!))
                return false;

            _count--;
            return true;
        }
    }

    private static void CompleteRejected(PendingApprovalRequest request)
    {
        var result = new McpToolResult
        {
            RequestId = request.OriginalRequest.RequestId,
            Success = false,
            Message = "Action rejected by user."
        };
        try { result.Status = "approval_rejected"; } catch (MissingMethodException) { }
        request.Completion.TrySetResult(result);
    }
}
