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
        "For a view or sheet (viewId, or sheetNumber; default the active view), reports text notes whose bounding box " +
        "overlaps another text note, a tag or a detail line, or crosses a viewport's edge on a sheet, with both ids and the " +
        "overlap in mm (overlapXmm, overlapYmm; on a line its length inside the note), also in paper mm for a scaled view. " +
        "minOverlapMm (paper mm, default 0.5) ignores touching. Title block labels are not checked: the Revit API does not " +
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

        var isSheet = view is ViewSheet;
        var scale = isSheet || view.Scale <= 0 ? 1 : view.Scale;
        var minOverlapPaperMm = ToolArguments.GetDouble(args, "minOverlapMm", 0.5);
        var limit = ToolArguments.GetInt(args, "limit", DefaultLimit);
        if (limit <= 0) limit = DefaultLimit;

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
                    AddBox(notes, element, "textNote", note.Text?.Trim() ?? string.Empty, frame, view);
                    break;
                case IndependentTag:
                case SpatialElementTag:
                    AddBox(tags, element, "tag", string.Empty, frame, view);
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

        var findings = AnnotationOverlapMath.Find(notes, tags, lines, viewports, minOverlapPaperMm * scale);
        var warnings = new List<string>();
        if (findings.Count > limit)
            warnings.Add($"Returned the {limit} largest of {findings.Count} overlaps. Raise limit to see the rest.");

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{findings.Count} overlap(s) in '{view.Name}': checked {notes.Count} text note(s) against {notes.Count - 1 + tags.Count} other text and tag box(es), {lines.Select(l => l.Id).Distinct().Count()} detail line(s) and {viewports.Count} viewport(s).",
            Data = new
            {
                viewId = view.Id.Value,
                viewName = view.Name,
                isSheet,
                viewScale = scale,
                coordinateFrame = isSheet ? "sheet" : frame.Name,
                overlapCount = findings.Count,
                overlaps = findings.Take(limit).Select(f => new
                {
                    textNoteId = f.TextNoteId,
                    text = f.Text,
                    otherId = f.OtherId,
                    otherKind = f.OtherKind,
                    otherLabel = f.OtherLabel.Length == 0 ? null : f.OtherLabel,
                    overlapXmm = Math.Round(f.OverlapXmm, 1),
                    overlapYmm = Math.Round(f.OverlapYmm, 1),
                    overlapPaperXmm = Math.Round(f.OverlapXmm / scale, 2),
                    overlapPaperYmm = Math.Round(f.OverlapYmm / scale, 2)
                }).ToList(),
                notChecked = isSheet
                    ? "Title block label areas: the Revit API does not expose them."
                    : null
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static void AddBox(List<OverlapItem> items, Element element, string kind, string label, ViewPlaneFrame frame, View view)
    {
        if (frame.BoxOf(element, view) is not { } box) return;
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
