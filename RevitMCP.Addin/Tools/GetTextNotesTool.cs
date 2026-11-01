using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Annotation;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class GetTextNotesTool : IRevitMcpTool
{
    public string Name => "revit_get_text_notes";
    public string Description => "Returns text note elements (user-placed text boxes via the Text command) from the active document. By default returns text notes in the active view. Pass viewId=0 to get text notes from all views, or a specific view element ID to scope to that view. Supports text content filtering and selection-based reading. Each note has its boundingBox (minX, minY, maxX, maxY in view or sheet mm) and lineCount (estimated rows including wrapping; paragraphCount is the hard line breaks), to plan space before writing.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var documentTarget = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (documentTarget == null) return Task.FromResult(documentFailure!);
        var doc = documentTarget.Document;
        var uidoc = ToolDocument.UiDocumentOf(uiapp, documentTarget);
        if (ToolArguments.GetBool(request.Arguments, "useSelection") && uidoc == null)
            return Task.FromResult(ToolDocument.ActiveRequired(request, "useSelection", documentTarget.Document.Title));
        var useSelection = ToolArguments.GetBool(request.Arguments, "useSelection");
        var viewId = ToolArguments.GetLong(request.Arguments, "viewId", -1L);
        var textFilter = ToolArguments.GetString(request.Arguments, "textFilter");
        var limit = ToolArguments.GetInt(request.Arguments, "limit", 200);

        var warnings = new List<string>();
        IEnumerable<TextNote> notes;

        if (useSelection)
        {
            var selIds = uidoc!.Selection.GetElementIds();
            var selNotes = new List<TextNote>();
            int skipped = 0;
            foreach (var id in selIds)
            {
                if (doc.GetElement(id) is TextNote tn)
                    selNotes.Add(tn);
                else
                    skipped++;
            }
            if (skipped > 0)
                warnings.Add($"{skipped} selected element(s) are not text notes and were skipped.");
            notes = selNotes;
        }
        else if (viewId == 0)
        {
            // All views
            notes = new FilteredElementCollector(doc)
                .OfClass(typeof(TextNote))
                .Cast<TextNote>();
        }
        else
        {
            // Active view (default) or explicitly provided view id
            ElementId scopeViewId;
            if (viewId > 0)
            {
                scopeViewId = new ElementId(viewId);
            }
            else
            {
                if (uidoc == null)
                    return Task.FromResult(Fail(request, $"'{doc.Title}' is not the active document, so it has no active view: pass viewId, or viewId=0 for all views."));
                var activeView = uidoc.ActiveView;
                if (activeView == null)
                    return Task.FromResult(Fail(request, "No active view. Pass viewId=0 to collect from all views."));
                scopeViewId = activeView.Id;
            }

            notes = SetTextNotesExecutor.TextNotesInView(doc, scopeViewId);
        }

        // Apply text filter
        if (!string.IsNullOrEmpty(textFilter))
        {
            notes = notes.Where(n =>
                n.Text.IndexOf(textFilter, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        var results = new List<object>();
        int total = 0;

        foreach (var note in notes)
        {
            if (cancellationToken.IsCancellationRequested) break;
            total++;
            if (results.Count >= limit) continue; // count total but don't add more

            var origin = note.Coord;
            var ownerView = doc.GetElement(note.OwnerViewId) as View;

            var typeElem = doc.GetElement(note.GetTypeId()) as TextNoteType;
            double fontSizeMm = 0;
            if (typeElem != null)
            {
                var sizeParam = typeElem.get_Parameter(BuiltInParameter.TEXT_SIZE);
                if (sizeParam != null)
                    fontSizeMm = Math.Round(sizeParam.AsDouble() * 304.8, 3);
            }

            var box = BoxOf(note, ownerView);
            var widthFactor = typeElem?.get_Parameter(BuiltInParameter.TEXT_WIDTH_SCALE)?.AsDouble() ?? 1.0;

            results.Add(new
            {
                elementId = note.Id.Value,
                text = note.Text,
                ownerViewId = note.OwnerViewId.Value,
                ownerViewName = ownerView?.Name ?? string.Empty,
                originX_mm = Math.Round(origin.X * 304.8, 1),
                originY_mm = Math.Round(origin.Y * 304.8, 1),
                originZ_mm = Math.Round(origin.Z * 304.8, 1),
                width_mm = Math.Round(note.Width * 304.8, 1),
                fontSizeMm,
                boundingBox = box == null ? null : new
                {
                    minX = Math.Round(box.Value.MinX, 1),
                    minY = Math.Round(box.Value.MinY, 1),
                    maxX = Math.Round(box.Value.MaxX, 1),
                    maxY = Math.Round(box.Value.MaxY, 1)
                },
                lineCount = TextNoteEditPlanner.EstimateLineCount(note.Text, note.Width * 304.8, fontSizeMm, widthFactor),
                paragraphCount = (note.Text ?? string.Empty).TrimEnd('\r', '\n').Split('\r').Length
            });
        }

        if (total > limit)
            warnings.Add($"Returned {limit} of {total} matching text notes. Increase limit or narrow the scope.");

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Returned {results.Count} of {total} text note(s).",
            Data = new
            {
                totalMatched = total,
                returned = results.Count,
                textNotes = results
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    /// <summary>The note's box in its view; null rather than a failed read when the view cannot report its frame.</summary>
    private static ViewBox? BoxOf(TextNote note, View? ownerView)
    {
        if (ownerView == null) return null;
        try { return new ViewPlaneFrame(ownerView).TextBoxOf(note, ownerView); }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; }
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
