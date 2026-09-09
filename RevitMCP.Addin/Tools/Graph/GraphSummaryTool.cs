using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Graph;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Graph;

/// <summary>
/// Cheap orientation for session start: counts per kind/category/level/workset/panel plus
/// orphan circuits and elements without a room/space.
/// Tool name: revit_graph_summary
/// </summary>
public class GraphSummaryTool : IRevitMcpTool
{
    public const int DefaultTopN = 25;
    public const int MaxTopN = 200;

    public string Name => "revit_graph_summary";
    public string Description =>
        "Summarises the model graph: node counts per kind, element counts per category/level/workset, panels with " +
        "circuit and fed-element counts, orphan circuits (no panel / no elements) and elements with no located_in edge. " +
        "Arguments: topN (default 25), sampleSize (default 25), sharedFolder, dbPath. Cheap orientation for session start; " +
        "values are for routing only.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(GraphToolSupport.Fail(request, "No active document."));

        var topN = GraphToolSupport.Clamp(ToolArguments.GetInt(request.Arguments, "topN", DefaultTopN), 1, MaxTopN);
        var sampleSize = GraphToolSupport.Clamp(ToolArguments.GetInt(request.Arguments, "sampleSize", DefaultTopN), 0, MaxTopN);

        var opened = GraphToolSupport.TryOpen(uiapp, doc, request.Arguments, out var openError);
        if (opened == null)
            return Task.FromResult(GraphToolSupport.Fail(request, openError));

        using (opened)
        {
            var s = opened.Database.Summarize(topN, sampleSize);
            var payload = new
            {
                databasePath = opened.Location.DatabasePath,
                nodeCount = opened.Meta.NodeCount,
                edgeCount = opened.Meta.EdgeCount,
                topN,
                nodesByKind = s.NodesByKind.ToDictionary(c => c.Key, c => c.Count),
                edgesByRel = s.EdgesByRel.ToDictionary(c => c.Key, c => c.Count),
                elementsByCategory = s.ElementsByCategory.Select(c => new { category = Label(c.Key), count = c.Count }).ToList(),
                elementsByLevel = s.ElementsByLevel.Select(c => new { level = Label(c.Key), count = c.Count }).ToList(),
                elementsByWorkset = s.ElementsByWorkset.Select(c => new { workset = Label(c.Key), count = c.Count }).ToList(),
                panels = s.Panels.Select(p => new
                {
                    id = p.Id,
                    name = p.Name,
                    level = string.IsNullOrEmpty(p.Level) ? null : p.Level,
                    circuitCount = p.CircuitCount,
                    fedElementCount = p.FedElementCount
                }).ToList(),
                orphanCircuits = new
                {
                    withoutPanelCount = s.CircuitsWithoutPanelCount,
                    withoutPanel = s.CircuitsWithoutPanel.Select(GraphToolSupport.NodeDto).ToList(),
                    withoutElementsCount = s.CircuitsWithoutElementsCount,
                    withoutElements = s.CircuitsWithoutElements.Select(GraphToolSupport.NodeDto).ToList()
                },
                elementsWithoutLocation = new
                {
                    count = s.ElementsWithoutLocationCount,
                    sample = s.ElementsWithoutLocation.Select(GraphToolSupport.NodeDto).ToList()
                }
            };

            sw.Stop();
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"Graph summary: {opened.Meta.NodeCount ?? 0} nodes, {opened.Meta.EdgeCount ?? 0} edges, " +
                          $"{s.Panels.Count} panel(s) listed, {s.CircuitsWithoutPanelCount + s.CircuitsWithoutElementsCount} orphan circuit(s), " +
                          $"{s.ElementsWithoutLocationCount} element(s) without a room/space.",
                Data = GraphToolSupport.WithEnvelope(payload, opened.Meta, opened.Freshness),
                Warnings = opened.Freshness.Stale
                    ? new List<string> { "Graph is stale: " + opened.Freshness.Reason + " Re-verify ids against the live model." }
                    : new List<string>(),
                DurationMs = sw.ElapsedMilliseconds
            });
        }
    }

    private static string Label(string key) => string.IsNullOrEmpty(key) ? "(none)" : key;
}
