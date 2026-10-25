using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documentation.Placement;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Schedules;
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
        "matchMode (ExactName|Contains|Fuzzy|PlaceViews|SheetNumberPrefix|SheetNumberSuffix|CustomParameter, default Contains). " +
        "PlaceViews reproduces the source plugin's sheet-first exact/number/word matcher, recognizes Estonian floor ordinals 1-99, " +
        "centers each viewport on the sheet outline, " +
        "and requires sheetIds or allSheets=true. " +
        "fuzzyThreshold (double 0-1, default 0.6), customParamName (string), " +
        "skipAlreadyPlaced (bool; default false for PlaceViews and true for other modes). " +
        "Schedule ids are placed as schedule instances (centred on the title block; one already on the sheet is skipped). " +
        "Use revit_preview_place_views_on_sheets first to verify proposals.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw    = Stopwatch.StartNew();
        var documentTarget = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (documentTarget == null) return Task.FromResult(documentFailure!);
        var doc = documentTarget.Document;
        var uidoc = ToolDocument.UiDocumentOf(uiapp, documentTarget);
        if (ToolArguments.GetBool(request.Arguments, "useSelection") && uidoc == null)
            return Task.FromResult(ToolDocument.ActiveRequired(request, "useSelection", documentTarget.Document.Title));

        var viewIds         = ToolArguments.GetLongArray(request.Arguments, "viewIds");
        var targetSheetId   = ToolArguments.GetLong(request.Arguments, "targetSheetId", 0L);
        var sheetIds        = ToolArguments.GetLongArray(request.Arguments, "sheetIds");
        var allSheets       = ToolArguments.GetBool(request.Arguments, "allSheets", false);
        var matchMode       = ToolArguments.GetString(request.Arguments, "matchMode", ViewSheetMatchingService.ModeContains);
        var placeViewsMode  = targetSheetId <= 0 && PlaceViewsMatchingService.IsPlaceViewsMode(matchMode);
        var fuzzyThreshold  = GetDouble(request.Arguments, "fuzzyThreshold", 0.6);
        var customParamName = ToolArguments.GetString(request.Arguments, "customParamName");
        var skipPlaced      = ToolArguments.GetBool(request.Arguments, "skipAlreadyPlaced", !placeViewsMode);

        if (viewIds.Length == 0)
            return Task.FromResult(Fail(request, "viewIds is required."));
        if (placeViewsMode && sheetIds.Length == 0 && !allSheets)
            return Task.FromResult(Fail(request, "PlaceViews mode requires sheetIds or allSheets=true."));

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

            cancellationToken.ThrowIfCancellationRequested();
            using var t = new Transaction(doc, "Revit MCP - Place Views on Sheets");
            t.Start();
            foreach (var vid in toPlace)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var view = doc.GetElement(new ElementId(vid)) as View;
                    if (view == null) { warnings.Add($"View {vid} not found."); continue; }
                    if (view is ViewSchedule schedule)
                    {
                        // #103: Viewport.Create does not take schedules; they go on as a ScheduleSheetInstance.
                        var placedSchedule = PlaceSchedule(doc, targetSheet, schedule, warnings);
                        if (placedSchedule != null) { placed++; placed_.Add(placedSchedule); }
                        continue;
                    }
                    var center = PlacementPointResolver.GetSheetCenter(doc, targetSheet);
                    Viewport.Create(doc, targetSheet.Id, view.Id, center);
                    placed++;
                    placed_.Add(new { viewId = vid, viewName = view.Name, sheetId = targetSheetId, sheetNumber = targetSheet.SheetNumber });
                }
                catch (Exception ex) { warnings.Add($"Failed to place view {vid}: {ex.Message}"); }
            }
            RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(t);

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
        else
        {
            candidateSheets = allSheets || sheetIds.Length == 0
                ? new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                : new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                    .Where(s => sheetIds.ToHashSet().Contains(s.Id.Value));
        }

        if (placeViewsMode)
        {
            return Task.FromResult(ExecutePlaceViewsMode(
                doc,
                request,
                targetIds,
                candidateSheets,
                sw,
                cancellationToken));
        }

        var proposals = ViewSheetMatchingService.Match(doc, targetIds, candidateSheets, matchMode, fuzzyThreshold, customParamName);

        var toPlaceB = proposals.Where(p => p.SheetId.HasValue).ToList();
        if (toPlaceB.Count == 0)
            return Task.FromResult(Fail(request, "No views could be matched to sheets. Run preview first, or use targetSheetId for direct placement."));

        int placed2   = 0;
        var warnings2 = new List<string>();
        var placed2_  = new List<object>();

        cancellationToken.ThrowIfCancellationRequested();
        using var t2 = new Transaction(doc, "Revit MCP - Place Views on Sheets");
        t2.Start();
        foreach (var p in toPlaceB)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var sheet  = doc.GetElement(new ElementId(p.SheetId!.Value)) as ViewSheet;
                var view   = doc.GetElement(new ElementId(p.ViewId))         as View;
                if (sheet == null || view == null) { warnings2.Add($"Could not resolve view/sheet for view {p.ViewId}"); continue; }
                if (view is ViewSchedule schedule)
                {
                    var placedSchedule = PlaceSchedule(doc, sheet, schedule, warnings2);
                    if (placedSchedule != null) { placed2++; placed2_.Add(placedSchedule); }
                    continue;
                }

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
        RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(t2);

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

    private static McpToolResult ExecutePlaceViewsMode(
        Document doc,
        McpToolRequest request,
        long[] targetViewIds,
        IEnumerable<ViewSheet> candidateSheets,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        var sheets = candidateSheets.ToList();
        if (sheets.Count == 0)
            return Fail(request, "No valid target sheets were found.");

        var proposals = PlaceViewsMatchingService.Match(doc, targetViewIds, sheets);
        var matched = proposals.Count(proposal => proposal.ViewId.HasValue);
        if (matched == 0)
            return Fail(request, "No selected views matched the selected sheet names in PlaceViews mode.");

        var warnings = new List<string>();
        var results = new List<object>();
        var placed = 0;

        cancellationToken.ThrowIfCancellationRequested();
        using var transaction = new Transaction(doc, "Revit MCP - Place Views on Sheets");
        transaction.Start();

        foreach (var proposal in proposals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!proposal.ViewId.HasValue)
            {
                warnings.Add($"Sheet '{proposal.SheetNumber} - {proposal.SheetName}' had no matching selected view.");
                continue;
            }

            var sheet = doc.GetElement(new ElementId(proposal.SheetId)) as ViewSheet;
            var view = doc.GetElement(new ElementId(proposal.ViewId.Value)) as View;
            if (sheet == null || view == null)
            {
                warnings.Add($"Could not resolve the proposed view or sheet for sheet ID {proposal.SheetId}.");
                continue;
            }

            if (view is ViewSchedule schedule)
            {
                using var scheduleTransaction = new SubTransaction(doc);
                scheduleTransaction.Start();
                try
                {
                    var placedSchedule = PlaceSchedule(doc, sheet, schedule, warnings);
                    if (placedSchedule == null)
                    {
                        scheduleTransaction.RollBack();
                        continue;
                    }
                    scheduleTransaction.Commit();
                    placed++;
                    results.Add(placedSchedule);
                }
                catch (Exception ex)
                {
                    if (scheduleTransaction.GetStatus() == TransactionStatus.Started)
                        scheduleTransaction.RollBack();
                    warnings.Add($"Failed to place schedule '{view.Name}' on sheet '{sheet.SheetNumber}': {ex.Message}");
                }
                continue;
            }

            try
            {
                if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                {
                    warnings.Add(
                        $"Cannot place '{view.Name}' on sheet '{sheet.SheetNumber}' because it is already placed or incompatible.");
                    continue;
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not validate '{view.Name}' for sheet '{sheet.SheetNumber}': {ex.Message}");
                continue;
            }

            using var subTransaction = new SubTransaction(doc);
            subTransaction.Start();
            try
            {
                var center = PlacementPointResolver.GetSheetOutlineCenter(sheet);
                var viewport = Viewport.Create(doc, sheet.Id, view.Id, center);
                subTransaction.Commit();
                placed++;
                results.Add(new
                {
                    viewId = view.Id.Value,
                    viewName = view.Name,
                    viewType = view.ViewType.ToString(),
                    sheetId = sheet.Id.Value,
                    sheetNumber = sheet.SheetNumber,
                    sheetName = sheet.Name,
                    viewportId = viewport.Id.Value,
                    matchScore = proposal.Score,
                    exactMatch = proposal.IsExact,
                    placementPointMm = new { x = Math.Round(center.X * 304.8, 1), y = Math.Round(center.Y * 304.8, 1) }   // X2: paper mm, was feet
                });
            }
            catch (Exception ex)
            {
                if (subTransaction.GetStatus() == TransactionStatus.Started)
                    subTransaction.RollBack();
                warnings.Add($"Failed to place '{view.Name}' on sheet '{sheet.SheetNumber}': {ex.Message}");
            }
        }

        RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(transaction);
        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = placed > 0,
            Message = $"PlaceViews mode placed {placed}/{sheets.Count} selected sheet/view match(es).",
            Data = new
            {
                matchMode = PlaceViewsMatchingService.ModePlaceViews,
                selectedSheets = sheets.Count,
                selectedViews = targetViewIds.Distinct().Count(),
                matched,
                placed,
                failedOrUnmatched = sheets.Count - placed,
                results
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    /// <summary>
    /// #103: a schedule id goes on the sheet with ScheduleSheetInstance.Create (Viewport.Create refuses schedules),
    /// centred on the title block like a viewport. A schedule already on that sheet is skipped with a warning.
    /// </summary>
    private static object? PlaceSchedule(Document doc, ViewSheet sheet, ViewSchedule schedule, List<string> warnings)
    {
        var refusal = ScheduleLookup.Refusal(schedule, forEdit: true);
        if (refusal != null)
        {
            warnings.Add(refusal);
            return null;
        }
        if (ScheduleSheetPlacer.InstancesOn(doc, sheet, schedule.Id).Count > 0)
        {
            warnings.Add($"Schedule '{schedule.Name}' is already on sheet '{sheet.SheetNumber}'; revit_place_schedule_on_sheet with allowDuplicate=true places a second copy.");
            return null;
        }

        var instance = ScheduleSheetPlacer.PlaceCentered(doc, sheet, schedule, warnings, out var box);
        return new
        {
            viewId = schedule.Id.Value,
            viewName = schedule.Name,
            viewType = schedule.ViewType.ToString(),
            kind = "schedule",
            sheetId = sheet.Id.Value,
            sheetNumber = sheet.SheetNumber,
            sheetName = sheet.Name,
            scheduleSheetInstanceId = instance.Id.Value,
            boxMm = box is { } b
                ? new { minX = Math.Round(b.MinX, 1), minY = Math.Round(b.MinY, 1), maxX = Math.Round(b.MaxX, 1), maxY = Math.Round(b.MaxY, 1) }
                : null
        };
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
