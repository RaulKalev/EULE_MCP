using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Checkpoints;
using RevitMCP.Addin.Documents;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Checkpoints;

/// <summary>Shared lookups for the checkpoint tools (#98): target document, store, checkpoint, live diff.</summary>
internal static class CheckpointSupport
{
    public const string NotFound = "checkpoint_not_found";
    public const string OtherDocument = "checkpoint_other_document";
    public const string SnapshotTooLarge = "snapshot_too_large";

    public static McpToolResult Fail(McpToolRequest request, string message, string status = "validation_failed", object? data = null)
        => new() { RequestId = request.RequestId, Success = false, Status = status, Message = message, Data = data };

    /// <summary>The <c>document</c> target (title or path; empty = active) and its checkpoint store.</summary>
    public static RevitDocumentResolver.DocumentTarget? Resolve(
        UIApplication uiapp, McpToolRequest request, out CheckpointStore? store, out McpToolResult? failure)
    {
        store = CheckpointChangeTracker.Store;
        if (store == null)
        {
            failure = Fail(request, "Checkpoints are not available: the connector's checkpoint store did not start. See RevitMCP_startup.log.", "tool_execution_failed");
            return null;
        }
        var target = RevitDocumentResolver.Resolve(uiapp, request, out var error);
        failure = target == null ? Fail(request, error ?? "No open project document.") : null;
        return target;
    }

    /// <summary>The checkpoint named by <c>checkpointId</c> in this document, or a failure naming why not.</summary>
    public static Checkpoint? Find(CheckpointStore store, Document doc, McpToolRequest request, out McpToolResult? failure)
    {
        var id = ToolArguments.GetString(request.Arguments, "checkpointId").Trim();
        if (!CheckpointIds.IsValid(id))
        {
            failure = Fail(request, "checkpointId is required (\"cp-\" + 6 hex, from revit_checkpoint_create or revit_checkpoint_list).");
            return null;
        }
        var modelId = CheckpointChangeTracker.ModelIdOf(doc);
        var checkpoint = store.Find(modelId, id);
        if (checkpoint != null)
        {
            failure = null;
            return checkpoint;
        }
        var other = store.FindModelOf(id);
        failure = other != null
            ? Fail(request, $"Checkpoint {id} belongs to another document than '{doc.Title}'. Pass document=<its title> or activate it.", OtherDocument)
            : Fail(request, $"Checkpoint {id} was not found. revit_checkpoint_list shows the checkpoints of the open documents.", NotFound);
        return null;
    }

    /// <summary>Re-reads the snapshotted elements and the document's warnings, and builds the diff.</summary>
    public static (CheckpointDiff Diff, WarningDiffResult Warnings, int UnreadableWarnings) ComputeDiff(Document doc, Checkpoint checkpoint)
    {
        var reader = new CheckpointSnapshotReader(doc);
        var scopeParameters = checkpoint.Scope?.Parameters ?? new List<string>();
        var deltas = SnapshotDiff.CompareAll(checkpoint.Snapshot, id =>
        {
            var before = checkpoint.Snapshot[id];
            var names = before.Parameters.Keys.Union(scopeParameters, StringComparer.OrdinalIgnoreCase).ToList();
            return reader.Read(id, names);
        });
        var diff = CheckpointDiffBuilder.Build(checkpoint, deltas);
        var now = RevitWarningReader.Read(doc, out var unreadable);
        return (diff, WarningDiff.Compare(checkpoint.Warnings, now), unreadable);
    }

    /// <summary>Restart / tracking-cap caveat for results built on a checkpoint.</summary>
    public static void AddTrackingWarning(Checkpoint checkpoint, McpToolResult result)
    {
        if (!checkpoint.TrackingComplete)
            result.Warnings.Add(checkpoint.TrackingNote ?? CheckpointStore.RestartNote);
    }
}
