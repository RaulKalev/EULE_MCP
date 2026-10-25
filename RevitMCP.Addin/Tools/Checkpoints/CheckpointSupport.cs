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

    /// <summary>
    /// Snapshots a scope, reads the warnings and stores a new checkpoint (revit_checkpoint_create, plan activation).
    /// Null with <paramref name="failure"/> for a bad scope or one over QueryLimits.MaxScanElements.
    /// </summary>
    public static Checkpoint? Create(
        UIApplication uiapp,
        RevitDocumentResolver.DocumentTarget target,
        CheckpointStore store,
        SnapshotScope scope,
        string title,
        string client,
        McpToolRequest request,
        CancellationToken cancellationToken,
        out List<Checkpoint> evicted,
        out int unreadableWarnings,
        out McpToolResult? failure)
    {
        evicted = new List<Checkpoint>();
        unreadableWarnings = 0;
        var doc = target.Document;
        var snapshot = new Dictionary<long, ElementSnapshot>();
        if (!scope.IsEmpty)
        {
            var ids = CheckpointSnapshotReader.ResolveScope(uiapp, doc, target.IsActive, scope, out var scopeError);
            if (ids == null)
            {
                failure = Fail(request, scopeError!);
                return null;
            }
            var cap = RevitMCP.Core.Safety.QueryLimits.Default.MaxScanElements;
            if (ids.Count > cap)
            {
                failure = Fail(request,
                    $"The scope resolves to {ids.Count:N0} elements; a snapshot holds at most {cap:N0}. Narrow it (elementIds, fewer categories, a view), or create the checkpoint without a scope to track change ids and warnings only.",
                    SnapshotTooLarge, new { elements = ids.Count, cap });
                return null;
            }

            var reader = new CheckpointSnapshotReader(doc);
            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snap = reader.Read(id, scope.Parameters.Count > 0 ? scope.Parameters : null);
                if (snap != null) snapshot[id] = snap;
            }
            // Selection and views are resolved now; store the ids so a diff after a restart covers the same elements.
            scope.ElementIds = snapshot.Keys.OrderBy(i => i).ToList();
        }

        var checkpoint = new Checkpoint
        {
            Id = CheckpointIds.NewId(),
            Title = CheckpointStore.NormalizeTitle(title),
            ModelId = CheckpointChangeTracker.ModelIdOf(doc),
            DocumentTitle = doc.Title,
            CreatedAt = DateTimeOffset.Now,
            DocumentVersion = RevitMCP.Addin.Approval.DocumentChangeTracker.Capture(doc),
            CreatedBy = client,
            Warnings = RevitWarningReader.Read(doc, out unreadableWarnings),
            Scope = scope.IsEmpty ? null : scope,
            Snapshot = snapshot
        };

        try { evicted = store.Add(checkpoint); }
        catch (ArgumentException ex)
        {
            failure = Fail(request, ex.Message, "tool_execution_failed");
            return null;
        }
        failure = null;
        return checkpoint;
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
