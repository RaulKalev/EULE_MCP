using System.IO;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Configuration;
using RevitMCP.Addin.Graph;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Graph;

/// <summary>
/// Reads the routing state of the open model's graph and attaches graph-first hints to broad live
/// queries (#64). Advisory only: it never blocks or changes a live result, and any failure here is
/// swallowed so the live tool's answer always goes back unchanged.
/// </summary>
internal static class GraphRoutingSupport
{
    /// <summary>Per-call opt-out argument accepted by every broad live tool.</summary>
    public const string OptOutArg = "graphHint";

    public static GraphRoutingState ReadState(UIApplication uiapp, Dictionary<string, object?> args)
    {
        var state = new GraphRoutingState();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return state;

        using var graph = GraphToolSupport.TryOpen(uiapp, doc, args, out _);
        if (graph == null) return state;

        state.Exists = true;
        state.Stale = graph.Freshness.Stale;
        state.StaleReason = graph.Freshness.Reason;
        var summary = graph.Database.Summarize(topN: 200, sampleSize: 0);
        foreach (var c in summary.NodesByKind) state.NodesByKind[c.Key] = c.Count;
        state.Categories = summary.ElementsByCategory.Select(c => c.Key).Where(k => !string.IsNullOrEmpty(k)).ToList();
        state.Levels = summary.ElementsByLevel.Select(c => c.Key).Where(k => !string.IsNullOrEmpty(k) && k != "(none)").ToList();
        return state;
    }

    /// <summary>
    /// Adds <c>routingHint</c> to the result of a broad live query (category scope, no ids, many
    /// matches). Opt out per call with graphHint=false or for a user with graph.routingHints=false.
    /// </summary>
    public static void TryAttachHint(UIApplication uiapp, McpToolRequest request, McpToolResult result)
    {
        try
        {
            if (!result.Success || result.Data == null) return;
            if (!GraphRouting.BroadLiveTools.Contains(request.ToolName)) return;
            if (!ToolArguments.GetBool(request.Arguments, OptOutArg, true)) return;
            if (ToolArguments.GetBool(request.Arguments, "useSelection")) return;
            if (ToolArguments.GetLongArray(request.Arguments, "elementIds").Length > 0) return;
            if (!HintsEnabledForUser()) return;

            var data = result.Data as JObject ?? JObject.FromObject(result.Data);
            var matched = (long?)data["totalMatched"] ?? (long?)data["returned"] ?? (data["elements"] as JArray)?.Count ?? 0;
            if (matched < GraphRouting.BroadQueryThreshold) return;

            var state = ReadState(uiapp, request.Arguments);
            var hint = GraphRouting.BroadQueryHint(
                request.ToolName,
                ToolArguments.GetString(request.Arguments, "category"),
                ToolArguments.GetString(request.Arguments, "level"),
                matched,
                state);
            if (hint == null) return;

            data["routingHint"] = hint;
            result.Data = data;
        }
        catch
        {
            // Advisory only — never let a hint break a live result.
        }
    }

    /// <summary>graph.routingHints=false in the user config switches the hints off for that user.</summary>
    private static bool HintsEnabledForUser()
    {
        try
        {
            var (path, _) = ConfigPathResolver.Resolve(ConfigPathResolver.ScopeUser);
            if (path == null || !File.Exists(path)) return true;
            var (config, _) = new JsonConfigService().Read(path);
            var node = config?["graph"]?["routingHints"];
            return node == null || !bool.TryParse(node.ToString(), out var on) || on;
        }
        catch
        {
            return true;
        }
    }
}
