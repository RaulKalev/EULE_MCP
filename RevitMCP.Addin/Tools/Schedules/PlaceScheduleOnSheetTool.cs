using System.Diagnostics;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Documentation.Placement;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Schedules;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Schedules;

/// <summary>revit_place_schedule_on_sheet (#103): ScheduleSheetInstance.Create at a point or next to a viewport.</summary>
public sealed class PlaceScheduleOnSheetTool : IRevitMcpTool
{
    public string Name => "revit_place_schedule_on_sheet";

    public string Description =>
        "Places a schedule on a sheet. Requires approval. schedule (name or id) or scheduleId; sheet (sheet number or " +
        "id). Position: xMm,yMm — the table's top-left corner in sheet mm; or nextTo {viewportId (a viewport, a view " +
        "placed on the sheet, or a placed schedule), side: right|below|left|above (default right), gapMm (default 10)} — " +
        "tops aligned for right/left, left edges for below/above; neither: centred on the title block. The placed table " +
        "is measured and moved so its corner lands exactly there; overlaps with other viewports and leaving the title " +
        "block are reported. A schedule already on that sheet is refused unless allowDuplicate=true.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Schedules;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return Task.FromResult(documentFailure!);
        var doc = target.Document;
        var args = request.Arguments;

        var schedule = ScheduleLookup.Resolve(doc, request, ToolArguments.GetString(args, "schedule"),
            ToolArguments.GetLong(args, "scheduleId"), forEdit: true, out var failure);
        if (schedule == null) return Task.FromResult(failure!);

        var sheet = ResolveSheet(doc, ToolArguments.GetString(args, "sheet"), out var sheetError);
        if (sheet == null) return Task.FromResult(ScheduleToolSupport.Fail(request, "validation_failed", sheetError!, sw: sw));

        var existing = ScheduleSheetPlacer.InstancesOn(doc, sheet, schedule.Id);
        if (existing.Count > 0 && !ToolArguments.GetBool(args, "allowDuplicate"))
            return Task.FromResult(ScheduleToolSupport.Fail(request, "validation_failed",
                $"'{schedule.Name}' is already on sheet {sheet.SheetNumber} (instance {string.Join(", ", existing.Select(i => i.Id.Value))}). Pass allowDuplicate=true to place it again.",
                new { existingInstanceIds = existing.Select(i => i.Id.Value).ToList() }, sw: sw));

        // Position: nextTo, an explicit point, or the title-block centre.
        Func<double, double, (double X, double Y)> topLeftFor;
        string mode;
        object? anchorInfo = null;
        var hasX = args.TryGetValue("xMm", out var rawX) && rawX != null && !(rawX is JValue { Type: JTokenType.Null });
        var hasY = args.TryGetValue("yMm", out var rawY) && rawY != null && !(rawY is JValue { Type: JTokenType.Null });
        var nextTo = NextTo(args);
        if (nextTo != null)
        {
            if (!SchedulePlacement.TryParseSide(nextTo.Side, out var side))
                return Task.FromResult(ScheduleToolSupport.Fail(request, "validation_failed", $"nextTo.side '{nextTo.Side}' is not right, below, left or above.", sw: sw));
            var anchor = AnchorBox(doc, sheet, nextTo.ElementId, out var anchorLabel, out var anchorError);
            if (anchor == null) return Task.FromResult(ScheduleToolSupport.Fail(request, "validation_failed", anchorError!, sw: sw));
            var box = anchor.Value;
            var gap = nextTo.GapMm ?? SchedulePlacement.DefaultGapMm;
            topLeftFor = (w, h) => SchedulePlacement.NextTo(box, side, gap, w, h);
            mode = $"next to {anchorLabel}, {side}, gap {gap.ToString("0.#", CultureInfo.InvariantCulture)} mm";
            anchorInfo = new { elementId = nextTo.ElementId, label = anchorLabel, side, gapMm = gap, boxMm = Rect(box) };
        }
        else if (hasX || hasY)
        {
            if (!(hasX && hasY))
                return Task.FromResult(ScheduleToolSupport.Fail(request, "validation_failed", "Give both xMm and yMm (the table's top-left corner on the sheet).", sw: sw));
            var x = ToolArguments.GetDouble(args, "xMm");
            var y = ToolArguments.GetDouble(args, "yMm");
            topLeftFor = (_, _) => (x, y);
            mode = $"top-left at ({x.ToString("0.#", CultureInfo.InvariantCulture)}, {y.ToString("0.#", CultureInfo.InvariantCulture)}) mm";
        }
        else
        {
            var center = PlacementPointResolver.GetSheetCenter(doc, sheet);
            var centerMm = (ScheduleSheetPlacer.FtToMm(center.X), ScheduleSheetPlacer.FtToMm(center.Y));
            topLeftFor = (w, h) => SchedulePlacement.CenteredTopLeft(centerMm, w, h);
            mode = "centred on the title block";
        }

        var warnings = new List<string>();
        ScheduleSheetInstance? instance = null;
        SheetRect? placedBox = null;
        cancellationToken.ThrowIfCancellationRequested();
        var (committed, diagnostics) = RevitTransactionRunner.Run(doc, "Revit MCP - Place Schedule on Sheet", () =>
        {
            instance = ScheduleSheetPlacer.Place(doc, sheet, schedule, topLeftFor, warnings, out placedBox);
        });

        if (!committed || instance == null)
        {
            warnings.AddRange(diagnostics.ToErrorLines());
            return Task.FromResult(ScheduleToolSupport.Fail(request, "transaction_failed",
                $"'{schedule.Name}' was not placed on sheet {sheet.SheetNumber}: {diagnostics.OriginalError ?? "the transaction did not commit"}.",
                new { transaction = diagnostics }, warnings, sw));
        }
        warnings.AddRange(diagnostics.FailureMessages);

        return Task.FromResult(ScheduleToolSupport.Ok(request, sw,
            $"Placed '{schedule.Name}' on sheet {sheet.SheetNumber} ({mode})" + (warnings.Count > 0 ? "; see warnings." : "."),
            new
            {
                instanceId = instance.Id.Value,
                scheduleId = schedule.Id.Value,
                scheduleName = schedule.Name,
                sheet = new { id = sheet.Id.Value, number = sheet.SheetNumber, name = sheet.Name },
                placement = mode,
                anchor = anchorInfo,
                boxMm = placedBox is { } b ? Rect(b) : null,
                duplicateOf = existing.Count > 0 ? existing.Select(i => i.Id.Value).ToList() : null
            }, warnings));
    }

    private sealed class NextToSpec
    {
        public long ElementId;
        public string? Side;
        public double? GapMm;
    }

    private static NextToSpec? NextTo(Dictionary<string, object?> args)
    {
        if (!args.TryGetValue("nextTo", out var raw) || raw == null) return null;
        var token = ScheduleSpecParser.Token(raw);
        if (token is not JObject obj) return null;
        var id = obj["viewportId"] ?? obj["elementId"] ?? obj["viewId"];
        if (id == null || id.Type == JTokenType.Null) return null;
        return new NextToSpec
        {
            ElementId = long.TryParse(id.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0,
            Side = (string?)obj["side"],
            GapMm = obj["gapMm"] is { Type: JTokenType.Integer or JTokenType.Float } gap ? gap.Value<double>() : null
        };
    }

    /// <summary>The box next to which the schedule goes: a viewport, a view placed on the sheet, or a placed schedule.</summary>
    private static SheetRect? AnchorBox(Document doc, ViewSheet sheet, long elementId, out string label, out string? error)
    {
        label = string.Empty;
        error = null;
        var element = doc.GetElement(new ElementId(elementId));
        switch (element)
        {
            case Viewport viewport when viewport.SheetId == sheet.Id:
                label = $"viewport {elementId} ('{(doc.GetElement(viewport.ViewId) as View)?.Name}')";
                return ScheduleSheetPlacer.ViewportBox(viewport);
            case Viewport viewport:
                error = $"Viewport {elementId} is on another sheet ({(doc.GetElement(viewport.SheetId) as ViewSheet)?.SheetNumber}), not on {sheet.SheetNumber}.";
                return null;
            case ScheduleSheetInstance instance when instance.OwnerViewId == sheet.Id:
                label = $"schedule {elementId} ('{instance.Name}')";
                return ScheduleSheetPlacer.BoxOf(instance, sheet) ?? Missing(out error, elementId);
            case View view:
                foreach (var id in sheet.GetAllViewports())
                    if (doc.GetElement(id) is Viewport vp && vp.ViewId == view.Id)
                    {
                        label = $"viewport {id.Value} of view '{view.Name}'";
                        return ScheduleSheetPlacer.ViewportBox(vp);
                    }
                error = $"View '{view.Name}' is not placed on sheet {sheet.SheetNumber}.";
                return null;
            default:
                error = element == null
                    ? $"nextTo.viewportId {elementId}: no such element."
                    : $"nextTo.viewportId {elementId} is a {element.GetType().Name}, not a viewport on sheet {sheet.SheetNumber} (revit_get_sheet_viewports lists them).";
                return null;
        }
    }

    private static SheetRect? Missing(out string? error, long id)
    {
        error = $"Revit gave no box for element {id}.";
        return null;
    }

    /// <summary>A sheet by element id, sheet number or sheet name.</summary>
    internal static ViewSheet? ResolveSheet(Document doc, string sheet, out string? error)
    {
        error = null;
        var text = (sheet ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            error = "sheet is required: a sheet number or element id (revit_list_sheets lists them).";
            return null;
        }
        var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsTemplate).ToList();
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && doc.GetElement(new ElementId(id)) is ViewSheet byId)
            return byId;
        var hit = sheets.FirstOrDefault(s => string.Equals(s.SheetNumber, text, StringComparison.OrdinalIgnoreCase))
                  ?? sheets.FirstOrDefault(s => string.Equals(s.Name, text, StringComparison.OrdinalIgnoreCase));
        if (hit != null) return hit;
        var near = SchedulePlanner.Suggest(text, sheets.Select(s => s.SheetNumber).Concat(sheets.Select(s => s.Name)));
        error = $"No sheet with number, name or id '{text}'." + (near.Count > 0 ? $" Nearest: {string.Join(", ", near)}." : string.Empty);
        return null;
    }

    private static object Rect(SheetRect r) => new
    {
        minX = Math.Round(r.MinX, 1),
        minY = Math.Round(r.MinY, 1),
        maxX = Math.Round(r.MaxX, 1),
        maxY = Math.Round(r.MaxY, 1),
        width = Math.Round(r.Width, 1),
        height = Math.Round(r.Height, 1)
    };
}
