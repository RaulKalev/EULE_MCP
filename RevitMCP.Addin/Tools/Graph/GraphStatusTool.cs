using System.Diagnostics;
using System.IO;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Graph;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Graph;

/// <summary>
/// Reports whether a graph exists for the open model, where it is, when it was built and
/// whether it still matches the document.
/// Tool name: revit_graph_status
/// </summary>
public class GraphStatusTool : IRevitMcpTool
{
    public string Name => "revit_graph_status";
    public string Description =>
        "Reports whether a model graph exists for the open document: database path, built_at, central_version, " +
        "the current document version, and stale (true/false) with a human-readable reason. " +
        "Optional: sharedFolder, dbPath (same overrides as revit_graph_build). Graph values are for routing only.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(GraphToolSupport.Fail(request, "No active document."));

        var (location, signal, configured) = GraphToolSupport.Locate(uiapp, doc, request.Arguments);
        var howToConfigure =
            $"Set the shared folder with config_update scope=user (or company) updates={{\"$.{GraphSchema.SharedFolderConfigKey}\": \"<folder>\"}}; " +
            "unset → local fallback under %LOCALAPPDATA%\\RKTools\\RevitMCP\\Graph.";

        if (!File.Exists(location.DatabasePath))
        {
            var missing = GraphFreshness.Evaluate(null, signal);
            sw.Stop();
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = "No graph has been built for this model yet.",
                Data = GraphToolSupport.WithEnvelope(new
                {
                    exists = false,
                    databasePath = location.DatabasePath,
                    root = location.Root,
                    rootSource = location.RootSource,
                    configuredSharedFolder = configured,
                    howToConfigure,
                    modelName = signal.ModelName,
                    modelPath = signal.ModelPath,
                    currentVersion = signal.Value,
                    versionSource = signal.Source,
                    currentElementCount = signal.ElementCount
                }, null, missing),
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        var store = new GraphStore();
        var handle = store.OpenForRead(location.DatabasePath);
        GraphMeta meta;
        using (var db = GraphDatabase.OpenReadOnly(handle.ReadPath))
            meta = db.ReadMeta();
        var freshness = GraphFreshness.Evaluate(meta, signal);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = freshness.Stale
                ? $"Graph exists but is stale: {freshness.Reason}"
                : "Graph exists and matches the current document.",
            Data = GraphToolSupport.WithEnvelope(new
            {
                exists = true,
                databasePath = location.DatabasePath,
                readFrom = handle.ReadPath,
                usedLocalCache = handle.UsedLocalCache,
                root = location.Root,
                rootSource = location.RootSource,
                configuredSharedFolder = configured,
                howToConfigure,
                builtBy = meta.BuiltBy,
                modelName = meta.ModelName,
                modelPath = meta.ModelPath,
                currentModelName = signal.ModelName,
                currentVersion = signal.Value,
                versionSource = signal.Source,
                storedVersionSource = meta.VersionSource,
                storedElementCount = meta.ElementCount,
                currentElementCount = signal.ElementCount,
                nodeCount = meta.NodeCount,
                edgeCount = meta.EdgeCount,
                schemaVersion = meta.SchemaVersion,
                expectedSchemaVersion = GraphSchema.SchemaVersion
            }, meta, freshness),
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}
