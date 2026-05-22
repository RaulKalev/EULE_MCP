using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documentation.Placement;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class PlaceViewsOnSheetsTool : IRevitMcpTool
{
    public string Name => "revit_place_views_on_sheets";
    public string Description =>
        "Places views on sheets. Requires approval. " +
        "Required: viewIds (long array). " +
        "Option A (direct): targetSheetId (long) — places ALL specified views on one specific sheet. " +
        "Option B (matching): sheetIds (long array) or allSheets (bool=true), " +
        "matchMode (ExactName|Contains|Fuzzy|SheetNumberPrefix|SheetNumberSuffix|CustomParameter, default Contains), " +
        "fuzzyThreshold (double 0-1, default 0.6), customParamName (string), " +
        "skipAlreadyPlaced (bool, default true). " +
        "Use revit_preview_place_views_on_sheets first to verify proposals.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw    = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        var doc   = uidoc?.Document;
        if (doc == null)
            return Task.FromResult(Fail(request, "No active document."));

        var viewIds         = ToolArguments.GetLongArray(request.Arguments, "viewIds");
        var targetSheetId   = ToolArguments.GetLong(request.Arguments, "targetSheetId", 0L);
        var sheetIds        = ToolArguments.GetLongArray(request.Arguments, "sheetIds");
        var allSheets       = ToolArguments.GetBool(request.Arguments, "allSheets", false);
        var matchMode       = ToolArguments.GetString(request.Arguments, "matchMode", ViewSheetMatchingService.ModeContains);
        var fuzzyThreshold  = GetDouble(request.Arguments, "fuzzyThreshold", 0.6);
        var customParamName = ToolArguments.GetString(request.Arguments, "customParamName");
        var skipPlaced      = ToolArguments.GetBool(request.Arguments, "skipAlreadyPlaced", true);

        if (viewIds.Length == 0)
            return Task.FromResult(Fail(request, "viewIds is required."));

        // Placed check
        var placedIds = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .SelectMany(s => s.GetAllPlacedViews())
            .ToHashSet();

        var targetIds = skipPlaced ? viewIds.Where(id => !placedIds.Contains(new ElementId(id))).ToArray() : viewIds;

        // --- Option A: direct placement onto a specific sheet (bypasses matching) ---
        if (targetSheetId > 0)
        {
            var targetSheet = doc.GetElement(new ElementId(targetSheetId)) as ViewSheet;
            if (targetSheet == null)
                return Task.FromResult(Fail(request, $"Sheet with id {targetSheetId} not found."));

            var toPlace = targetIds.ToList();
            if (toPlace.Count == 0)
                return Task.FromResult(Fail(request, "All specified views are already placed."));

            int placed   = 0;
            var warnings = new List<string>();
            var placed_  = new List<object>();

            using var t = new Transaction(doc, "Revit MCP - Place Views on Sheets");
            t.Start();
            foreach (var vid in toPlace)
            {
                try
                {
                    var view = doc.GetElement(new ElementId(vid)) as View;
                    if (view == null) { warnings.Add($"View {vid} not found."); continue; }
                    var center = PlacementPointResolver.GetSheetCenter(doc, targetSheet);
                    Viewport.Create(doc, targetSheet.Id, view.Id, center);
                    placed++;
                    placed_.Add(new { viewId = vid, viewName = view.Name, sheetId = targetSheetId, sheetNumber = targetSheet.SheetNumber });
                }
                catch (Exception ex) { warnings.Add($"Failed to place view {vid}: {ex.Message}"); }
            }
            t.Commit();

            sw.Stop();
            return Task.FromResult(new McpToolResult
            {
                RequestId  = request.RequestId,
                Success    = placed > 0,
                Message    = $"Placed {placed}/{toPlace.Count} view(s) on sheet '{targetSheet.SheetNumber}'.",
                Data       = new { placed, failed = toPlace.Count - placed, results = placed_ },
                Warnings   = warnings,
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        // --- Option B: matching-based placement ---
        IEnumerable<ViewSheet> candidateSheets = allSheets || sheetIds.Length == 0
            ? new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
            : new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Where(s => sheetIds.ToHashSet().Contains(s.Id.Value));

        var proposals = ViewSheetMatchingService.Match(doc, targetIds, candidateSheets, matchMode, fuzzyThreshold, customParamName);

        var toPlaceB = proposals.Where(p => p.SheetId.HasValue).ToList();
        if (toPlaceB.Count == 0)
            return Task.FromResult(Fail(request, "No views could be matched to sheets. Run preview first, or use targetSheetId for direct placement."));

        int placed2   = 0;
        var warnings2 = new List<string>();
        var placed2_  = new List<object>();

        using var t2 = new Transaction(doc, "Revit MCP - Place Views on Sheets");
        t2.Start();
        foreach (var p in toPlaceB)
        {
            try
            {
                var sheet  = doc.GetElement(new ElementId(p.SheetId!.Value)) as ViewSheet;
                var view   = doc.GetElement(new ElementId(p.ViewId))         as View;
                if (sheet == null || view == null) { warnings2.Add($"Could not resolve view/sheet for view {p.ViewId}"); continue; }

                var center = PlacementPointResolver.GetSheetCenter(doc, sheet);
                Viewport.Create(doc, sheet.Id, view.Id, center);
                placed2++;
                placed2_.Add(new { viewId = p.ViewId, viewName = p.ViewName, sheetId = p.SheetId, sheetNumber = p.SheetNumber });
            }
            catch (Exception ex)
            {
                warnings2.Add($"Failed to place '{p.ViewName}': {ex.Message}");
            }
        }
        t2.Commit();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = placed2 > 0,
            Message    = $"Placed {placed2}/{toPlaceB.Count} view(s) on sheets.",
            Data       = new { placed = placed2, failed = toPlaceB.Count - placed2, results = placed2_ },
            Warnings   = warnings2,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static double GetDouble(Dictionary<string, object?> args, string key, double def)
    {
        if (!args.TryGetValue(key, out var val)) return def;
        return val switch
        {
            double d                               => d,
            Newtonsoft.Json.Linq.JValue jv => (double)(jv.Value ?? def),
            _ => def
        };
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
