using RevitMCP.Village;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Village.Hosting;

/// <summary>
/// The only two places the connector touches the village. Both calls are O(1), never block,
/// never throw, and do nothing when the village is disabled or not running. They read values the
/// connector already has (tool name, client, success, status, duration, document context) and
/// never keep the request or result.
/// </summary>
public static class VillageHooks
{
    /// <summary>Called by PipeServer just before a request is dispatched.</summary>
    public static void ToolStarted(McpToolRequest? request, RevitDocumentContext? context)
    {
        try
        {
            var service = VillageService.Current;
            if (service == null || request == null || !service.IsRunning) return;
            service.Hub.ToolStarted(request.ToolName, request.ClientName, ToProject(context));
        }
        catch
        {
            // Visualization must never affect a tool call.
        }
    }

    /// <summary>Called by ActivityLogger for every completed request (pipe path and post-approval path).</summary>
    public static void ToolCompleted(McpToolRequest? request, McpToolResult? result, RevitDocumentContext? context)
    {
        try
        {
            var service = VillageService.Current;
            if (service == null || request == null || result == null || !service.IsRunning) return;
            service.Hub.ToolCompleted(
                request.ToolName,
                request.ClientName,
                result.Success,
                result.Status,
                result.DurationMs,
                result.Data,
                ToProject(context));
        }
        catch
        {
            // Visualization must never affect a tool call.
        }
    }

    public static VillageProjectContext? ToProject(RevitDocumentContext? context)
    {
        if (context == null) return null;
        return new VillageProjectContext
        {
            ModelTitle = context.ModelTitle ?? string.Empty,
            CentralPath = context.CentralPath ?? string.Empty,
            LocalPath = context.LocalPath ?? string.Empty,
            RevitVersion = context.RevitVersion ?? string.Empty,
            IsWorkshared = context.IsWorkshared
        };
    }
}
