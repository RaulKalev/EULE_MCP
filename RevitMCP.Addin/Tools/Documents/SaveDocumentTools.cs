using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Documents;

/// <summary>revit_save_document (#90): saves one or all open project documents (local file for workshared ones).</summary>
public sealed class SaveDocumentTool : IRevitMcpTool
{
    public string Name => "revit_save_document";
    public string Description =>
        "Saves an open project document (active or not), or every open project document with all=true. A workshared " +
        "local is saved locally only — use revit_sync_with_central to synchronize. Skips documents that are read-only, " +
        "detached, never saved or unchanged (onlyModified=true, default).";
    public ToolPermission Permission => ToolPermission.DirectEdit;
    public ToolCategory Category => ToolCategory.Worksharing;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var documents = SaveSupport.ResolveDocuments(uiapp, request, out var failure);
        if (documents == null) return Task.FromResult(failure!);

        var onlyModified = ToolArguments.GetBool(request.Arguments, "onlyModified", true);
        var results = new List<object>();
        var saved = 0;
        var failed = 0;
        foreach (var doc in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var skip = SaveSupport.SkipReason(doc, requireWorkshared: false);
            if (skip == null && onlyModified && !doc.IsModified) skip = "no unsaved changes";
            if (skip != null)
            {
                results.Add(new { title = doc.Title, path = doc.PathName ?? string.Empty, status = "skipped", reason = skip });
                continue;
            }

            try
            {
                doc.Save();
                saved++;
                results.Add(new { title = doc.Title, path = doc.PathName ?? string.Empty, status = "saved", reason = (string?)null });
            }
            catch (Exception ex)
            {
                failed++;
                results.Add(new { title = doc.Title, path = doc.PathName ?? string.Empty, status = "failed", reason = (string?)ex.Message });
            }
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = failed == 0,
            Message = $"Saved {saved} of {documents.Count} document(s){(failed > 0 ? $", {failed} failed" : string.Empty)}.",
            Data = new { results },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

/// <summary>revit_sync_with_central (#90): synchronizes workshared documents with central after manual approval.</summary>
public sealed class SyncWithCentralTool : IRevitMcpTool
{
    public string Name => "revit_sync_with_central";
    public string Description =>
        "Synchronizes a workshared document (active or not), or every open workshared document with all=true, with " +
        "central. Always requires manual approval. Options: comment, relinquishAll (default true), saveLocal (save the " +
        "local file before and after, default true), compact (default false).";
    public ToolPermission Permission => ToolPermission.DestructiveRequiresManualApproval;
    public ToolCategory Category => ToolCategory.Worksharing;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var documents = SaveSupport.ResolveDocuments(uiapp, request, out var failure);
        if (documents == null) return Task.FromResult(failure!);

        var comment = ToolArguments.GetString(request.Arguments, "comment");
        var relinquishAll = ToolArguments.GetBool(request.Arguments, "relinquishAll", true);
        var saveLocal = ToolArguments.GetBool(request.Arguments, "saveLocal", true);
        var compact = ToolArguments.GetBool(request.Arguments, "compact", false);

        var results = new List<object>();
        var synced = 0;
        var failed = 0;
        foreach (var doc in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var skip = SaveSupport.SkipReason(doc, requireWorkshared: true);
            if (skip != null)
            {
                results.Add(new { title = doc.Title, status = "skipped", reason = skip });
                continue;
            }

            try
            {
                var syncOptions = new SynchronizeWithCentralOptions
                {
                    Comment = comment,
                    SaveLocalBefore = saveLocal,
                    SaveLocalAfter = saveLocal,
                    Compact = compact
                };
                syncOptions.SetRelinquishOptions(new RelinquishOptions(relinquishAll));
                doc.SynchronizeWithCentral(new TransactWithCentralOptions(), syncOptions);
                synced++;
                results.Add(new { title = doc.Title, status = "synced", reason = (string?)null });
            }
            catch (Exception ex)
            {
                failed++;
                results.Add(new { title = doc.Title, status = "failed", reason = (string?)ex.Message });
            }
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = failed == 0,
            Message = $"Synchronized {synced} of {documents.Count} document(s) with central{(failed > 0 ? $", {failed} failed" : string.Empty)}.",
            Data = new { results },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

internal static class SaveSupport
{
    /// <summary>The target documents: the <c>document</c> argument, or every open project document with all=true.</summary>
    public static List<Document>? ResolveDocuments(UIApplication uiapp, McpToolRequest request, out McpToolResult? failure)
    {
        failure = null;
        if (ToolArguments.GetBool(request.Arguments, "all"))
            return RevitDocumentResolver.GetProjectDocuments(uiapp);

        var target = DocumentToolSupport.ResolveTarget(uiapp, request, out failure);
        return target == null ? null : new List<Document> { target.Document };
    }

    public static string? SkipReason(Document doc, bool requireWorkshared)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(doc.PathName)) return "never saved (use Save As in Revit)";
            if (doc.IsReadOnly) return "read-only";
            if (doc.IsModifiable) return "a transaction is open";
            if (doc.IsWorkshared && doc.IsDetached) return "detached from central";
            if (requireWorkshared && !doc.IsWorkshared) return "not workshared (use revit_save_document)";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        return null;
    }
}
