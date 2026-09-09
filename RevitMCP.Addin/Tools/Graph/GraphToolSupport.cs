using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Graph;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Graph;

/// <summary>
/// Shared plumbing for the revit_graph_* tools: locating the database for the open document,
/// opening it safely for reading, and the freshness envelope every read response carries.
/// </summary>
internal static class GraphToolSupport
{
    public const string ArgDbPath = "dbPath";
    public const string ArgSharedFolder = "sharedFolder";

    /// <summary>A graph opened read-only for the current document, with its freshness evaluated.</summary>
    public sealed class OpenedGraph : IDisposable
    {
        public GraphLocation Location { get; set; } = new();
        public ModelVersionSignal Signal { get; set; } = new();
        public GraphReadHandle ReadHandle { get; set; } = new();
        public GraphDatabase Database { get; set; } = null!;
        public GraphMeta Meta { get; set; } = new();
        public GraphFreshnessResult Freshness { get; set; } = new();

        public void Dispose() => Database?.Dispose();
    }

    public static McpToolResult Fail(McpToolRequest request, string message, string? status = null) =>
        new() { RequestId = request.RequestId, Success = false, Message = message, Status = status };

    /// <summary>Resolves where this document's graph lives. Reads the Revit document (API thread).</summary>
    public static (GraphLocation Location, ModelVersionSignal Signal, string? ConfiguredSharedFolder) Locate(
        UIApplication uiapp, Document doc, Dictionary<string, object?> args)
    {
        var signal = RevitModelVersionReader.Read(uiapp, doc);
        var dbPath = ToolArguments.GetString(args, ArgDbPath);
        var sharedFolder = ToolArguments.GetString(args, ArgSharedFolder);
        var (configured, configuredSource) = GraphPathResolver.LoadConfiguredSharedFolder();
        var location = GraphPathResolver.Resolve(
            dbPath, sharedFolder, configured, configuredSource, signal.ProjectKey, signal.ModelName);
        return (location, signal, configured);
    }

    /// <summary>
    /// Opens the document's graph read-only (through the local cache when the file is not local)
    /// and evaluates freshness. Returns null with <paramref name="error"/> set when no graph exists.
    /// </summary>
    public static OpenedGraph? TryOpen(UIApplication uiapp, Document doc, Dictionary<string, object?> args, out string error)
    {
        var (location, signal, _) = Locate(uiapp, doc, args);
        if (!File.Exists(location.DatabasePath))
        {
            error = $"No graph database exists for this model at '{location.DatabasePath}' ({location.RootSource}). Run revit_graph_build first.";
            return null;
        }

        var store = new GraphStore();
        var handle = store.OpenForRead(location.DatabasePath);
        var db = GraphDatabase.OpenReadOnly(handle.ReadPath);
        try
        {
            var meta = db.ReadMeta();
            var freshness = GraphFreshness.Evaluate(meta, signal);
            error = string.Empty;
            return new OpenedGraph
            {
                Location = location,
                Signal = signal,
                ReadHandle = handle,
                Database = db,
                Meta = meta,
                Freshness = freshness
            };
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Freshness fields attached to every status/query/summary response. Key names follow the
    /// graph contract (built_at, central_version, stale, note) rather than the camelCase used by
    /// the rest of the payload, so agents can look for them verbatim.
    /// </summary>
    public static JObject Envelope(GraphMeta? meta, GraphFreshnessResult freshness) => new()
    {
        ["built_at"] = meta?.BuiltAt,
        ["central_version"] = meta?.CentralVersion,
        ["stale"] = freshness.Stale,
        ["stale_reason"] = freshness.Reason,
        ["note"] = GraphSchema.RoutingNote
    };

    /// <summary>Merges the freshness envelope into a payload object so it sits at the top level of Data.</summary>
    public static JObject WithEnvelope(object payload, GraphMeta? meta, GraphFreshnessResult freshness)
    {
        var body = JObject.FromObject(payload);
        body.Merge(Envelope(meta, freshness));
        return body;
    }

    public static object NodeDto(GraphNode node) => new
    {
        id = node.Id,
        kind = node.Kind,
        name = node.Name,
        category = string.IsNullOrEmpty(node.Category) ? null : node.Category,
        level = string.IsNullOrEmpty(node.Level) ? null : node.Level,
        workset = string.IsNullOrEmpty(node.Workset) ? null : node.Workset,
        extra = ParseExtra(node.Extra)
    };

    public static JObject? ParseExtra(string? extra)
    {
        if (string.IsNullOrWhiteSpace(extra)) return null;
        try { return JObject.Parse(extra!); }
        catch { return null; }
    }

    public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
}
