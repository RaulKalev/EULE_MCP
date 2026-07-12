using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// DESTRUCTIVE — permanently deletes views. Always requires manual approval (DestructiveRequiresManualApproval).
/// Direct Edit mode does NOT bypass this approval gate.
/// </summary>
public class DeleteViewsTool : IRevitMcpTool
{
    public string Name => "revit_delete_views";
    public string Description =>
        "DESTRUCTIVE: Permanently deletes views. Always requires manual approval — cannot be bypassed. " +
        "Required: viewIds (long array). " +
        "Optional: skipPlacedOnSheets (bool, default true — skips views that are on sheets). " +
        "Always run revit_preview_delete_views first.";
    public ToolPermission Permission => ToolPermission.DestructiveRequiresManualApproval;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw  = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(Fail(request, "No active document."));

        var viewIds            = ToolArguments.GetLongArray(request.Arguments, "viewIds");
        var skipPlacedOnSheets = ToolArguments.GetBool(request.Arguments, "skipPlacedOnSheets", true);

        if (viewIds.Length == 0)
            return Task.FromResult(Fail(request, "viewIds is required."));

        var placedIds = skipPlacedOnSheets
            ? new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .SelectMany(s => s.GetAllPlacedViews())
                .ToHashSet()
            : [];

        var toDelete = viewIds
            .Where(id => !placedIds.Contains(new ElementId(id)))
            .Select(id => new ElementId(id))
            .ToList();

        int skipped = viewIds.Length - toDelete.Count;

        if (toDelete.Count == 0)
            return Task.FromResult(Fail(request, "All selected views are placed on sheets and were skipped (skipPlacedOnSheets=true)."));

        int deleted  = 0;
        var warnings = new List<string>();

        cancellationToken.ThrowIfCancellationRequested();
        using var t = new Transaction(doc, "Revit MCP - Delete Views");
        t.Start();
        foreach (var eid in toDelete)
        {
            try
            {
                var deleted_ = doc.Delete(eid);
                if (deleted_.Count > 0) deleted++;
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not delete view {eid.Value}: {ex.Message}");
            }
        }
        RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(t);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = deleted > 0,
            Message    = $"Deleted {deleted} view(s), skipped {skipped} (placed on sheets).",
            Data       = new { deleted, skipped, failed = toDelete.Count - deleted },
            Warnings   = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
