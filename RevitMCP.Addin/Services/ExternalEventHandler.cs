using System.Collections.Concurrent;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Approval;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Logging;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;
using RevitMCP.Core.Safety;

namespace RevitMCP.Addin.Services;

/// <summary>
/// Runs on the Revit API thread. Processes a bounded batch of queued requests.
/// All Revit API calls must happen here — never on the pipe thread.
/// Tools marked RequiresApproval are intercepted and routed to ApprovalService instead of executing immediately.
/// </summary>
public class ExternalEventHandler : IExternalEventHandler
{
    private readonly ExternalEventWorkQueue _queue;
    private readonly ConcurrentDictionary<string, ExternalEventWorkItem> _active = new();
    private readonly int _maxRequestsPerExecute;
    private readonly SemaphoreSlim _backgroundSlots;
    private readonly Dictionary<string, IRevitMcpTool> _tools = new();
    private volatile RevitDocumentContext? _lastContext;
    private ApprovalService? _approvalService;
    private ActivityLogger? _activityLogger;
    private Action? _requestNextDispatch;

    public ExternalEventHandler()
        : this(QueryLimits.Default)
    {
    }

    internal ExternalEventHandler(QueryLimits limits)
    {
        _queue = new ExternalEventWorkQueue(limits.MaxQueuedRequests);
        _maxRequestsPerExecute = Math.Max(1, limits.MaxRequestsPerExternalEvent);
        _backgroundSlots = new SemaphoreSlim(
            Math.Max(1, limits.MaxConcurrentBackgroundRequests),
            Math.Max(1, limits.MaxConcurrentBackgroundRequests));
    }

    public string GetName() => "RevitMCP.ExternalEventHandler";

    /// <summary>
    /// Wires the approval service. Called once during startup from App.cs.
    /// </summary>
    public void SetApprovalService(ApprovalService service) => _approvalService = service;

    /// <summary>
    /// Wires the activity logger so post-approval executions are logged.
    /// </summary>
    public void SetActivityLogger(ActivityLogger logger) => _activityLogger = logger;

    public void SetRequestNextDispatch(Action requestNextDispatch)
        => _requestNextDispatch = requestNextDispatch;

    public void RegisterTool(IRevitMcpTool tool)
        => _tools[tool.Name] = tool;

    public IReadOnlyList<string> GetRegisteredToolNames()
        => _tools.Keys.OrderBy(k => k).ToList();

    public bool TryEnqueue(ExternalEventWorkItem item) => _queue.TryEnqueue(item);

    public RevitDocumentContext? GetLastContext() => _lastContext;

    /// <summary>
    /// Drains the queue and resolves all pending requests with a failed result.
    /// Called by PanicStop to avoid requests hanging until timeout.
    /// </summary>
    public void CancelAllPending(string reason)
    {
        _queue.Drain(reason, "request_cancelled");
        foreach (var item in _active.Values)
            item.Cancel(reason, "request_cancelled");
    }

    /// <summary>
    /// Marks a queued request as cancelled (best-effort) so Execute() skips it.
    /// Used when Raise() returns TimedOut and the caller is no longer waiting.
    /// </summary>
    public bool CancelPending(string requestId, string reason, string status)
        => _queue.TryCancel(requestId, reason, status);

    public void Execute(UIApplication app)
    {
        _lastContext = CaptureContext(app);

        try
        {
            for (var processed = 0; processed < _maxRequestsPerExecute; processed++)
            {
                if (!_queue.TryDequeue(out var item))
                    break;

                if (!_active.TryAdd(item!.Request.RequestId, item))
                {
                    item.Cancel("A request with the same requestId is already executing.", "validation_failed");
                    item.Dispose();
                    continue;
                }
                var disposeAfterExecute = true;
                try
                {
                    disposeAfterExecute = ExecuteItem(app, item!);
                }
                finally
                {
                    if (disposeAfterExecute)
                    {
                        _active.TryRemove(item!.Request.RequestId, out _);
                        item!.Dispose();
                    }
                }
            }
        }
        finally
        {
            if (_queue.HasItems)
                _requestNextDispatch?.Invoke();
        }
    }

    /// <returns>True when the caller should dispose the work item.</returns>
    private bool ExecuteItem(UIApplication app, ExternalEventWorkItem item)
    {
        var request = item.Request;
        var tcs = item.Completion;

        if (item.CancellationToken.IsCancellationRequested || tcs.Task.IsCompleted)
            return true;

        if (request.IsApproved &&
            !ApprovalContextGuard.IsValid(
                item.IsDocumentBound,
                item.ExpectedDocument,
                app.ActiveUIDocument?.Document))
        {
            var activeTitle = app.ActiveUIDocument?.Document?.Title ?? "no active document";
            var contextChanged = new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = "approval_context_changed",
                Message = $"Approval was created for '{item.ExpectedDocumentTitle}', but the active document is now '{activeTitle}'. Run the preview again and request a new approval."
            };
            tcs.TrySetResult(contextChanged);
            _ = _activityLogger?.WriteAsync(request, contextChanged, _lastContext);
            return true;
        }

        if (request.IsApproved && item.IsSelectionBound)
        {
            var activeSelection = app.ActiveUIDocument?.Selection.GetElementIds()
                .Select(id => id.Value)
                .ToList() ?? new List<long>();
            if (!ApprovalContextGuard.IsSelectionValid(
                    true,
                    item.ExpectedSelectionIds,
                    activeSelection))
            {
                var selectionChanged = new McpToolResult
                {
                    RequestId = request.RequestId,
                    Success = false,
                    Status = "approval_context_changed",
                    Message = "The active Revit selection changed after approval was requested. Run the preview again and request a new approval."
                };
                tcs.TrySetResult(selectionChanged);
                _ = _activityLogger?.WriteAsync(request, selectionChanged, _lastContext);
                return true;
            }
        }

        if (request.IsApproved && item.IsDocumentBound &&
            DocumentChangeTracker.Capture(item.ExpectedDocument) != item.ExpectedDocumentVersion)
        {
            var modelChanged = new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = "approval_context_changed",
                Message = $"The Revit model '{item.ExpectedDocumentTitle}' changed after approval was requested. Run the preview again and request a new approval."
            };
            tcs.TrySetResult(modelChanged);
            _ = _activityLogger?.WriteAsync(request, modelChanged, _lastContext);
            return true;
        }

        if (!_tools.TryGetValue(request.ToolName, out var tool))
        {
            var unknownResult = new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Message = $"Unknown tool: {request.ToolName}"
            };
            try { unknownResult.Status = "unknown_tool"; } catch (MissingMethodException) { }
            tcs.TrySetResult(unknownResult);
            return true;
        }

            // Intercept RequiresApproval / DestructiveRequiresManualApproval tools —
            // defer execution until user approves in the UI.
            // Approved requests (re-dispatched after user clicks Approve) skip this check.
            // Direct Edit mode bypasses RequiresApproval, but NEVER bypasses Destructive.
#if REVIT2024
            // Revit 2024 build has no UI window — direct edit is always disabled (headless).
            var isDirectEditEnabled = false;
#else
            var isDirectEditEnabled = RevitMCP.Addin.App.GetViewModel()?.IsDirectEditEnabled ?? false;
#endif
        var needsApproval =
            ((tool.Permission == ToolPermission.RequiresApproval && !isDirectEditEnabled)
             || tool.Permission == ToolPermission.DestructiveRequiresManualApproval)
            && !request.IsApproved
            && _approvalService != null;
        if (needsApproval)
        {
            var summary = ApprovalSummaryBuilder.Build(request);
            var activeDocument = app.ActiveUIDocument?.Document;
            var bindSelection = activeDocument != null &&
                                tool is not IBackgroundMcpTool &&
                                ToolArguments.GetBool(request.Arguments, "useSelection");
            var selectionIds = bindSelection
                ? app.ActiveUIDocument!.Selection.GetElementIds().Select(id => id.Value).OrderBy(id => id).ToList()
                : new List<long>();

                // Use a fresh TCS for the actual post-approval execution so we can log the result
                // without keeping the pipe connection open. The original TCS is resolved immediately
                // so the LLM sees approval_required instead of waiting 5 minutes.
                var executionTcs = new TaskCompletionSource<McpToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);

            var pendingApproval = new PendingApprovalRequest
            {
                OriginalRequest = request,
                Completion = executionTcs,
                ToolName = tool.Name,
                Summary = summary,
                ClientName = request.ClientName,
                IsDocumentBound = activeDocument != null && tool is not IBackgroundMcpTool,
                OriginDocumentToken = tool is IBackgroundMcpTool ? null : activeDocument,
                OriginDocumentTitle = tool is IBackgroundMcpTool ? string.Empty : activeDocument?.Title ?? string.Empty,
                OriginDocumentVersion = tool is IBackgroundMcpTool ? 0 : DocumentChangeTracker.Capture(activeDocument),
                IsSelectionBound = bindSelection,
                OriginSelectionIds = selectionIds
            };
            if (!_approvalService!.Add(pendingApproval))
            {
                executionTcs.TrySetResult(new McpToolResult
                {
                    RequestId = request.RequestId,
                    Success = false,
                    Status = "queue_full",
                    Message = "The approval queue is full. Resolve existing pending approvals and retry."
                });
                tcs.TrySetResult(new McpToolResult
                {
                    RequestId = request.RequestId,
                    Success = false,
                    Status = "queue_full",
                    Message = "The approval queue is full. Resolve existing pending approvals and retry."
                });
                return true;
            }
                // Return approval_required immediately — don't make the pipe wait 5 minutes.
                // The user approves in the Revit MCP window; execution happens asynchronously.
                var approvalResult = new McpToolResult
                {
                    RequestId = request.RequestId,
                    Success = false,
                    Message = $"'{summary}' is pending approval in Revit. Open the RevitMCP window and click Approve on the Pending tab to execute, or Reject to cancel."
                };
                try { approvalResult.Status = "approval_required"; } catch (MissingMethodException) { }
            tcs.TrySetResult(approvalResult);
            return true;
        }

        if (tool is IBackgroundMcpTool)
        {
            _ = ExecuteBackgroundItemAsync(app, tool, item);
            return false;
        }

        try
        {
            item.CancellationToken.ThrowIfCancellationRequested();
            var execution = tool.ExecuteAsync(app, request, item.CancellationToken);
            if (!execution.IsCompleted)
                throw new InvalidOperationException($"Tool '{tool.Name}' performed asynchronous work on the Revit API thread. Mark safe non-Revit I/O as background execution instead.");

            var result = execution.GetAwaiter().GetResult();
            tcs.TrySetResult(result);
            if (request.IsApproved)
                _ = _activityLogger?.WriteAsync(request, result, _lastContext);
        }
        catch (OperationCanceledException)
        {
            var cancelled = new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = "request_cancelled",
                Message = "Tool execution was cancelled before completion."
            };
            tcs.TrySetResult(cancelled);
            if (request.IsApproved)
                _ = _activityLogger?.WriteAsync(request, cancelled, _lastContext);
        }
        catch (Exception ex)
        {
            var failResult = new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Message = $"Tool execution failed: {ex.Message}",
                Errors = new List<string> { ex.ToString() }
            };
            try
            {
                failResult.Status = tool.Permission == ToolPermission.ReadOnly
                    ? "tool_execution_failed"
                    : "transaction_failed";
            }
            catch (MissingMethodException) { }
            tcs.TrySetResult(failResult);
            if (request.IsApproved)
                _ = _activityLogger?.WriteAsync(request, failResult, _lastContext);
        }

        return true;
    }

    private async Task ExecuteBackgroundItemAsync(
        UIApplication app,
        IRevitMcpTool tool,
        ExternalEventWorkItem item)
    {
        var request = item.Request;
        var enteredSlot = false;
        try
        {
            await _backgroundSlots.WaitAsync(item.CancellationToken).ConfigureAwait(false);
            enteredSlot = true;
            item.CancellationToken.ThrowIfCancellationRequested();

            // Marker contract: background tools must not access the UIApplication.
            var result = await tool.ExecuteAsync(app, request, item.CancellationToken).ConfigureAwait(false);
            item.Completion.TrySetResult(result);
            if (request.IsApproved)
                await (_activityLogger?.WriteAsync(request, result, _lastContext) ?? Task.CompletedTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            item.Completion.TrySetResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = "request_cancelled",
                Message = "Background tool execution was cancelled before completion."
            });
        }
        catch (Exception ex)
        {
            var failResult = new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = "tool_execution_failed",
                Message = $"Background tool execution failed: {ex.Message}",
                Errors = new List<string> { ex.ToString() }
            };
            item.Completion.TrySetResult(failResult);
            if (request.IsApproved)
                await (_activityLogger?.WriteAsync(request, failResult, _lastContext) ?? Task.CompletedTask).ConfigureAwait(false);
        }
        finally
        {
            if (enteredSlot)
                _backgroundSlots.Release();
            _active.TryRemove(request.RequestId, out _);
            item.Dispose();
        }
    }

    private static RevitDocumentContext CaptureContext(UIApplication app)
    {
        try
        {
            var ctx = RevitContextService.Read(app);
            return new RevitDocumentContext
            {
                RevitVersion = ctx.RevitVersion,
                ModelTitle = ctx.DocumentTitle,
                CentralPath = ctx.CentralModelPath,
                LocalPath = ctx.LocalModelPath,
                ActiveViewName = ctx.ActiveViewName,
                IsWorkshared = ctx.IsWorkshared,
                RevitUsername = ctx.RevitUsername
            };
        }
        catch
        {
            return new RevitDocumentContext();
        }
    }
}
