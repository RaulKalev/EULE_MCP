using System.Collections.Concurrent;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Approval;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Services;

/// <summary>
/// Runs on the Revit API thread. Drains the pending request queue and resolves each TaskCompletionSource.
/// All Revit API calls must happen here — never on the pipe thread.
/// Tools marked RequiresApproval are intercepted and routed to ApprovalService instead of executing immediately.
/// </summary>
public class ExternalEventHandler : IExternalEventHandler
{
    private readonly ConcurrentQueue<(McpToolRequest Request, TaskCompletionSource<McpToolResult> Tcs)> _queue = new();
    private readonly Dictionary<string, IRevitMcpTool> _tools = new();
    private volatile RevitDocumentContext? _lastContext;
    private ApprovalService? _approvalService;

    public string GetName() => "RevitMCP.ExternalEventHandler";

    /// <summary>
    /// Wires the approval service. Called once during startup from App.cs.
    /// </summary>
    public void SetApprovalService(ApprovalService service) => _approvalService = service;

    public void RegisterTool(IRevitMcpTool tool)
        => _tools[tool.Name] = tool;

    public IReadOnlyList<string> GetRegisteredToolNames()
        => _tools.Keys.OrderBy(k => k).ToList();

    public void Enqueue(McpToolRequest request, TaskCompletionSource<McpToolResult> tcs)
        => _queue.Enqueue((request, tcs));

    public RevitDocumentContext? GetLastContext() => _lastContext;

    /// <summary>
    /// Drains the queue and resolves all pending requests with a failed result.
    /// Called by PanicStop to avoid requests hanging until timeout.
    /// </summary>
    public void CancelAllPending(string reason)
    {
        while (_queue.TryDequeue(out var item))
        {
            item.Tcs.TrySetResult(new McpToolResult
            {
                RequestId = item.Request.RequestId,
                Success = false,
                Message = reason
            });
        }
    }

    /// <summary>
    /// Marks a queued request as cancelled (best-effort) so Execute() skips it.
    /// Used when Raise() returns TimedOut and the caller is no longer waiting.
    /// </summary>
    public void CancelPending(string requestId)
    {
        foreach (var (req, tcs) in _queue)
        {
            if (req.RequestId == requestId)
                tcs.TrySetCanceled();
        }
    }

    public void Execute(UIApplication app)
    {
        _lastContext = CaptureContext(app);

        while (_queue.TryDequeue(out var item))
        {
            var (request, tcs) = item;

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
                continue;
            }

            // Intercept RequiresApproval tools — defer execution until user approves in the UI.
            // Approved requests (re-dispatched after user clicks Approve) skip this check.
            // Direct Edit mode bypasses approval entirely.
            var isDirectEditEnabled = RevitMCP.Addin.App.GetViewModel()?.IsDirectEditEnabled ?? false;
            if (tool.Permission == ToolPermission.RequiresApproval
                && !request.IsApproved
                && _approvalService != null
                && !isDirectEditEnabled)
            {
                var summary = ApprovalSummaryBuilder.Build(request);

                // Use a fresh TCS for the actual post-approval execution so we can log the result
                // without keeping the pipe connection open. The original TCS is resolved immediately
                // so the LLM sees approval_required instead of waiting 5 minutes.
                var executionTcs = new TaskCompletionSource<McpToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);

                _approvalService.Add(new PendingApprovalRequest
                {
                    OriginalRequest = request,
                    Completion = executionTcs,
                    ToolName = tool.Name,
                    Summary = summary,
                    ClientName = request.ClientName
                });
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
                continue; // pipe connection is now resolved; execution proceeds after user approves
            }

            try
            {
                // ExecuteAsync runs synchronously here because we are already on the Revit API thread.
                // The task will complete immediately for read-only tools.
                var result = tool.ExecuteAsync(app, request, CancellationToken.None).GetAwaiter().GetResult();
                tcs.TrySetResult(result);
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
                try { failResult.Status = "transaction_failed"; } catch (MissingMethodException) { }
                tcs.TrySetResult(failResult);
            }
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
