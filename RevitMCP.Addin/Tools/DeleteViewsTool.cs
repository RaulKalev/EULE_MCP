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
        "If a target view is open or active it is closed / switched away from automatically. " +
        "Always run revit_preview_delete_views first.";
    public ToolPermission Permission => ToolPermission.DestructiveRequiresManualApproval;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw    = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        var doc   = uidoc?.Document;
        if (doc == null)
            return Task.FromResult(Fail(request, "No active document."));

        var viewIds            = ToolArguments.GetLongArray(request.Arguments, "viewIds");
        var skipPlacedOnSheets = ToolArguments.GetBool(request.Arguments, "skipPlacedOnSheets", true);

        if (viewIds.Length == 0)
            return Task.FromResult(Fail(request, "viewIds is required."));

        var warnings = new List<string>();
        var views    = new List<View>();
        int skippedMissing  = 0;
        int skippedSheets   = 0;
        int skippedNonViews = 0;
        foreach (var id in viewIds.Distinct())
        {
            var el = doc.GetElement(new ElementId(id));
            if (el == null)
            {
                skippedMissing++;
                warnings.Add($"Element {id} not found — skipped.");
            }
            else if (el is ViewSheet)
            {
                skippedSheets++;
                warnings.Add($"Element {id} is a sheet — use revit_delete_sheets instead — skipped.");
            }
            else if (el is not View)
            {
                skippedNonViews++;
                warnings.Add($"Element {id} ('{el.Name}') is not a view — use revit_delete_elements instead — skipped.");
            }
            else
            {
                views.Add((View)el);
            }
        }

        var placedIds = skipPlacedOnSheets
            ? new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .SelectMany(s => s.GetAllPlacedViews())
                .ToHashSet()
            : [];

        var toDelete = views.Where(v => !placedIds.Contains(v.Id)).ToList();
        int skippedPlaced = views.Count - toDelete.Count;
        if (skippedPlaced > 0)
            warnings.Add($"{skippedPlaced} view(s) skipped: placed on sheets (skipPlacedOnSheets=true).");

        if (toDelete.Count == 0)
            return Task.FromResult(Fail(request, "No deletable views in the selection — see warnings for the reason each view was skipped.", warnings));

        // Revit refuses to delete the active view (and open views on some versions):
        // switch the active view away and close open tabs of targeted views first.
        var undeletable = ViewDeletionPrep.PrepareForDeletion(uidoc!, toDelete.Select(v => v.Id).ToHashSet(), warnings);
        if (undeletable.Count > 0)
            toDelete.RemoveAll(v => undeletable.Contains(v.Id));

        if (toDelete.Count == 0)
            return Task.FromResult(Fail(request, "No deletable views remain — see warnings.", warnings));

        // Capture ids/names up front: deleting a parent view cascades to its dependents,
        // and reading members of an already-deleted Element throws.
        var targets = toDelete.Select(v => (v.Id, IdValue: v.Id.Value, v.Name)).ToList();

        int deleted  = 0;
        var failures = new List<object>();

        cancellationToken.ThrowIfCancellationRequested();
        using var t = new Transaction(doc, "Revit MCP - Delete Views");
        t.Start();
        foreach (var (eid, idValue, name) in targets)
        {
            if (doc.GetElement(eid) == null)
            {
                deleted++; // already removed as a dependent of an earlier delete
                continue;
            }
            try
            {
                var deleted_ = doc.Delete(eid);
                if (deleted_.Count > 0) deleted++;
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not delete view '{name}' ({idValue}): {ex.Message}");
                failures.Add(new { viewId = idValue, viewName = name, error = ex.Message });
            }
        }
        RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(t);

        sw.Stop();
        int failed = targets.Count - deleted;
        int skippedUndeletable = undeletable.Count;
        int skipped = skippedMissing + skippedSheets + skippedNonViews + skippedPlaced + skippedUndeletable;
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = deleted > 0,
            Message    = $"Deleted {deleted} view(s), skipped {skipped}, failed {failed}.",
            Data       = new
            {
                deleted,
                skipped,
                skippedMissing,
                skippedSheets,
                skippedNonViews,
                skippedPlaced,
                skippedUndeletable,
                failed,
                failures
            },
            Warnings   = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg, List<string>? warnings = null) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg, Warnings = warnings ?? new List<string>() };
}
