using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// DESTRUCTIVE — permanently deletes sheets. Always requires manual approval.
/// Direct Edit mode does NOT bypass this approval gate.
/// </summary>
public class DeleteSheetsTool : IRevitMcpTool
{
    public string Name => "revit_delete_sheets";
    public string Description =>
        "DESTRUCTIVE: Permanently deletes sheets. Always requires manual approval — cannot be bypassed. " +
        "Required: sheetIds (long array) OR sheetNumbers (string array). " +
        "Optional: skipSheetsWithViews (bool, default true — skips sheets that have placed views). " +
        "If a target sheet is open or active it is closed / switched away from automatically. " +
        "Always run revit_preview_delete_sheets first.";
    public ToolPermission Permission => ToolPermission.DestructiveRequiresManualApproval;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw    = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        var doc   = uidoc?.Document;
        if (doc == null)
            return Task.FromResult(Fail(request, "No active document."));

        var sheetIds          = ToolArguments.GetLongArray(request.Arguments, "sheetIds");
        var sheetNumbers      = ToolArguments.GetStringArray(request.Arguments, "sheetNumbers");
        var skipSheetsWithVps = ToolArguments.GetBool(request.Arguments, "skipSheetsWithViews", true);

        if (sheetIds.Length == 0 && sheetNumbers.Length == 0)
            return Task.FromResult(Fail(request, "Provide sheetIds or sheetNumbers."));

        IEnumerable<ViewSheet> sheets = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>();

        if (sheetIds.Length > 0)
            sheets = sheets.Where(s => sheetIds.ToHashSet().Contains(s.Id.Value));
        else
            sheets = sheets.Where(s => sheetNumbers.Select(n => n.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase).Contains(s.SheetNumber));

        var warnings = new List<string>();
        var matched  = sheets.ToList();
        var toDelete = matched.Where(s => !(skipSheetsWithVps && s.GetAllPlacedViews().Count > 0)).ToList();
        int skipped  = (sheetIds.Length > 0 ? sheetIds.Length : sheetNumbers.Length) - toDelete.Count;
        if (matched.Count > toDelete.Count)
            warnings.Add($"{matched.Count - toDelete.Count} sheet(s) skipped: have placed views (skipSheetsWithViews=true).");

        if (toDelete.Count == 0)
            return Task.FromResult(Fail(request, "No deletable sheets in the selection — sheets with placed views are skipped when skipSheetsWithViews=true.", warnings));

        // Revit refuses to delete the active view (and open views on some versions) —
        // a sheet can be the active view too. Switch away / close tabs first.
        var undeletable = ViewDeletionPrep.PrepareForDeletion(uidoc!, toDelete.Select(s => s.Id).ToHashSet(), warnings);
        if (undeletable.Count > 0)
            toDelete.RemoveAll(s => undeletable.Contains(s.Id));

        if (toDelete.Count == 0)
            return Task.FromResult(Fail(request, "No deletable sheets remain — see warnings.", warnings));

        // Capture ids/numbers up front — reading members of an already-deleted Element throws.
        var targets = toDelete.Select(s => (s.Id, IdValue: s.Id.Value, s.SheetNumber)).ToList();

        int deleted  = 0;
        var failures = new List<object>();

        cancellationToken.ThrowIfCancellationRequested();
        using var t = new Transaction(doc, "Revit MCP - Delete Sheets");
        t.Start();
        foreach (var (eid, idValue, number) in targets)
        {
            if (doc.GetElement(eid) == null)
            {
                deleted++; // already removed as a dependent of an earlier delete
                continue;
            }
            try
            {
                var dels = doc.Delete(eid);
                if (dels.Count > 0) deleted++;
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not delete sheet '{number}' ({idValue}): {ex.Message}");
                failures.Add(new { sheetId = idValue, sheetNumber = number, error = ex.Message });
            }
        }
        RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(t);

        sw.Stop();
        int failed = targets.Count - deleted;
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = deleted > 0,
            Message    = $"Deleted {deleted} sheet(s), skipped {skipped + undeletable.Count}, failed {failed}.",
            Data       = new { deleted, skipped = skipped + undeletable.Count, failed, failures },
            Warnings   = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg, List<string>? warnings = null) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg, Warnings = warnings ?? new List<string>() };
}
