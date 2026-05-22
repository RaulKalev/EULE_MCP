using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class GetViewSheetSummaryTool : IRevitMcpTool
{
    public string Name => "revit_get_view_sheet_summary";
    public string Description =>
        "Returns a high-level summary of views and sheets in the active document: " +
        "total views by type, total sheets, views placed vs unplaced, sheets with/without views, " +
        "sheets with/without title blocks, view template coverage.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw  = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "No active document." });

        var allSheets = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .ToList();

        var placedIds = allSheets
            .SelectMany(s => s.GetAllPlacedViews())
            .ToHashSet();

        var allViews = new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .ToList();

        var nonTemplateViews   = allViews.Where(v => !v.IsTemplate).ToList();
        var templateViews      = allViews.Where(v => v.IsTemplate).ToList();
        var printableViews     = nonTemplateViews.Where(v => v.CanBePrinted).ToList();

        var viewsByType = nonTemplateViews
            .GroupBy(v => v.ViewType.ToString())
            .ToDictionary(g => g.Key, g => g.Count());

        int sheetsWithoutViews    = allSheets.Count(s => s.GetAllPlacedViews().Count == 0);
        int sheetsWithoutTb       = 0;
        int viewsWithTemplate     = nonTemplateViews.Count(v => v.ViewTemplateId != ElementId.InvalidElementId);

        foreach (var sheet in allSheets)
        {
            try
            {
                var hasTb = new FilteredElementCollector(doc)
                    .OwnedByView(sheet.Id)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .Any();
                if (!hasTb) sheetsWithoutTb++;
            }
            catch { }
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = $"Summary: {allSheets.Count} sheets, {nonTemplateViews.Count} views ({printableViews.Count} printable).",
            Data = new
            {
                sheets = new
                {
                    total               = allSheets.Count,
                    withoutViews        = sheetsWithoutViews,
                    withViews           = allSheets.Count - sheetsWithoutViews,
                    withoutTitleBlock   = sheetsWithoutTb
                },
                views = new
                {
                    total               = nonTemplateViews.Count,
                    printable           = printableViews.Count,
                    placedOnSheets      = placedIds.Count,
                    unplaced            = printableViews.Count(v => !placedIds.Contains(v.Id)),
                    withViewTemplate    = viewsWithTemplate,
                    withoutViewTemplate = nonTemplateViews.Count - viewsWithTemplate,
                    templates           = templateViews.Count,
                    byType              = viewsByType
                }
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}
