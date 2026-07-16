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
        "matchMode (ExactName|Contains|Fuzzy|PlaceViews|SheetNumberPrefix|SheetNumberSuffix|CustomParameter, default Contains). " +
        "PlaceViews reproduces the source plugin's sheet-first exact/number/word matcher, recognizes Estonian floor ordinals 1-99, " +
        "and requires sheetIds or allSheets=true. " +
        "fuzzyThreshold (double 0-1, default 0.6), customParamName (string, for CustomParameter mode), " +
        "skipAlreadyPlaced (bool; default false for PlaceViews and true for other modes). " +
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
        var placeViewsMode   = PlaceViewsMatchingService.IsPlaceViewsMode(matchMode);
        var fuzzyThreshold   = GetDouble(request.Arguments, "fuzzyThreshold", 0.6);
        var customParamName  = ToolArguments.GetString(request.Arguments, "customParamName");
        var skipPlaced       = ToolArguments.GetBool(request.Arguments, "skipAlreadyPlaced", !placeViewsMode);

        if (viewIds.Length == 0)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "viewIds is required." });
        if (placeViewsMode && sheetIds.Length == 0 && !allSheets)
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Message = "PlaceViews mode requires sheetIds or allSheets=true."
            });

        // Build view→sheet reverse map so we can report which sheet a view is already on
        var viewToSheet = new Dictionary<ElementId, (long sheetId, string sheetNumber)>();
        var placedIds = new HashSet<ElementId>();
        foreach (var sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
        {
            foreach (var vid in sheet.GetAllPlacedViews())
            {
                placedIds.Add(vid);
                viewToSheet[vid] = (sheet.Id.Value, sheet.SheetNumber);
            }
        }

        IEnumerable<ViewSheet> candidateSheets;
        if (placeViewsMode)
        {
            candidateSheets = allSheets
                ? new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .Where(sheet => !sheet.IsTemplate)
                    .OrderBy(sheet => sheet.Name)
                : sheetIds
                    .Distinct()
                    .Select(sheetId => doc.GetElement(new ElementId(sheetId)) as ViewSheet)
                    .Where(sheet => sheet != null && !sheet.IsTemplate)
                    .Cast<ViewSheet>()
                    .OrderBy(sheet => sheet.Name);
        }
        else if (allSheets || sheetIds.Length == 0)
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

        if (placeViewsMode)
        {
            return Task.FromResult(PreviewPlaceViewsMode(
                doc,
                request,
                targetIds,
                candidateSheets,
                skippedIds,
                viewToSheet,
                sw,
                cancellationToken));
        }

        var proposals = ViewSheetMatchingService.Match(doc, targetIds, candidateSheets, matchMode,
            fuzzyThreshold, customParamName);

        var matched   = proposals.Count(p => p.SheetId.HasValue);
        var unmatched = proposals.Count - matched;

        var warnings = new List<string>();
        if (skippedIds.Length > 0)
        {
            var skippedDescs = skippedIds.Select(id =>
            {
                var eid = new ElementId(id);
                var name = (doc.GetElement(eid) as View)?.Name ?? id.ToString();
                if (viewToSheet.TryGetValue(eid, out var info))
                    return $"'{name}' (already on sheet {info.sheetNumber})";
                return $"'{name}' (already placed)";
            });
            warnings.Add($"{skippedIds.Length} view(s) skipped — already placed: {string.Join(", ", skippedDescs)}. " +
                         $"To place on a different sheet use skipAlreadyPlaced=false, or use targetSheetId for direct placement.");
        }
        if (unmatched > 0)
            warnings.Add($"{unmatched} view(s) could not be matched to a sheet with mode '{matchMode}'.");

        // Build skipped-view entries for inline visibility
        var skippedProposals = skippedIds.Select(id =>
        {
            var eid  = new ElementId(id);
            var name = (doc.GetElement(eid) as View)?.Name ?? id.ToString();
            viewToSheet.TryGetValue(eid, out var info);
            return new
            {
                viewId            = id,
                viewName          = name,
                targetSheetId     = (long?)null,
                targetSheetNumber = (string?)null,
                targetSheetName   = (string?)null,
                score             = 0.0,
                reason            = info.sheetNumber != null
                    ? $"Skipped — already on sheet {info.sheetNumber} (id {info.sheetId})"
                    : "Skipped — already placed",
                skipped           = true
            };
        });

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
                    reason           = p.Reason,
                    skipped          = false
                }).Concat<object>(skippedProposals)
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

    private static McpToolResult PreviewPlaceViewsMode(
        Document doc,
        McpToolRequest request,
        long[] targetViewIds,
        IEnumerable<ViewSheet> candidateSheets,
        long[] skippedViewIds,
        Dictionary<ElementId, (long sheetId, string sheetNumber)> viewToSheet,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        var sheets = candidateSheets.ToList();
        var proposals = PlaceViewsMatchingService.Match(doc, targetViewIds, sheets);
        var reservedSinglePlacementViews = new HashSet<long>();
        var results = new List<object>();
        var matched = 0;
        var canPlaceCount = 0;
        var matchedButCannotPlace = 0;

        foreach (var proposal in proposals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var canPlace = false;
            var placementReason = proposal.Reason;
            if (proposal.ViewId.HasValue)
            {
                matched++;
                var view = doc.GetElement(new ElementId(proposal.ViewId.Value)) as View;
                var sheet = doc.GetElement(new ElementId(proposal.SheetId)) as ViewSheet;
                try
                {
                    canPlace = view != null && sheet != null &&
                               Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id);
                    if (canPlace && view!.ViewType != ViewType.Legend &&
                        !reservedSinglePlacementViews.Add(view.Id.Value))
                    {
                        canPlace = false;
                        placementReason += " The same non-legend view was already reserved for an earlier sheet in this batch.";
                    }
                    else if (!canPlace)
                    {
                        placementReason += " Revit reports that the view cannot be added to this sheet (already placed or incompatible).";
                    }
                }
                catch (Exception ex)
                {
                    placementReason += $" Placement validation failed: {ex.Message}";
                }

                if (canPlace) canPlaceCount++;
                else matchedButCannotPlace++;
            }

            results.Add(new
            {
                viewId = proposal.ViewId,
                viewName = proposal.ViewName,
                viewType = proposal.ViewType,
                targetSheetId = proposal.SheetId,
                targetSheetNumber = proposal.SheetNumber,
                targetSheetName = proposal.SheetName,
                score = proposal.Score,
                scoreKind = "PlaceViewsRaw",
                exactMatch = proposal.IsExact,
                canPlace,
                reason = placementReason,
                skipped = false
            });
        }

        var warnings = new List<string>();
        var invalidViewIds = targetViewIds
            .Where(viewId => !PlaceViewsMatchingService.IsCandidateView(
                doc.GetElement(new ElementId(viewId)) as View))
            .Distinct()
            .ToArray();
        if (invalidViewIds.Length > 0)
            warnings.Add($"{invalidViewIds.Length} selected view(s) were not printable PlaceViews candidates: {string.Join(", ", invalidViewIds)}.");
        if (skippedViewIds.Length > 0)
        {
            var skippedDescriptions = skippedViewIds.Select(viewId =>
            {
                var elementId = new ElementId(viewId);
                var viewName = (doc.GetElement(elementId) as View)?.Name ?? viewId.ToString();
                return viewToSheet.TryGetValue(elementId, out var placement)
                    ? $"'{viewName}' (already on sheet {placement.sheetNumber})"
                    : $"'{viewName}' (already placed)";
            });
            warnings.Add($"{skippedViewIds.Length} view(s) were excluded by skipAlreadyPlaced=true: " +
                         string.Join(", ", skippedDescriptions) + ".");
        }
        if (matchedButCannotPlace > 0)
            warnings.Add($"{matchedButCannotPlace} matched sheet(s) cannot receive their proposed view.");

        var unmatched = proposals.Count - matched;
        if (unmatched > 0)
            warnings.Add($"{unmatched} selected sheet(s) had no matching selected view.");

        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"PlaceViews preview: {canPlaceCount} placement(s) can proceed, " +
                      $"{matchedButCannotPlace} matched but incompatible, {unmatched} unmatched sheet(s).",
            Data = new
            {
                matchMode = PlaceViewsMatchingService.ModePlaceViews,
                selectedSheets = sheets.Count,
                selectedViews = targetViewIds.Distinct().Count(),
                matched,
                canPlace = canPlaceCount,
                matchedButCannotPlace,
                unmatched,
                skippedViews = skippedViewIds.Length,
                proposals = results
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }
}
