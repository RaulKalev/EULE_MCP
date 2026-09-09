using System.Diagnostics;
using System.Globalization;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Graph;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Graph;

/// <summary>
/// Full rebuild of the model routing graph from the open document.
/// Tool name: revit_graph_build
/// Runs on the Revit API thread (single collector pass); the SQLite file is written locally and
/// then swapped into the shared location atomically. Writes only the graph file, never the model.
/// </summary>
public class GraphBuildTool : IRevitMcpTool
{
    public const int DefaultElementLimit = 250_000;
    public const int MaxElementLimit = 2_000_000;

    public string Name => "revit_graph_build";
    public string Description =>
        "Builds (full rebuild) the model knowledge graph for the open document into a per-model SQLite file: " +
        "nodes for elements, types, panels, circuits, rooms/spaces, levels, worksets, sheets and views; " +
        "edges fed_by, located_in, hosted_on, type_of, tagged_in, on_sheet, in_workset, on_level. " +
        "Arguments: incremental (bool, not implemented yet — falls back to full), elementLimit (int, default 250000), " +
        "sharedFolder (string, overrides graph.sharedFolder config), dbPath (string, explicit database path). " +
        "Returns node/edge counts and elapsed time. The graph is a routing layer only.";
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
        if (incremental)
            warnings.Add("incremental=true is not implemented in this version; a full rebuild was performed instead.");

        try
        {
            var (location, signal, _) = GraphToolSupport.Locate(uiapp, doc, request.Arguments);

            var extractWatch = Stopwatch.StartNew();
            var extraction = RevitGraphExtractor.Extract(doc, elementLimit);
            extractWatch.Stop();
            warnings.AddRange(extraction.Warnings);

            var builtAt = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var meta = new Dictionary<string, string>(StringComparer.Ordinal)
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
                [GraphSchema.MetaKeys.BuildDurationMs] = extraction.ElapsedMs.ToString(CultureInfo.InvariantCulture)
            };

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
            total.Stop();

            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"Graph built: {counts.Nodes} nodes, {counts.Edges} edges in {total.ElapsedMilliseconds} ms → {location.DatabasePath}",
                Data = new
                {
                    databasePath = location.DatabasePath,
                    root = location.Root,
                    rootSource = location.RootSource,
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
                        requested = incremental,
                        applied = false,
                        note = "Incremental builds are not implemented yet; every build is a full rebuild."
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
            });
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
}
