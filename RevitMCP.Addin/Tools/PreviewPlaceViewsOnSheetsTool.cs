using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documentation.Placement;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class PreviewPlaceViewsOnSheetsTool : IRevitMcpTool
{
    public string Name => "revit_preview_place_views_on_sheets";
    public string Description =>
        "Previews which views would be placed on which sheets WITHOUT making any changes. " +
        "Parameters: viewIds (long array, required), sheetIds (long array) or allSheets (bool=true), " +
        "matchMode (ExactName|Contains|Fuzzy|SheetNumberPrefix|SheetNumberSuffix|CustomParameter, default Contains), " +
        "fuzzyThreshold (double 0-1, default 0.6), customParamName (string, for CustomParameter mode), " +
        "skipAlreadyPlaced (bool, default true). " +
        "Returns proposals array with viewId, viewName, targetSheetId, targetSheetNumber, targetSheetName, score, reason, alreadyPlaced.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw  = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "No active document." });

        var viewIds          = ToolArguments.GetLongArray(request.Arguments, "viewIds");
        var sheetIds         = ToolArguments.GetLongArray(request.Arguments, "sheetIds");
        var allSheets        = ToolArguments.GetBool(request.Arguments, "allSheets", false);
        var matchMode        = ToolArguments.GetString(request.Arguments, "matchMode", ViewSheetMatchingService.ModeContains);
        var fuzzyThreshold   = GetDouble(request.Arguments, "fuzzyThreshold", 0.6);
        var customParamName  = ToolArguments.GetString(request.Arguments, "customParamName");
        var skipPlaced       = ToolArguments.GetBool(request.Arguments, "skipAlreadyPlaced", true);

        if (viewIds.Length == 0)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "viewIds is required." });

        var placedIds = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .SelectMany(s => s.GetAllPlacedViews())
            .ToHashSet();

        IEnumerable<ViewSheet> candidateSheets;
        if (allSheets || sheetIds.Length == 0)
        {
            candidateSheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>();
        }
        else
        {
            var idSet = sheetIds.ToHashSet();
            candidateSheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => idSet.Contains(s.Id.Value));
        }

        var targetIds  = skipPlaced ? viewIds.Where(id => !placedIds.Contains(new ElementId(id))).ToArray() : viewIds;
        var skippedIds = skipPlaced ? viewIds.Except(targetIds).ToArray() : [];

        var proposals = ViewSheetMatchingService.Match(doc, targetIds, candidateSheets, matchMode,
            fuzzyThreshold, customParamName);

        var matched   = proposals.Count(p => p.SheetId.HasValue);
        var unmatched = proposals.Count - matched;

        var warnings = new List<string>();
        if (skippedIds.Length > 0)
            warnings.Add($"{skippedIds.Length} view(s) skipped (already placed on sheets).");
        if (unmatched > 0)
            warnings.Add($"{unmatched} view(s) could not be matched to a sheet.");

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = $"Preview: {matched} view(s) would be placed, {unmatched} unmatched, {skippedIds.Length} skipped.",
            Data = new
            {
                toPlace    = matched,
                unmatched,
                skipped    = skippedIds.Length,
                proposals  = proposals.Select(p => new
                {
                    viewId           = p.ViewId,
                    viewName         = p.ViewName,
                    targetSheetId    = p.SheetId,
                    targetSheetNumber= p.SheetNumber,
                    targetSheetName  = p.SheetName,
                    score            = Math.Round(p.Score, 3),
                    reason           = p.Reason
                })
            },
            Warnings   = warnings,
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
}
