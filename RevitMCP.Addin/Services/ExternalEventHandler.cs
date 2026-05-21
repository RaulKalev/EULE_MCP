using System.Collections.Concurrent;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Services;

/// <summary>
/// Runs on the Revit API thread. Drains the pending request queue and resolves each TaskCompletionSource.
/// All Revit API calls must happen here — never on the pipe thread.
/// </summary>
public class ExternalEventHandler : IExternalEventHandler
{
    private readonly ConcurrentQueue<(McpToolRequest Request, TaskCompletionSource<McpToolResult> Tcs)> _queue = new();
    private readonly Dictionary<string, IRevitMcpTool> _tools = new();
    private volatile RevitDocumentContext? _lastContext;

    public string GetName() => "RevitMCP.ExternalEventHandler";

    public void RegisterTool(IRevitMcpTool tool)
        => _tools[tool.Name] = tool;

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
                tcs.TrySetResult(new McpToolResult
                {
                    RequestId = request.RequestId,
                    Success = false,
                    Message = $"Unknown tool: {request.ToolName}"
                });
                continue;
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
                tcs.TrySetResult(new McpToolResult
                {
                    RequestId = request.RequestId,
                    Success = false,
                    Message = $"Tool execution failed: {ex.Message}",
                    Errors = new List<string> { ex.ToString() }
                });
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
