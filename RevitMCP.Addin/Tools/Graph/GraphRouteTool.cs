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
                    levels = state.Levels
                },
                steps = plan.Steps.Select((s, i) => new { step = i + 1, tool = s.Tool, args = s.Args, why = s.Why }).ToList(),
                notes = plan.Notes,
                note = GraphSchema.RoutingNote
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
