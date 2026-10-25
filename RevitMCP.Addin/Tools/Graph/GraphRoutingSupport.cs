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

    /// <param name="category">
    /// When set, only that category's host and linked element counts are read (the gate's cheap path); otherwise
    /// every category, level and node kind.
    /// </param>
    public static GraphRoutingState ReadState(UIApplication uiapp, Dictionary<string, object?> args, string? category = null)
    {
        var state = new GraphRoutingState();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return state;

        using var graph = GraphToolSupport.TryOpen(uiapp, doc, args, out _);
        if (graph == null) return state;

        state.Exists = true;
        state.Stale = graph.Freshness.Stale;
        state.StaleReason = graph.Freshness.Reason;
        state.SetCounts(graph.Database.RoutingCounts(category));
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
            if (RevitMCP.Addin.Documents.ToolDocument.TargetsOtherDocument(uiapp, request)) return;   // the graph describes the active model
            if (!GraphRouting.BroadLiveTools.Contains(request.ToolName)) return;
            if (!ToolArguments.GetBool(request.Arguments, OptOutArg, true)) return;
            if (ToolArguments.GetBool(request.Arguments, "useSelection")) return;
            if (ToolArguments.GetLongArray(request.Arguments, "elementIds").Length > 0) return;
            if (!HintsEnabledForUser()) return;

            var data = result.Data as JObject ?? JObject.FromObject(result.Data);
            var matched = (long?)data["totalMatched"] ?? (long?)data["returned"] ?? (data["elements"] as JArray)?.Count ?? 0;
            if (matched < GraphRouting.BroadQueryThreshold) return;

            var state = ReadState(uiapp, request.Arguments, NullIfEmpty(ToolArguments.GetString(request.Arguments, "category")));
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

    // ── graph-first gate and automatic refresh (#96) ──────────────────────────

    private static readonly HashSet<string> GraphReadTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "revit_graph_query", "revit_graph_summary", "revit_graph_route", "revit_graph_status"
    };

    // A graph that is still stale after an automatic build is not rebuilt again for a while (per database file).
    private static readonly Dictionary<string, DateTime> AutoBuildBackoff = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan AutoBuildBackoffTime = TimeSpan.FromMinutes(10);

    private static GraphRouting.BroadRead ReadOf(McpToolRequest request)
    {
        var args = request.Arguments;
        var hasFilters = args.TryGetValue("filters", out var filters) && filters switch
        {
            null => false,
            JArray array => array.Count > 0,
            string text => text.Trim().Length > 2,
            _ => true
        };
        return new GraphRouting.BroadRead
        {
            Tool = request.ToolName,
            Category = ToolArguments.GetString(args, "category"),
            HasElementIds = ToolArguments.GetLongArray(args, "elementIds").Length > 0,
            UseSelection = ToolArguments.GetBool(args, "useSelection"),
            HasFilters = hasFilters,
            SummaryOnly = ToolArguments.GetBool(args, "summaryOnly"),
            GraphFirst = ToolArguments.GetBool(args, GraphRouting.GateArg, true)
        };
    }

    /// <summary>
    /// Builds a missing graph or refreshes a stale one before a graph read or a read the gate may answer, so the
    /// graph is there to be used instead of being a step the agent has to remember. Incremental when possible.
    /// Returns a note for the result, or null when nothing was done. graph.autoBuild=false switches it off.
    /// </summary>
    public static string? EnsureFresh(UIApplication uiapp, McpToolRequest request)
    {
        try
        {
            var isGraphRead = GraphReadTools.Contains(request.ToolName);
            if (!isGraphRead && !GraphRouting.IsGateCandidate(ReadOf(request))) return null;
            if (!isGraphRead && RevitMCP.Addin.Documents.ToolDocument.TargetsOtherDocument(uiapp, request)) return null;
            if (!EnabledForUser("autoBuild")) return null;
            var doc = uiapp.ActiveUIDocument?.Document;
            if (doc == null || doc.IsFamilyDocument) return null;

            bool missing;
            string databasePath;
            using (var graph = GraphToolSupport.TryOpen(uiapp, doc, request.Arguments, out _))
            {
                if (graph != null && !graph.Freshness.Stale) return null;
                missing = graph == null;
                databasePath = graph?.Location.DatabasePath ?? GraphToolSupport.Locate(uiapp, doc, request.Arguments).Location.DatabasePath;
            }
            lock (AutoBuildBackoff)
                if (AutoBuildBackoff.TryGetValue(databasePath, out var until) && DateTime.UtcNow < until) return null;

            var buildArgs = new Dictionary<string, object?> { ["incremental"] = !missing };
            foreach (var key in new[] { GraphToolSupport.ArgDbPath, GraphToolSupport.ArgSharedFolder })
                if (request.Arguments.TryGetValue(key, out var value) && value != null) buildArgs[key] = value;
            var build = new GraphBuildTool().ExecuteAsync(uiapp,
                new McpToolRequest { ToolName = "revit_graph_build", ClientName = request.ClientName, Arguments = buildArgs }, CancellationToken.None)
                .GetAwaiter().GetResult();

            bool fresh = false;
            if (build.Success)
                using (var after = GraphToolSupport.TryOpen(uiapp, doc, request.Arguments, out _))
                    fresh = after != null && !after.Freshness.Stale;
            if (!fresh)
                lock (AutoBuildBackoff) AutoBuildBackoff[databasePath] = DateTime.UtcNow + AutoBuildBackoffTime;

            if (!build.Success)
                return $"The model graph was {(missing ? "missing" : "stale")} and the automatic build failed: {build.Message}";
            return $"The model graph was {(missing ? "missing and was built" : "stale and was refreshed")} automatically in {build.DurationMs} ms" +
                   (fresh ? "." : "; it still reports stale, so verify ids live.");
        }
        catch (Exception ex)
        {
            return $"The model graph could not be refreshed automatically: {ex.Message}";
        }
    }

    /// <summary>
    /// Answers an unnarrowed read of a large category with the graph route instead of running it. The agent gets
    /// the count and the two calls to make, and can repeat with graphFirst=false to read everything after all.
    /// Null means: run the tool. graph.enforce=false switches the gate off for a user.
    /// </summary>
    public static McpToolResult? TryGate(UIApplication uiapp, McpToolRequest request)
    {
        try
        {
            var read = ReadOf(request);
            if (!GraphRouting.IsGateCandidate(read) || !EnabledForUser("enforce")) return null;
            if (RevitMCP.Addin.Documents.ToolDocument.TargetsOtherDocument(uiapp, request)) return null;   // its graph is not the active one

            var state = ReadState(uiapp, request.Arguments, read.Category);
            var count = GraphRouting.GateCount(read, state);
            if (count == null) return null;
            var linked = state.LinkedCount(read.Category);

            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Status = GraphRouting.GateStatus,
                Message = GraphRouting.GateMessage(read, count.Value, linked),
                Data = new
                {
                    graphFirst = true,
                    read = false,
                    category = read.Category,
                    elementsInGraph = count.Value,
                    elementsInLinks = linked,
                    threshold = GraphRouting.GateThreshold,
                    next = GraphRouting.GateSteps(read).Select(s => new { tool = s.Tool, args = s.Args, why = s.Why }),
                    bypass = new Dictionary<string, object> { [GraphRouting.GateArg] = false },
                    note = GraphSchema.RoutingNote
                }
            };
        }
        catch
        {
            // The gate must never stand between an agent and a live read it cannot evaluate.
            return null;
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>graph.routingHints=false in the user config switches the hints off for that user.</summary>
    private static bool HintsEnabledForUser() => EnabledForUser("routingHints");

    /// <summary>A graph.&lt;key&gt; switch in the user config; on unless it says false.</summary>
    private static bool EnabledForUser(string key)
    {
        try
        {
            var (path, _) = ConfigPathResolver.Resolve(ConfigPathResolver.ScopeUser);
            if (path == null || !File.Exists(path)) return true;
            var (config, _) = new JsonConfigService().Read(path);
            var node = config?["graph"]?[key];
            return node == null || !bool.TryParse(node.ToString(), out var on) || on;
        }
        catch
        {
            return true;
        }
    }
}
