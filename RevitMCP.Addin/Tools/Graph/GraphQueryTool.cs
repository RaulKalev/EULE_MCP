using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Graph;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;
using RevitMCP.Core.Safety;

namespace RevitMCP.Addin.Tools.Graph;

/// <summary>
/// Structured graph queries (no raw SQL): neighbors, find, path, subtree.
/// Tool name: revit_graph_query
/// </summary>
public class GraphQueryTool : IRevitMcpTool
{
    public const int DefaultNeighborLimit = 100;
    public const int MaxNeighborLimit = 500;
    public const int DefaultMaxHops = 6;
    public const int MaxMaxHops = 10;
    public const int DefaultDepth = 3;
    public const int MaxDepth = 10;
    public const int DefaultSubtreeNodes = 500;
    public const int MaxSubtreeNodes = 2_000;

    private static readonly string[] Operations = { "neighbors", "find", "path", "subtree" };

    public string Name => "revit_graph_query";
    public string Description =>
        "Structured queries against the model graph (build it first with revit_graph_build). operation: " +
        "neighbors (id, rel?, direction? in|out|both, limit?) | " +
        "find (kind?, category?, level?, workset?, nameContains?, page?, pageSize?) → ids + names, paginated | " +
        "path (fromId, toId, maxHops?) → shortest undirected path | " +
        "subtree (id, rel, depth?, direction? in|out, maxNodes?) e.g. everything fed_by a panel. " +
        "Kinds: element, type, panel, circuit, space, level, workset, sheet, view. " +
        "Rels: fed_by, located_in, hosted_on, type_of, tagged_in, on_sheet, in_workset, on_level. " +
        "Returns ids and structure only — fetch live values by id afterwards.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(GraphToolSupport.Fail(request, "No active document."));

        var args = request.Arguments;
        var operation = ToolArguments.GetString(args, "operation").Trim().ToLowerInvariant();
        if (Array.IndexOf(Operations, operation) < 0)
        {
            return Task.FromResult(GraphToolSupport.Fail(request,
                $"Unknown operation '{operation}'. Valid operations: {string.Join(", ", Operations)}.", "validation_failed"));
        }

        var rel = ToolArguments.GetString(args, "rel").Trim().ToLowerInvariant();
        if (rel.Length > 0 && !GraphSchema.IsRel(rel))
        {
            return Task.FromResult(GraphToolSupport.Fail(request,
                $"Unknown rel '{rel}'. Valid rels: {string.Join(", ", GraphSchema.Rels.All)}.", "validation_failed"));
        }

        var kind = ToolArguments.GetString(args, "kind").Trim().ToLowerInvariant();
        if (kind.Length > 0 && !GraphSchema.IsKind(kind))
        {
            return Task.FromResult(GraphToolSupport.Fail(request,
                $"Unknown kind '{kind}'. Valid kinds: {string.Join(", ", GraphSchema.Kinds.All)}.", "validation_failed"));
        }

        var opened = GraphToolSupport.TryOpen(uiapp, doc, args, out var openError);
        if (opened == null)
            return Task.FromResult(GraphToolSupport.Fail(request, openError));

        using (opened)
        {
            object payload;
            string message;
            switch (operation)
            {
                case "neighbors":
                {
                    var id = ToolArguments.GetString(args, "id").Trim();
                    if (id.Length == 0) return Task.FromResult(GraphToolSupport.Fail(request, "neighbors requires 'id'.", "validation_failed"));
                    var node = opened.Database.GetNode(id);
                    if (node == null) return Task.FromResult(GraphToolSupport.Fail(request, $"Node '{id}' is not in the graph. Rebuild the graph if the element is new.", "validation_failed"));

                    var direction = ToolArguments.GetString(args, "direction", "both");
                    var limit = GraphToolSupport.Clamp(ToolArguments.GetInt(args, "limit", DefaultNeighborLimit), 1, MaxNeighborLimit);
                    var neighbors = opened.Database.Neighbors(id, rel.Length > 0 ? rel : null, direction, limit);
                    payload = new
                    {
                        operation,
                        node = GraphToolSupport.NodeDto(node),
                        rel = rel.Length > 0 ? rel : null,
                        direction,
                        limit,
                        count = neighbors.Count,
                        truncated = neighbors.Count >= limit,
                        neighbors = neighbors.Select(n => new
                        {
                            rel = n.Rel,
                            direction = n.Direction,
                            id = n.Node.Id,
                            kind = n.Node.Kind,
                            name = n.Node.Name,
                            category = NullIfEmpty(n.Node.Category),
                            level = NullIfEmpty(n.Node.Level),
                            workset = NullIfEmpty(n.Node.Workset),
                            extra = GraphToolSupport.ParseExtra(n.Node.Extra)
                        }).ToList()
                    };
                    message = $"{neighbors.Count} neighbor(s) of '{node.Name}' ({node.Kind} {node.Id}).";
                    break;
                }
                case "find":
                {
                    var category = ToolArguments.GetString(args, "category");
                    var level = ToolArguments.GetString(args, "level");
                    var workset = ToolArguments.GetString(args, "workset");
                    var nameContains = ToolArguments.GetString(args, "nameContains");
                    var page = Math.Max(0, ToolArguments.GetInt(args, "page", 0));
                    var pageSize = QueryGuard.NormalizePageSize(ToolArguments.GetInt(args, "pageSize", 0));

                    var result = opened.Database.Find(
                        NullIfEmpty(kind), NullIfEmpty(category), NullIfEmpty(level), NullIfEmpty(workset), NullIfEmpty(nameContains),
                        page, pageSize);
                    payload = new
                    {
                        operation,
                        filters = new
                        {
                            kind = NullIfEmpty(kind),
                            category = NullIfEmpty(category),
                            level = NullIfEmpty(level),
                            workset = NullIfEmpty(workset),
                            nameContains = NullIfEmpty(nameContains)
                        },
                        itemsReturned = result.ItemsReturned,
                        totalAvailable = result.TotalAvailable,
                        page = result.Page,
                        pageSize = result.PageSize,
                        hasMore = result.HasMore,
                        nextPage = result.HasMore ? result.Page + 1 : (int?)null,
                        items = result.Items.Select(GraphToolSupport.NodeDto).ToList()
                    };
                    message = $"{result.ItemsReturned} of {result.TotalAvailable} matching node(s) (page {result.Page}, pageSize {result.PageSize}).";
                    break;
                }
                case "path":
                {
                    var fromId = ToolArguments.GetString(args, "fromId").Trim();
                    var toId = ToolArguments.GetString(args, "toId").Trim();
                    if (fromId.Length == 0 || toId.Length == 0)
                        return Task.FromResult(GraphToolSupport.Fail(request, "path requires 'fromId' and 'toId'.", "validation_failed"));
                    var maxHops = GraphToolSupport.Clamp(ToolArguments.GetInt(args, "maxHops", DefaultMaxHops), 1, MaxMaxHops);

                    var result = opened.Database.FindPath(fromId, toId, maxHops);
                    payload = new
                    {
                        operation,
                        fromId,
                        toId,
                        maxHops,
                        found = result.Found,
                        hops = result.Hops,
                        visitedNodes = result.VisitedNodes,
                        searchTruncated = result.SearchTruncated,
                        steps = result.Steps.Select(s => new
                        {
                            id = s.Node.Id,
                            kind = s.Node.Kind,
                            name = s.Node.Name,
                            category = NullIfEmpty(s.Node.Category),
                            level = NullIfEmpty(s.Node.Level),
                            rel = s.Rel,
                            direction = s.Direction
                        }).ToList()
                    };
                    message = result.Found
                        ? $"Path found in {result.Hops} hop(s)."
                        : result.SearchTruncated
                            ? $"No path found within the search budget ({result.VisitedNodes} nodes visited); try a smaller maxHops or a closer start node."
                            : $"No path within {maxHops} hop(s) (or one of the ids is not in the graph).";
                    break;
                }
                default: // subtree
                {
                    var id = ToolArguments.GetString(args, "id").Trim();
                    if (id.Length == 0) return Task.FromResult(GraphToolSupport.Fail(request, "subtree requires 'id'.", "validation_failed"));
                    if (rel.Length == 0) return Task.FromResult(GraphToolSupport.Fail(request, "subtree requires 'rel'.", "validation_failed"));
                    var depth = GraphToolSupport.Clamp(ToolArguments.GetInt(args, "depth", DefaultDepth), 1, MaxDepth);
                    var maxNodes = GraphToolSupport.Clamp(ToolArguments.GetInt(args, "maxNodes", DefaultSubtreeNodes), 1, MaxSubtreeNodes);
                    var direction = ToolArguments.GetString(args, "direction", "in");

                    var result = opened.Database.Subtree(id, rel, depth, direction, maxNodes);
                    if (result.Root == null)
                        return Task.FromResult(GraphToolSupport.Fail(request, $"Node '{id}' is not in the graph. Rebuild the graph if the element is new.", "validation_failed"));

                    payload = new
                    {
                        operation,
                        root = GraphToolSupport.NodeDto(result.Root),
                        rel,
                        direction,
                        depth,
                        maxNodes,
                        count = result.Nodes.Count,
                        maxDepthReached = result.MaxDepthReached,
                        truncated = result.Truncated,
                        nodes = result.Nodes.Select(n => new
                        {
                            id = n.Node.Id,
                            kind = n.Node.Kind,
                            name = n.Node.Name,
                            category = NullIfEmpty(n.Node.Category),
                            level = NullIfEmpty(n.Node.Level),
                            workset = NullIfEmpty(n.Node.Workset),
                            depth = n.Depth,
                            parentId = n.ParentId
                        }).ToList()
                    };
                    message = $"{result.Nodes.Count} node(s) reachable via {rel} ({direction}) from '{result.Root.Name}' within depth {depth}" +
                              (result.Truncated ? " (truncated at maxNodes)." : ".");
                    break;
                }
            }

            sw.Stop();
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = message,
                Data = GraphToolSupport.WithEnvelope(payload, opened.Meta, opened.Freshness),
                Warnings = opened.Freshness.Stale
                    ? new List<string> { "Graph is stale: " + opened.Freshness.Reason + " Re-verify ids against the live model." }
                    : new List<string>(),
                DurationMs = sw.ElapsedMilliseconds
            });
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
