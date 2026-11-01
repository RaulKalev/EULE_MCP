using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Annotation;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Placement;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>Reports text notes overlapping other annotation in a view or on a sheet, without exporting an image (#141).</summary>
public class CheckAnnotationOverlapsTool : IRevitMcpTool
{
    private const double FeetToMm = 304.8;
    private const int DefaultLimit = 200;

    public string Name => "revit_check_annotation_overlaps";
    public string Description =>
        "For a view or sheet (viewId, or sheetNumber; default the active view), reports text notes whose box (the text, not " +
        "its leaders) overlaps another text note, a tag or a detail line, or crosses a viewport's edge on a sheet, with both ids and the " +
        "overlap in mm (overlapXmm, overlapYmm; on a line its length inside the note), also in paper mm for a scaled view. " +
        "minOverlapMm (paper mm, default 0.5) ignores touching. A sheet also checks each view placed on it, on its own. Title block labels are not checked: the Revit API does not " +
        "expose their areas, use revit_get_titleblock_values and revit_export_view_image for those. Read-only.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return Task.FromResult(documentFailure!);
        var doc = target.Document;
        var uidoc = ToolDocument.UiDocumentOf(uiapp, target);
        var args = request.Arguments;

        View? view;
        var sheetNumber = ToolArguments.GetString(args, "sheetNumber").Trim();
        if (sheetNumber.Length > 0)
        {
            view = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .FirstOrDefault(s => string.Equals(s.SheetNumber, sheetNumber, StringComparison.OrdinalIgnoreCase));
            if (view == null) return Task.FromResult(Fail(request, $"No sheet with number '{sheetNumber}'."));
        }
        else
        {
            string? viewError;
            (view, viewError) = PlacementHelpers.ResolveGraphicalView(uidoc, doc, ToolArguments.GetLong(args, "viewId"));
            if (view == null) return Task.FromResult(Fail(request, viewError!));
        }

        var minOverlapPaperMm = ToolArguments.GetDouble(args, "minOverlapMm", 0.5);
        var limit = ToolArguments.GetInt(args, "limit", DefaultLimit);
        if (limit <= 0) limit = DefaultLimit;

        // What a sheet shows lives in its views: a legend's notes are the legend's elements, not the sheet's.
        var isSheet = view is ViewSheet;
        var scans = new List<Scan> { ScanView(doc, view, isSheet, minOverlapPaperMm, cancellationToken) };
        if (isSheet)
            foreach (var viewportId in ((ViewSheet)view).GetAllViewports())
                if (doc.GetElement(viewportId) is Viewport viewport && doc.GetElement(viewport.ViewId) is View placed)
                    scans.Add(ScanView(doc, placed, false, minOverlapPaperMm, cancellationToken));

        var findings = scans
            .SelectMany(scan => scan.Findings.Select(finding => (Scan: scan, Finding: finding)))
            .OrderByDescending(f => Math.Max(f.Finding.OverlapXmm, f.Finding.OverlapYmm) / f.Scan.Scale)
            .ToList();
        var warnings = new List<string>();
        if (findings.Count > limit)
            warnings.Add($"Returned the {limit} largest of {findings.Count} overlaps. Raise limit to see the rest.");

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{findings.Count} overlap(s) in '{view.Name}': checked {scans.Sum(s => s.NoteCount)} text note(s), {scans.Sum(s => s.TagCount)} tag(s), {scans.Sum(s => s.LineCount)} detail line(s) and {scans.Sum(s => s.ViewportCount)} viewport(s) in {scans.Count} view(s).",
            Data = new
            {
                viewId = view.Id.Value,
                viewName = view.Name,
                isSheet,
                checkedViews = scans.Select(s => new
                {
                    viewId = s.View.Id.Value,
                    viewName = s.View.Name,
                    viewScale = s.Scale,
                    coordinateFrame = s.View is ViewSheet ? "sheet" : s.Frame,
                    textNotes = s.NoteCount
                }).ToList(),
                overlapCount = findings.Count,
                overlaps = findings.Take(limit).Select(f => new
                {
                    viewId = f.Scan.View.Id.Value,
                    viewName = f.Scan.View.Name,
                    textNoteId = f.Finding.TextNoteId,
                    text = f.Finding.Text,
                    otherId = f.Finding.OtherId,
                    otherKind = f.Finding.OtherKind,
                    otherLabel = f.Finding.OtherLabel.Length == 0 ? null : f.Finding.OtherLabel,
                    overlapXmm = Math.Round(f.Finding.OverlapXmm, 1),
                    overlapYmm = Math.Round(f.Finding.OverlapYmm, 1),
                    overlapPaperXmm = Math.Round(f.Finding.OverlapXmm / f.Scan.Scale, 2),
                    overlapPaperYmm = Math.Round(f.Finding.OverlapYmm / f.Scan.Scale, 2)
                }).ToList(),
                notChecked = isSheet
                    ? "Title block label areas: the Revit API does not expose them. Text in one viewport against another viewport's content."
                    : null
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private sealed class Scan
    {
        public View View = null!;
        public int Scale;
        public string Frame = string.Empty;
        public int NoteCount;
        public int TagCount;
        public int LineCount;
        public int ViewportCount;
        public List<OverlapFinding> Findings = new();
    }

    private static Scan ScanView(Document doc, View view, bool isSheet, double minOverlapPaperMm, CancellationToken cancellationToken)
    {
        var scale = isSheet || view.Scale <= 0 ? 1 : view.Scale;
        var frame = new ViewPlaneFrame(view);
        var notes = new List<OverlapItem>();
        var tags = new List<OverlapItem>();
        var lines = new List<OverlapSegment>();
        var viewports = new List<OverlapItem>();

        // A sheet's own annotation; a view's visible annotation, also what a dependent view shows of its parent.
        var collector = isSheet
            ? new FilteredElementCollector(doc).OwnedByView(view.Id)
            : new FilteredElementCollector(doc, view.Id);
        foreach (var element in collector.WhereElementIsNotElementType())
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (element)
            {
                case TextNote note:
                    AddBox(notes, note, "textNote", note.Text?.Trim() ?? string.Empty, frame.TextBoxOf(note, view));
                    break;
                case IndependentTag:
                case SpatialElementTag:
                    AddBox(tags, element, "tag", string.Empty, frame.BoxOf(element, view));
                    break;
                case CurveElement curve when curve.ViewSpecific:
                    AddSegments(lines, curve, frame);
                    break;
                case Viewport viewport when isSheet:
                    var outline = viewport.GetBoxOutline();
                    viewports.Add(new OverlapItem
                    {
                        Id = viewport.Id.Value,
                        Kind = "viewport",
                        Label = doc.GetElement(viewport.ViewId)?.Name ?? string.Empty,
                        MinX = outline.MinimumPoint.X * FeetToMm,
                        MinY = outline.MinimumPoint.Y * FeetToMm,
                        MaxX = outline.MaximumPoint.X * FeetToMm,
                        MaxY = outline.MaximumPoint.Y * FeetToMm
                    });
                    break;
            }
        }

        return new Scan
        {
            View = view,
            Scale = scale,
            Frame = frame.Name,
            NoteCount = notes.Count,
            TagCount = tags.Count,
            LineCount = lines.Select(l => l.Id).Distinct().Count(),
            ViewportCount = viewports.Count,
            Findings = AnnotationOverlapMath.Find(notes, tags, lines, viewports, minOverlapPaperMm * scale)
        };
    }

    private static void AddBox(List<OverlapItem> items, Element element, string kind, string label, ViewBox? found)
    {
        if (found is not { } box) return;
        items.Add(new OverlapItem
        {
            Id = element.Id.Value,
            Kind = kind,
            Label = label,
            MinX = box.MinX,
            MinY = box.MinY,
            MaxX = box.MaxX,
            MaxY = box.MaxY
        });
    }

    private static void AddSegments(List<OverlapSegment> lines, CurveElement curve, ViewPlaneFrame frame)
    {
        IList<XYZ> points;
        try { points = curve.GeometryCurve.Tessellate(); }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { return; }

        for (var i = 0; i + 1 < points.Count; i++)
        {
            var (x1, y1) = frame.FromModel(points[i]);
            var (x2, y2) = frame.FromModel(points[i + 1]);
            lines.Add(new OverlapSegment { Id = curve.Id.Value, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 });
        }
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
