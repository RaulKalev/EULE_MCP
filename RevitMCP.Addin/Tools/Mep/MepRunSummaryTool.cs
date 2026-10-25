using System.Diagnostics;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Mep;
using RevitMCP.Core.Models;
using RevitMCP.Core.Safety;

namespace RevitMCP.Addin.Tools.Mep;

/// <summary>revit_get_mep_run_summary (#106): tray and conduit lengths, fittings, open ends and disconnected segments.</summary>
public sealed class MepRunSummaryTool : IRevitMcpTool
{
    public string Name => "revit_get_mep_run_summary";

    public string Description =>
        "Summarises cable trays and conduits (read-only): count and length (mm and m) per type, size (300x60, Ø25), level and " +
        "workset, and per type × size × level × workset (the rows a schedule shows; lengths are Revit's Length parameter); " +
        "fittings counted by family type; open ends (unconnected end connectors) with their position in mm, up to 500; " +
        "disconnected segments (every end open), up to 500. Optional filters: kind (cableTray|conduit), types (names or ids), " +
        "levels (names or ids), viewId (only what that view shows), runId (a run drawn by revit_create_mep_run). Publish the " +
        "open-ends list with revit_publish_page (one row per end, data-revit-id) so the user can jump to each one.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return Task.FromResult(failure!);
        var doc = target.Document;

        var kindText = ToolArguments.GetString(request.Arguments, "kind").Trim();
        var kind = kindText.Length == 0 ? null : MepKind.Normalize(kindText);
        if (kindText.Length > 0 && kind == null)
            return Task.FromResult(Fail(request, $"kind '{kindText}' is not known: use 'cableTray', 'conduit' or leave it empty for both."));

        var viewId = ToolArguments.GetLong(request.Arguments, "viewId");
        if (viewId > 0)
        {
            var view = doc.GetElement(new ElementId(viewId)) as View;
            if (view == null)
                return Task.FromResult(Fail(request, $"Element {viewId} is not a view."));
            if (view.IsTemplate || view is ViewSchedule || view is ViewSheet)
                return Task.FromResult(Fail(request, $"View '{view.Name}' is a {(view.IsTemplate ? "view template" : view.ViewType.ToString())}: it shows no model elements. Pass a plan, section or 3D view."));
        }

        var filter = new MepSummaryFilter
        {
            Kind = kind,
            Types = MepArgs.Strings(request.Arguments, "types"),
            Levels = MepArgs.Strings(request.Arguments, "levels"),
            ViewId = viewId,
            RunId = ToolArguments.GetString(request.Arguments, "runId").Trim(),
            ScanLimit = QueryLimits.Default.MaxScanElements
        };

        var scan = MepRunSummaryService.Scan(doc, filter);
        var s = scan.Summary;
        var warnings = new List<string>(scan.Warnings);
        if (filter.RunId.Length > 0 && s.Segments == 0)
            warnings.Add($"No element carries run id '{filter.RunId}'. The id comes from revit_create_mep_run (data.runId).");
        if (s.OpenEndsTruncated)
            warnings.Add($"{s.OpenEndCount} open ends; the first {s.OpenEnds.Count} are listed. Narrow with levels, viewId or runId.");

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{s.Segments} segment(s), {s.TotalLengthM.ToString("0.###", CultureInfo.InvariantCulture)} m" +
                      string.Concat(s.TotalByKindM.Select(k => $" ({MepKind.Label(k.Key)} {k.Value.ToString("0.###", CultureInfo.InvariantCulture)} m)")) +
                      $", {s.Fittings} fitting(s), {s.OpenEndCount} open end(s), {s.DisconnectedCount} disconnected segment(s).",
            Data = new
            {
                filters = new
                {
                    kind,
                    types = filter.Types,
                    levels = filter.Levels,
                    viewId = viewId > 0 ? viewId : (long?)null,
                    runId = filter.RunId.Length > 0 ? filter.RunId : null
                },
                totals = new
                {
                    segments = s.Segments,
                    fittings = s.Fittings,
                    lengthMm = s.TotalLengthMm,
                    lengthM = s.TotalLengthM,
                    byKindM = s.TotalByKindM
                },
                byType = s.ByType.Select(Group),
                bySize = s.BySize.Select(Group),
                byLevel = s.ByLevel.Select(Group),
                byWorkset = doc.IsWorkshared ? s.ByWorkset.Select(Group) : null,
                groups = s.Groups.Select(Group),
                fittingsByType = s.FittingsByType.Select(g => new { kind = g.Kind, type = g.Key, count = g.Count }),
                openEnds = new
                {
                    count = s.OpenEndCount,
                    listed = s.OpenEnds.Count,
                    truncated = s.OpenEndsTruncated,
                    items = s.OpenEnds.Select(o => new
                    {
                        elementId = o.ElementId, kind = o.Kind, element = o.ElementClass, type = o.TypeName, level = o.Level,
                        xMm = o.Position.X, yMm = o.Position.Y, zMm = o.Position.Z
                    })
                },
                disconnectedSegments = new
                {
                    count = s.DisconnectedCount,
                    truncated = s.DisconnectedTruncated,
                    elementIds = s.DisconnectedSegmentIds
                },
                scanned = scan.Scanned,
                scanTruncated = scan.ScanTruncated
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static object Group(MepLengthGroup g) => new
    {
        kind = g.Kind, key = g.Key, type = g.Type, size = g.Size, level = g.Level, workset = g.Workset,
        count = g.Count, lengthMm = g.LengthMm, lengthM = g.LengthM
    };

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Status = MepStatus.ValidationFailed, Message = message };
}
