using Newtonsoft.Json.Linq;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Memory.Hosting;

/// <summary>
/// Where the connector hands completed calls to shared memory: <see cref="Logging.ActivityLogger"/> for every
/// completed request and <see cref="Approval.ApprovalService"/> for a rejection (which is never logged as a call).
/// Each call is O(1), never blocks and never throws; it copies the allow-listed argument values and the document's
/// project key, and keeps nothing of the request or result.
/// </summary>
public static class MemoryHooks
{
    /// <summary>Tool permission lookup, set at startup (the request does not carry it).</summary>
    public static Func<string, ToolPermission?>? PermissionOf { get; set; }

    /// <summary>The last captured document context, for rejections (the approval queue does not keep one).</summary>
    public static Func<RevitDocumentContext?>? LastContext { get; set; }

    public static void ToolCompleted(McpToolRequest? request, McpToolResult? result, RevitDocumentContext? context)
    {
        try
        {
            var service = MemoryService.Current;
            if (service == null || request == null || result == null) return;
            if (WorkflowObserver.IsIgnored(request.ToolName)) return;
            var call = Build(request, context);
            call.Success = result.Success;
            call.Status = result.Status;
            if (request.ToolName == "revit_graph_route" && result.Success) call.RouteKey = RouteKey(request, result);
            service.Observe(call);
        }
        catch
        {
            // Learning must never affect a tool call.
        }
    }

    /// <summary>The user rejected a pending call in Revit: a correction may follow.</summary>
    public static void ToolRejected(McpToolRequest? request)
    {
        try
        {
            var service = MemoryService.Current;
            if (service == null || request == null) return;
            var call = Build(request, LastContext?.Invoke());
            call.Success = false;
            call.Status = WorkflowObserver.RejectedStatus;
            service.Observe(call);
        }
        catch
        {
        }
    }

    private static ObservedCall Build(McpToolRequest request, RevitDocumentContext? context)
    {
        var permission = PermissionOf?.Invoke(request.ToolName) ?? request.Permission;
        var central = context?.CentralPath ?? string.Empty;
        return new ObservedCall
        {
            Tool = request.ToolName,
            Client = request.ClientName ?? string.Empty,
            User = string.IsNullOrWhiteSpace(context?.RevitUsername) ? Environment.UserName : context!.RevitUsername,
            Project = string.IsNullOrWhiteSpace(context?.ProjectKey) ? null : context!.ProjectKey,
            ModelPath = central.Length > 0 ? central : context?.LocalPath,
            IsWrite = permission != ToolPermission.ReadOnly,
            IsApproved = request.IsApproved,
            Arguments = WorkflowObserver.ExtractArguments(request.Arguments),
            Time = DateTimeOffset.UtcNow
        };
    }

    private static string? RouteKey(McpToolRequest request, McpToolResult result)
    {
        string? route = null, category = null;
        if (result.Data != null)
        {
            var data = result.Data as JObject ?? JObject.FromObject(result.Data);
            route = data["route"]?.ToString();
            category = data["detected"]?["category"]?.ToString();
        }
        var intent = request.Arguments.TryGetValue("intent", out var i) ? i?.ToString() : null;
        return WorkflowObserver.RouteKeyOf(route, category, intent);
    }
}
