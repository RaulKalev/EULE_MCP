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
        "Always run revit_preview_delete_sheets first.";
    public ToolPermission Permission => ToolPermission.DestructiveRequiresManualApproval;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw  = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
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

        var toDelete = sheets.Where(s => !(skipSheetsWithVps && s.GetAllPlacedViews().Count > 0)).ToList();
        int skipped  = (sheetIds.Length > 0 ? sheetIds.Length : sheetNumbers.Length) - toDelete.Count;

        if (toDelete.Count == 0)
            return Task.FromResult(Fail(request, "All selected sheets have placed views and were skipped (skipSheetsWithViews=true)."));

        int deleted  = 0;
        var warnings = new List<string>();

        using var t = new Transaction(doc, "Revit MCP - Delete Sheets");
        t.Start();
        foreach (var s in toDelete)
        {
            try
            {
                var dels = doc.Delete(s.Id);
                if (dels.Count > 0) deleted++;
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not delete sheet '{s.SheetNumber}': {ex.Message}");
            }
        }
        t.Commit();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = deleted > 0,
            Message    = $"Deleted {deleted} sheet(s), skipped {skipped}.",
            Data       = new { deleted, skipped, failed = toDelete.Count - deleted },
            Warnings   = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
