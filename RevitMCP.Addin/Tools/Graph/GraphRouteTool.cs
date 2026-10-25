using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Graph;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Graph;

/// <summary>
/// Suggests the cheapest discovery path for an intent (#64): graph summary → narrow ids → live read
/// by id → act. Reads only the graph; when the graph is missing or stale the plan says so and
/// starts with a build.
/// </summary>
public sealed class GraphRouteTool : IRevitMcpTool
{
    public string Name => "revit_graph_route";

    public string Description =>
        "Start here for element discovery. Given an intent in plain words (\"devices in room 1.12\", \"everything fed by panel " +
        "JK-1\", \"fire alarm devices on 2. korrus\"), returns the cheapest call plan: graph steps that narrow ids, then a live " +
        "read by id. Includes the graph's freshness: when it is missing or stale the plan begins with revit_graph_build. Optional " +
        "category, level and name override what is detected in the intent. Read-only; graph data is routing hints, never facts.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        if (uiapp.ActiveUIDocument?.Document == null)
            return Task.FromResult(GraphToolSupport.Fail(request, "No active document."));

        var args = request.Arguments;
        var state = GraphRoutingSupport.ReadState(uiapp, args);
        var plan = GraphRouting.Plan(
            ToolArguments.GetString(args, "intent"),
            state,
            NullIfEmpty(ToolArguments.GetString(args, "category")),
            NullIfEmpty(ToolArguments.GetString(args, "level")),
            NullIfEmpty(ToolArguments.GetString(args, "name")));

        var memory = MemoryHints(uiapp, request, plan.Category);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = plan.Summary,
            Data = new
            {
                route = plan.Route,
                summary = plan.Summary,
                detected = new { category = plan.Category, level = plan.Level, name = plan.NameHint },
                graph = new
                {
                    exists = state.Exists,
                    stale = state.Stale,
                    stale_reason = state.StaleReason,
                    nodesByKind = state.NodesByKind,
                    categories = state.Categories.Take(25).ToList(),
                    // Categories with linked-model elements and how many: host tools cannot read those ids.
                    linkedElementsByCategory = state.LinkedElementsByCategory.Count == 0 ? null
                        : state.LinkedElementsByCategory.OrderByDescending(p => p.Value).Take(25).ToDictionary(p => p.Key, p => p.Value),
                    levels = state.Levels
                },
                steps = plan.Steps.Select((s, i) => new { step = i + 1, tool = s.Tool, args = s.Args, why = s.Why }).ToList(),
                notes = plan.Notes,
                note = GraphSchema.RoutingNote,
                memory
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    /// <summary>
    /// Shared memory entries matching the intent (docs/shared-memory.md, "Recall"), or null when hints are off
    /// (memory.hints), opted out per call (memoryHint=false), or nothing matches. Never fails the route.
    /// </summary>
    private static object? MemoryHints(UIApplication uiapp, McpToolRequest request, string? category)
    {
        try
        {
            var service = RevitMCP.Addin.Memory.Hosting.MemoryService.Current;
            if (service == null || !ToolArguments.GetBool(request.Arguments, "memoryHint", true) || !service.Settings.Hints) return null;
            var context = Tools.Memory.MemoryToolSupport.Context(uiapp, request, service, out _);
            if (context == null) return null;
            var query = (ToolArguments.GetString(request.Arguments, "intent") + " " + category).Trim();
            var hints = service.Hints(context, query);
            if (hints == null || hints.Hits.Count == 0) return null;
            return new
            {
                note = RevitMCP.Addin.Memory.MemoryRecall.Note,
                entries = hints.Hits.Select(h => Tools.Memory.MemoryToolSupport.Dto(h, false)).ToList()
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
