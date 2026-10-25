using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Approval;
using RevitMCP.Addin.Checkpoints;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;
using RevitMCP.Core.Safety;

namespace RevitMCP.Addin.Tools.Checkpoints;

/// <summary>revit_get_warnings (#98): Revit's own warnings, grouped or listed, filtered, paged.</summary>
public sealed class GetWarningsTool : IRevitMcpTool
{
    public string Name => "revit_get_warnings";
    public string Description =>
        "Lists Revit's own model warnings and errors (the Review Warnings list) with their element ids, grouped by " +
        "description (groupBy=none lists each warning). Filters: severity (all|warning|error), containsText, elementIds, " +
        "categories, sinceCheckpointId (only warnings that are new since that checkpoint). Use it to see what Revit " +
        "complains about after a batch of writes.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Reports;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = CheckpointSupport.Resolve(uiapp, request, out var store, out var failure);
        if (target == null) return Task.FromResult(failure!);
        var doc = target.Document;
        var args = request.Arguments;

        var all = RevitWarningReader.Read(doc, out var unreadable);
        IEnumerable<WarningKey> warnings = all;

        Checkpoint? since = null;
        if (!string.IsNullOrWhiteSpace(ToolArguments.GetString(args, "sinceCheckpointId")))
        {
            var sinceRequest = new McpToolRequest { RequestId = request.RequestId, Arguments = new() { ["checkpointId"] = ToolArguments.GetString(args, "sinceCheckpointId") } };
            since = CheckpointSupport.Find(store!, doc, sinceRequest, out failure);
            if (since == null) return Task.FromResult(failure!);
            warnings = WarningDiff.Since(since.Warnings, all);
        }

        var severity = ToolArguments.GetString(args, "severity", "all");
        warnings = warnings.Where(w => WarningDiff.MatchesSeverity(w, severity));

        var text = ToolArguments.GetString(args, "containsText").Trim();
        if (text.Length > 0)
            warnings = warnings.Where(w => w.Description.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);

        var ids = ToolArguments.GetLongArray(args, "elementIds").ToHashSet();
        if (ids.Count > 0) warnings = warnings.Where(w => w.ElementIds.Any(ids.Contains));

        var categories = ToolArguments.GetStringArray(args, "categories").Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        var categoryOf = new Dictionary<long, string?>();
        string? CategoryOf(long id)
        {
            if (!categoryOf.TryGetValue(id, out var name))
            {
                try { name = doc.GetElement(new ElementId(id))?.Category?.Name; } catch { name = null; }
                categoryOf[id] = name;
            }
            return name;
        }
        if (categories.Count > 0)
            warnings = warnings.Where(w => w.ElementIds.Any(id => CategoryOf(id) is { } c &&
                                                                  categories.Any(want => string.Equals(want.Trim(), c, StringComparison.OrdinalIgnoreCase))));

        var filtered = warnings.ToList();
        var pageSize = QueryGuard.NormalizePageSize(ToolArguments.GetInt(args, "pageSize", 0) is var ps and > 0 ? ps : null);
        var page = Math.Max(1, ToolArguments.GetInt(args, "page", 1));
        var includeNames = ToolArguments.GetBool(args, "includeElementNames");
        var groupBy = ToolArguments.GetString(args, "groupBy", "description");

        object rows;
        int totalRows;
        if (string.Equals(groupBy, "none", StringComparison.OrdinalIgnoreCase))
        {
            var ordered = filtered.OrderBy(w => WarningDiff.SeverityRank(w.Severity)).ThenBy(w => w.Description, StringComparer.Ordinal).ToList();
            totalRows = ordered.Count;
            rows = ordered.Skip((page - 1) * pageSize).Take(pageSize).Select(w => new
            {
                description = w.Description,
                severity = w.Severity,
                elementIds = w.ElementIds,
                names = includeNames ? w.ElementIds.Select(id => NameOf(doc, id)).ToList() : null
            }).ToList();
        }
        else
        {
            var groups = WarningDiff.Group(filtered);
            totalRows = groups.Count;
            rows = groups.Skip((page - 1) * pageSize).Take(pageSize).Select(g => new
            {
                description = g.Description,
                severity = g.Severity,
                count = g.Count,
                elementIds = g.ElementIds,
                elementIdCount = g.ElementIdCount,
                sample = g.Sample.Select(s => new
                {
                    ids = s,
                    names = includeNames ? s.Select(id => NameOf(doc, id)).ToList() : null
                }).ToList()
            }).ToList();
        }

        var result = new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{filtered.Count} warning(s){(since != null ? $" new since {since.Id}" : string.Empty)} in '{doc.Title}' " +
                      $"({filtered.Count(WarningDiff.IsErrorSeverity)} error(s)). For a long list, publish it with revit_publish_page.",
            Data = new
            {
                document = doc.Title,
                total = filtered.Count,
                totalInModel = all.Count,
                bySeverity = new
                {
                    warning = filtered.Count(w => !WarningDiff.IsErrorSeverity(w)),
                    error = filtered.Count(WarningDiff.IsErrorSeverity)
                },
                unreadable,
                groupBy = string.Equals(groupBy, "none", StringComparison.OrdinalIgnoreCase) ? "none" : "description",
                page,
                pageSize,
                totalRows,
                hasMore = totalRows > page * pageSize,
                rows
            },
            DurationMs = sw.ElapsedMilliseconds
        };
        if (since != null) CheckpointSupport.AddTrackingWarning(since, result);
        return Task.FromResult(result);
    }

    private static string? NameOf(Document doc, long id)
    {
        try { return doc.GetElement(new ElementId(id))?.Name; }
        catch { return null; }
    }
}

/// <summary>revit_checkpoint_create (#98): remembers "now" so later changes can be listed and rolled back.</summary>
public sealed class CheckpointCreateTool : IRevitMcpTool
{
    public string Name => "revit_checkpoint_create";
    public string Description =>
        "Creates a checkpoint before a batch of writes: remembers the model's change stamp and Revit warnings and, for a " +
        "scope (elementIds, categories, viewId or useSelection), a snapshot of parameter values and locations. Afterwards " +
        "revit_checkpoint_diff lists what changed since (added/modified/deleted, by which tool or the user, before/now " +
        "values, moves, new warnings) and revit_checkpoint_rollback can undo it. Without a scope only change ids and " +
        "warnings are tracked. Does not change the model.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Reports;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = CheckpointSupport.Resolve(uiapp, request, out var store, out var failure);
        if (target == null) return Task.FromResult(failure!);
        var doc = target.Document;
        var args = request.Arguments;

        var viewId = ToolArguments.GetLong(args, "viewId");
        var scope = new SnapshotScope
        {
            ElementIds = ToolArguments.GetLongArray(args, "elementIds").Distinct().ToList(),
            Categories = ToolArguments.GetStringArray(args, "categories").Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList(),
            ViewId = viewId > 0 ? viewId : null,
            UseSelection = ToolArguments.GetBool(args, "useSelection"),
            Parameters = ToolArguments.GetStringArray(args, "parameters").Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };

        var client = string.IsNullOrWhiteSpace(request.ClientName) ? "agent" : request.ClientName.Trim();
        var title = ToolArguments.GetString(args, "title") is { Length: > 0 } t ? t : $"{client} {DateTime.Now:HH:mm}";
        var checkpoint = CheckpointSupport.Create(uiapp, target, store!, scope, title, client, request, cancellationToken,
            out var evicted, out var unreadable, out failure);
        if (checkpoint == null) return Task.FromResult(failure!);
        var snapshot = checkpoint.Snapshot;
        var warnings = checkpoint.Warnings;

        var result = new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Checkpoint {checkpoint.Id} '{checkpoint.Title}' created for '{doc.Title}'" +
                      (snapshot.Count > 0 ? $" with a snapshot of {snapshot.Count} element(s)." : " (change ids and warnings only)."),
            Data = new
            {
                checkpointId = checkpoint.Id,
                title = checkpoint.Title,
                document = doc.Title,
                documentVersion = checkpoint.DocumentVersion,
                warnings = new
                {
                    total = warnings.Count,
                    error = warnings.Count(WarningDiff.IsErrorSeverity),
                    warning = warnings.Count(w => !WarningDiff.IsErrorSeverity(w)),
                    unreadable
                },
                snapshot = new
                {
                    elements = snapshot.Count,
                    parameters = snapshot.Values.Sum(s => s.Parameters.Count)
                },
                note = $"After your writes: revit_checkpoint_diff checkpointId={checkpoint.Id} (what changed, by whom, before/now) and " +
                       $"revit_get_warnings sinceCheckpointId={checkpoint.Id} (new Revit warnings)."
            },
            DurationMs = sw.ElapsedMilliseconds
        };
        foreach (var old in evicted)
            result.Warnings.Add($"At most {CheckpointStore.MaxPerModel} checkpoints are kept per document; the oldest, {old.Id} '{old.Title}', was removed.");
        return Task.FromResult(result);
    }
}

/// <summary>revit_checkpoint_list (#98): checkpoints of the open documents. Read-only: revit_checkpoint_remove deletes one.</summary>
public sealed class CheckpointListTool : IRevitMcpTool
{
    public string Name => "revit_checkpoint_list";
    public string Description =>
        "Lists the checkpoints of the open documents (or one with document=<title>): id, title, age, snapshot size, " +
        "recorded transactions, whether tracking is complete, plan id. revit_checkpoint_remove deletes one.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Reports;

    /// <summary>The open project documents, or the one named by <c>document</c>; null with <paramref name="failure"/> otherwise.</summary>
    internal static List<Document>? Documents(UIApplication uiapp, McpToolRequest request, out McpToolResult? failure)
    {
        failure = null;
        if (string.IsNullOrWhiteSpace(ToolArguments.GetString(request.Arguments, RevitDocumentResolver.ArgumentName)))
            return RevitDocumentResolver.GetProjectDocuments(uiapp);
        var target = RevitDocumentResolver.Resolve(uiapp, request, out var error);
        if (target != null) return new List<Document> { target.Document };
        failure = CheckpointSupport.Fail(request, error ?? "Document was not found.");
        return null;
    }

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var store = CheckpointChangeTracker.Store;
        if (store == null)
            return Task.FromResult(CheckpointSupport.Fail(request, "Checkpoints are not available: the connector's checkpoint store did not start.", "tool_execution_failed"));

        var documents = Documents(uiapp, request, out var failure);
        if (documents == null) return Task.FromResult(failure!);

        var messages = new List<string>();
        var now = DateTimeOffset.Now;
        var rows = new List<object>();
        foreach (var doc in documents)
        {
            foreach (var cp in store.ForModel(CheckpointChangeTracker.ModelIdOf(doc)).OrderByDescending(c => c.CreatedAt))
            {
                var net = ChangeNetting.Net(cp.Batches);
                rows.Add(new
                {
                    id = cp.Id,
                    title = cp.Title,
                    document = doc.Title,
                    createdAt = cp.CreatedAt,
                    ageMinutes = Math.Round((now - cp.CreatedAt).TotalMinutes, 1),
                    createdBy = cp.CreatedBy,
                    snapshotElements = cp.Snapshot.Count,
                    transactions = cp.Batches.Count,
                    touched = net.Count,
                    warningsAtCreation = cp.Warnings.Count,
                    trackingComplete = cp.TrackingComplete,
                    planId = cp.PlanId
                });
            }
        }

        messages.Add($"{rows.Count} checkpoint(s) in {documents.Count} document(s).");
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = string.Join(" ", messages),
            Data = new { checkpoints = rows }
        });
    }
}

/// <summary>
/// revit_checkpoint_remove (H1): deletes one checkpoint record, split off revit_checkpoint_list so the list stays
/// read-only. The model is not changed, so it needs no approval.
/// </summary>
public sealed class CheckpointRemoveTool : IRevitMcpTool
{
    public string Name => "revit_checkpoint_remove";
    public string Description =>
        "Deletes one checkpoint record (checkpointId, from revit_checkpoint_list) of the open documents or of document=<title>. " +
        "The model is not changed; the checkpoint can no longer be diffed or rolled back.";
    public ToolPermission Permission => ToolPermission.DirectEdit;
    public ToolCategory Category => ToolCategory.Reports;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var store = CheckpointChangeTracker.Store;
        if (store == null)
            return Task.FromResult(CheckpointSupport.Fail(request, "Checkpoints are not available: the connector's checkpoint store did not start.", "tool_execution_failed"));

        var id = ToolArguments.GetString(request.Arguments, "checkpointId").Trim();
        if (id.Length == 0) return Task.FromResult(CheckpointSupport.Fail(request, "checkpointId is required."));

        var documents = CheckpointListTool.Documents(uiapp, request, out var failure);
        if (documents == null) return Task.FromResult(failure!);

        if (!documents.Any(d => store.Remove(CheckpointChangeTracker.ModelIdOf(d), id)))
            return Task.FromResult(CheckpointSupport.Fail(request, $"Checkpoint {id} was not found in the open documents.", CheckpointSupport.NotFound));
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Removed checkpoint {id}. The model was not changed.",
            Data = new { removed = id }
        });
    }
}

/// <summary>revit_checkpoint_diff (#98): everything that changed since a checkpoint.</summary>
public sealed class CheckpointDiffTool : IRevitMcpTool
{
    public string Name => "revit_checkpoint_diff";
    public string Description =>
        "Shows what changed in the model since a checkpoint: added / modified / deleted element ids with who did it " +
        "(agent:<tool>, page or user), parameter before/now values and moves for snapshotted elements, the transactions " +
        "Revit recorded (its Undo names), new and resolved Revit warnings, and what revit_checkpoint_rollback could undo. " +
        "include=summary (default) | elements (element lists, paged) | all. Filters: categories, attribution (agent|user|all).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Reports;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = CheckpointSupport.Resolve(uiapp, request, out var store, out var failure);
        if (target == null) return Task.FromResult(failure!);
        var doc = target.Document;
        var checkpoint = CheckpointSupport.Find(store!, doc, request, out failure);
        if (checkpoint == null) return Task.FromResult(failure!);

        var args = request.Arguments;
        var include = ToolArguments.GetString(args, "include", "summary").Trim().ToLowerInvariant();
        if (include is not ("summary" or "elements" or "all")) include = "summary";
        var categories = ToolArguments.GetStringArray(args, "categories").Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList();
        var options = new DiffReportOptions
        {
            Include = include,
            Categories = categories.Count > 0 ? new HashSet<string>(categories, StringComparer.OrdinalIgnoreCase) : null,
            Attribution = ToolArguments.GetString(args, "attribution", "all"),
            Page = Math.Max(1, ToolArguments.GetInt(args, "page", 1)),
            PageSize = QueryGuard.NormalizePageSize(ToolArguments.GetInt(args, "pageSize", 0) is var ps and > 0 ? ps : null)
        };

        var (diff, warnings, unreadable) = CheckpointSupport.ComputeDiff(doc, checkpoint);
        var report = CheckpointDiffReport.Build(checkpoint, diff, warnings, options, DateTimeOffset.Now);
        report["unreadableWarnings"] = unreadable;

        var counts = (Dictionary<string, object?>)report["counts"]!;
        var result = new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Since checkpoint {checkpoint.Id} '{checkpoint.Title}': {counts["added"]} added, {counts["modified"]} modified, " +
                      $"{counts["deleted"]} deleted; {warnings.New.Count} new and {warnings.Resolved.Count} resolved warning(s)." +
                      (include == "summary" ? " Pass include=elements for the element lists; publish long lists with revit_publish_page." : string.Empty),
            Data = report,
            DurationMs = sw.ElapsedMilliseconds
        };
        CheckpointSupport.AddTrackingWarning(checkpoint, result);
        if (checkpoint.Scope == null)
            result.Warnings.Add("This checkpoint has no snapshot: changes are listed by id, without before/now values, and a rollback can only delete added elements.");
        return Task.FromResult(result);
    }
}
