using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class PreviewDeleteSheetsTool : IRevitMcpTool
{
    public string Name => "revit_preview_delete_sheets";
    public string Description =>
        "Previews which sheets would be deleted WITHOUT making any changes. " +
        "Required: sheetIds (long array) OR sheetNumbers (string array) OR nameFilter (string). " +
        "Optional: skipSheetsWithViews (bool, default true — protect sheets that have placed views). " +
        "Returns proposals: sheetId, sheetNumber, sheetName, viewportCount, wouldDelete, reason.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw  = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "No active document." });

        var sheetIds          = ToolArguments.GetLongArray(request.Arguments, "sheetIds");
        var sheetNumbers      = ToolArguments.GetStringArray(request.Arguments, "sheetNumbers");
        var nameFilter        = ToolArguments.GetString(request.Arguments, "nameFilter");
        var skipSheetsWithVps = ToolArguments.GetBool(request.Arguments, "skipSheetsWithViews", true);

        if (sheetIds.Length == 0 && sheetNumbers.Length == 0 && string.IsNullOrWhiteSpace(nameFilter))
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "Provide sheetIds, sheetNumbers, or nameFilter." });

        IEnumerable<ViewSheet> sheets = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>();

        if (sheetIds.Length > 0)       sheets = sheets.Where(s => sheetIds.ToHashSet().Contains(s.Id.Value));
        else if (sheetNumbers.Length > 0) sheets = sheets.Where(s => sheetNumbers.Select(n => n.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase).Contains(s.SheetNumber));
        if (!string.IsNullOrWhiteSpace(nameFilter)) sheets = sheets.Where(s => s.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase));

        var proposals    = new List<object>();
        int wouldDelete  = 0;
        int wouldProtect = 0;

        foreach (var s in sheets)
        {
            var vpCount = s.GetAllPlacedViews().Count;
            bool del    = !(skipSheetsWithVps && vpCount > 0);
            if (del) wouldDelete++; else wouldProtect++;

            proposals.Add(new
            {
                sheetId       = s.Id.Value,
                sheetNumber   = s.SheetNumber,
                sheetName     = s.Name,
                viewportCount = vpCount,
                wouldDelete   = del,
                reason        = del ? "Eligible for deletion" : $"Protected: {vpCount} view(s) placed"
            });
        }

        var warnings = new List<string>();
        if (wouldDelete > 0)
            warnings.Add($"WARNING: {wouldDelete} sheet(s) will be permanently deleted. Use revit_delete_sheets to confirm.");

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = $"Preview: {wouldDelete} sheet(s) would be deleted, {wouldProtect} protected.",
            Data       = new { wouldDelete, wouldProtect, proposals },
            Warnings   = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}
