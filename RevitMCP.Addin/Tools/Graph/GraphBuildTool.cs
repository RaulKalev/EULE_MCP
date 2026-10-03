using System.Diagnostics;
using System.Globalization;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Graph;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Graph;

/// <summary>
/// Builds the model routing graph from the open document — a full rebuild, or with incremental=true
/// an update of only the elements changed since the last build in this session (#61).
/// Tool name: revit_graph_build
/// Runs on the Revit API thread; the SQLite file is written locally and then swapped into the shared
/// location atomically. Writes only the graph file, never the model.
/// </summary>
public class GraphBuildTool : IRevitMcpTool
{
    public const int DefaultElementLimit = 250_000;
    public const int MaxElementLimit = 2_000_000;

    public string Name => "revit_graph_build";
    public string Description =>
        "Builds the model knowledge graph for the open document into a per-model SQLite file: " +
        "nodes for elements, types, panels, circuits, rooms/spaces, levels, worksets, sheets and views; " +
        "edges fed_by, located_in, hosted_on, type_of, tagged_in, on_sheet, in_workset, on_level. " +
        "Arguments: incremental (bool, default false — re-extract only elements added/changed/deleted since the last " +
        "build in this Revit session; falls back to a full rebuild when that is not safe, and says why), " +
        "elementLimit (int, default 250000), sharedFolder (string, overrides graph.sharedFolder config), " +
        "dbPath (string, explicit database path). Returns node/edge counts and elapsed time. The graph is a routing layer only.";
    public ToolPermission Permission => ToolPermission.ReadOnly;   // Writes only the local/shared graph file, not the Revit model
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(GraphToolSupport.Fail(request, "No active document."));

        var incremental = ToolArguments.GetBool(request.Arguments, "incremental", false);
        var elementLimit = GraphToolSupport.Clamp(
            ToolArguments.GetInt(request.Arguments, "elementLimit", DefaultElementLimit), 1_000, MaxElementLimit);

        var warnings = new List<string>();

        try
        {
            var (location, signal, _) = GraphToolSupport.Locate(uiapp, doc, request.Arguments);

            string? fallbackReason = null;
            if (incremental)
            {
                try
                {
                    var delta = TryIncremental(doc, location, signal, request, total, warnings, out fallbackReason);
                    if (delta != null) return Task.FromResult(delta);
                }
                catch (Exception ex)
                {
                    fallbackReason = $"incremental update failed ({ex.Message})";
                }
            }

            return Task.FromResult(FullBuild(doc, location, signal, request, elementLimit, incremental, fallbackReason, total, warnings));
        }
        catch (Exception ex)
        {
            total.Stop();
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = "tool_execution_failed",
                Message = $"Graph build failed: {ex.Message}",
                Errors = new List<string> { ex.ToString() },
                Warnings = warnings,
                DurationMs = total.ElapsedMilliseconds
            });
        }
    }

    // ─── Full rebuild ──────────────────────────────────────────────────────

    private static McpToolResult FullBuild(
        Document doc, GraphLocation location, ModelVersionSignal signal, McpToolRequest request,
        int elementLimit, bool incrementalRequested, string? fallbackReason, Stopwatch total, List<string> warnings)
    {
        var extractWatch = Stopwatch.StartNew();
        var extraction = RevitGraphExtractor.Extract(doc, elementLimit);
        extractWatch.Stop();
        warnings.AddRange(extraction.Warnings);

        var builtAt = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var meta = BaseMeta(signal, builtAt, extraction.ElapsedMs);
        meta[GraphSchema.MetaKeys.ElementLimitReached] = extraction.ElementLimitReached ? "true" : "false";
        meta[GraphSchema.MetaKeys.LastFullBuildAt] = builtAt;
        meta[GraphSchema.MetaKeys.IncrementalUpdates] = "0";

        var store = new GraphStore();
        var tempPath = store.CreateBuildTempPath();
        (long Nodes, long Edges) counts;
        var writeWatch = Stopwatch.StartNew();
        using (var db = GraphDatabase.CreateNew(tempPath))
            counts = db.WriteGraph(extraction.Nodes, extraction.Edges, meta);
        writeWatch.Stop();

        var publishWatch = Stopwatch.StartNew();
        store.Publish(tempPath, location.DatabasePath);
        publishWatch.Stop();
        GraphChangeTracker.For(doc).Reset(builtAt, location.DatabasePath);
        total.Stop();

        if (fallbackReason != null)
            warnings.Add($"Incremental update not possible: {fallbackReason}. A full rebuild was performed instead.");

        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Graph built: {counts.Nodes} nodes, {counts.Edges} edges in {total.ElapsedMilliseconds} ms → {location.DatabasePath}",
            Data = new
            {
                databasePath = location.DatabasePath,
                root = location.Root,
                rootSource = location.RootSource,
                mode = "full",
                nodeCount = counts.Nodes,
                edgeCount = counts.Edges,
                nodesByKind = extraction.NodesByKind,
                edgesByRel = extraction.EdgesByRel,
                danglingEdgesDropped = extraction.DanglingEdgesDropped,
                elementLimit,
                elementLimitReached = extraction.ElementLimitReached,
                builtAt,
                builtBy = signal.Username,
                modelName = signal.ModelName,
                modelPath = signal.ModelPath,
                centralVersion = signal.Value,
                versionSource = signal.Source,
                elementCount = signal.ElementCount,
                incremental = new
                {
                    requested = incrementalRequested,
                    applied = false,
                    fallbackReason
                },
                timing = new
                {
                    extractMs = extractWatch.ElapsedMilliseconds,
                    writeMs = writeWatch.ElapsedMilliseconds,
                    publishMs = publishWatch.ElapsedMilliseconds,
                    totalMs = total.ElapsedMilliseconds
                },
                note = GraphSchema.RoutingNote
            },
            Warnings = warnings,
            DurationMs = total.ElapsedMilliseconds
        };
    }

    // ─── Incremental update (#61) ──────────────────────────────────────────

    /// <summary>
    /// Applies the tracked changes to a private copy of the graph and publishes it. Returns null with
    /// <paramref name="fallbackReason"/> set when a full rebuild is needed instead.
    /// </summary>
    private static McpToolResult? TryIncremental(
        Document doc, GraphLocation location, ModelVersionSignal signal, McpToolRequest request,
        Stopwatch total, List<string> warnings, out string? fallbackReason)
    {
        fallbackReason = null;
        var changes = GraphChangeTracker.For(doc);
        if (!File.Exists(location.DatabasePath))
        {
            fallbackReason = GraphIncrementalPlanner.Decide(new IncrementalInputs { GraphExists = false }).Reason;
            return null;
        }

        var store = new GraphStore();
        var tempPath = store.CreateBuildTempPath();
        File.Copy(location.DatabasePath, tempPath, overwrite: true);
        var published = false;
        try
        {
            var extractWatch = Stopwatch.StartNew();
            GraphDeltaResult delta;
            GraphExtractionResult extraction;
            List<string> refreshList, deletedList;
            var trackedAdded = 0;
            string builtAt;
            using (var db = GraphDatabase.OpenReadWrite(tempPath))
            {
                var meta = db.ReadMeta();
                var hasOwners = db.HasEdgeOwners();
                var added = changes.Added.Select(Str).ToList();
                trackedAdded = added.Count;
                var modified = changes.Modified.Select(Str).ToList();
                var deleted = changes.Deleted.Select(Str).ToList();

                var inputs = new IncrementalInputs
                {
                    GraphExists = true,
                    HasEdgeOwners = hasOwners,
                    BaselineMatches = changes.MatchesBaseline(meta.BuiltAt, location.DatabasePath),
                    Overflow = changes.Overflow,
                    ElementLimitReached = string.Equals(meta.Get(GraphSchema.MetaKeys.ElementLimitReached), "true", StringComparison.OrdinalIgnoreCase)
                };
                if (inputs.HasEdgeOwners && inputs.BaselineMatches && !inputs.Overflow && !inputs.ElementLimitReached)
                {
                    var known = db.KindsOf(modified.Concat(deleted));
                    foreach (var id in added)
                        AddUnsafe(inputs, GraphIncrementalPlanner.UnsafeReason(LiveKind(doc, id), added: true, deleted: false));
                    foreach (var id in modified)
                        AddUnsafe(inputs, GraphIncrementalPlanner.UnsafeReason(
                            known.TryGetValue(id, out var k) ? k : LiveKind(doc, id), added: false, deleted: false));
                    foreach (var id in deleted)
                        AddUnsafe(inputs, GraphIncrementalPlanner.UnsafeReason(
                            known.TryGetValue(id, out var k) ? k : null, added: false, deleted: true));
                }

                var decision = GraphIncrementalPlanner.Decide(inputs);
                if (!decision.Incremental)
                {
                    fallbackReason = decision.Reason;
                    return null;
                }

                var kindCache = new Dictionary<string, string?>(StringComparer.Ordinal);
                string? KindOf(string id)
                {
                    if (!kindCache.TryGetValue(id, out var kind))
                        kindCache[id] = kind = db.GetNode(id)?.Kind;
                    return kind;
                }
                var refresh = GraphIncrementalPlanner.ExpandRefreshSet(
                    added.Concat(modified), deleted, KindOf,
                    (id, rel, outgoing) => db.Neighbors(id, rel, outgoing ? "out" : "in", 100_000).Select(n => n.Node.Id));

                extraction = RevitGraphExtractor.ExtractSubset(doc, refresh.Select(long.Parse));
                warnings.AddRange(extraction.Warnings);
                // Ids that were added/changed and then vanished are deletions as far as the graph is concerned.
                var missing = extraction.Missing.Select(Str).ToList();
                deletedList = deleted.Concat(missing).Distinct(StringComparer.Ordinal).ToList();
                refreshList = refresh.Except(missing, StringComparer.Ordinal).ToList();
                extractWatch.Stop();

                builtAt = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                var newMeta = BaseMeta(signal, builtAt, extraction.ElapsedMs);
                long.TryParse(meta.Get(GraphSchema.MetaKeys.IncrementalUpdates), NumberStyles.Integer, CultureInfo.InvariantCulture, out var updates);
                newMeta[GraphSchema.MetaKeys.IncrementalUpdates] = (updates + 1).ToString(CultureInfo.InvariantCulture);

                delta = db.ApplyDelta(refreshList, deletedList, extraction.Nodes, extraction.Edges, newMeta);
            }

            var publishWatch = Stopwatch.StartNew();
            store.Publish(tempPath, location.DatabasePath);
            published = true;
            publishWatch.Stop();
            changes.Reset(builtAt, location.DatabasePath);
            total.Stop();

            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"Graph updated incrementally: {refreshList.Count} refreshed, {deletedList.Count} deleted → " +
                          $"{delta.NodeCount} nodes, {delta.EdgeCount} edges in {total.ElapsedMilliseconds} ms",
                Data = new
                {
                    databasePath = location.DatabasePath,
                    root = location.Root,
                    rootSource = location.RootSource,
                    mode = "incremental",
                    nodeCount = delta.NodeCount,
                    edgeCount = delta.EdgeCount,
                    incremental = new
                    {
                        requested = true,
                        applied = true,
                        trackedAdded,
                        refreshedIds = refreshList.Count,
                        deletedIds = deletedList.Count,
                        nodesUpserted = delta.NodesUpserted,
                        nodesRemoved = delta.NodesRemoved,
                        edgesAdded = delta.EdgesAdded,
                        edgesRemoved = delta.EdgesRemoved,
                        danglingEdgesDropped = delta.DanglingEdgesDropped,
                        typesPruned = delta.TypesPruned
                    },
                    builtAt,
                    builtBy = signal.Username,
                    modelName = signal.ModelName,
                    centralVersion = signal.Value,
                    versionSource = signal.Source,
                    elementCount = signal.ElementCount,
                    timing = new
                    {
                        extractMs = extractWatch.ElapsedMilliseconds,
                        publishMs = publishWatch.ElapsedMilliseconds,
                        totalMs = total.ElapsedMilliseconds
                    },
                    note = GraphSchema.RoutingNote
                },
                Warnings = warnings,
                DurationMs = total.ElapsedMilliseconds
            };
        }
        finally
        {
            if (!published) TryDelete(tempPath);
        }
    }

    private static void AddUnsafe(IncrementalInputs inputs, string? reason)
    {
        if (reason != null) inputs.UnsafeChanges.Add(reason);
    }

    /// <summary>Graph kind of a live element, as far as the safety check cares (level or room/space).</summary>
    private static string? LiveKind(Document doc, string id)
    {
        Element? element;
        try { element = doc.GetElement(new ElementId(long.Parse(id, CultureInfo.InvariantCulture))); } catch { return null; }
        return element switch
        {
            Level => GraphSchema.Kinds.Level,
            Room or Space => GraphSchema.Kinds.Space,
            _ => null
        };
    }

    private static Dictionary<string, string> BaseMeta(ModelVersionSignal signal, string builtAt, long durationMs) =>
        new(StringComparer.Ordinal)
        {
            [GraphSchema.MetaKeys.ModelPath] = signal.ModelPath,
            [GraphSchema.MetaKeys.ModelName] = signal.ModelName,
            [GraphSchema.MetaKeys.BuiltAt] = builtAt,
            [GraphSchema.MetaKeys.CentralVersion] = signal.Value,
            [GraphSchema.MetaKeys.ElementCount] = signal.ElementCount.ToString(CultureInfo.InvariantCulture),
            [GraphSchema.MetaKeys.SchemaVersion] = GraphSchema.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            [GraphSchema.MetaKeys.BuiltBy] = signal.Username,
            [GraphSchema.MetaKeys.VersionSource] = signal.Source,
            [GraphSchema.MetaKeys.IsWorkshared] = signal.IsWorkshared ? "true" : "false",
            [GraphSchema.MetaKeys.RevitVersion] = signal.RevitVersion,
            [GraphSchema.MetaKeys.ProjectKey] = signal.ProjectKey,
            [GraphSchema.MetaKeys.BuildDurationMs] = durationMs.ToString(CultureInfo.InvariantCulture)
        };

    private static string Str(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
