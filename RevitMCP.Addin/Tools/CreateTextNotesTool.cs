using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Placement;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class CreateTextNotesTool : IRevitMcpTool
{
    public string Name => "revit_create_text_notes";
    public string Description => "Creates text notes in a view at given positions (mm, model coordinates). Each note: {text, x, y, z, widthMm, rotationDegrees}. Uses the default text note type unless typeId or typeName is given. Requires approval. Transaction-wrapped and reversible via Revit Undo.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null)
            return Task.FromResult(Fail(request, "No active document."));
        var doc = uidoc.Document;

        var viewId = ToolArguments.GetLong(request.Arguments, "viewId");
        var typeId = ToolArguments.GetLong(request.Arguments, "typeId");
        var typeName = ToolArguments.GetString(request.Arguments, "typeName");

        if (!request.Arguments.TryGetValue("notes", out var rawNotes) || rawNotes == null)
            return Task.FromResult(Fail(request, "Provide 'notes': a JSON array of {text, x, y, z, widthMm, rotationDegrees} (mm)."));
        var notes = rawNotes as JArray ?? (rawNotes is string s ? ToolArguments.TryParseJArray(s) : null);
        if (notes == null || notes.Count == 0)
            return Task.FromResult(Fail(request, "'notes' could not be parsed as a non-empty JSON array."));

        var (view, viewError) = PlacementHelpers.ResolveGraphicalView(uidoc, doc, viewId);
        if (view == null)
            return Task.FromResult(Fail(request, viewError!));

        var (textTypeId, typeError) = ResolveTextNoteType(doc, typeId, typeName);
        if (textTypeId == null)
            return Task.FromResult(Fail(request, typeError!));

        var created = new List<object>();
        var errors = new List<string>();

        var (txSuccess, diagnostics) = RevitTransactionRunner.Run(doc, "Revit MCP - Create Text Notes", () =>
        {
            int index = 0;
            foreach (var token in notes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                index++;

                var text = PlacementHelpers.TokenString(token, "text");
                if (string.IsNullOrWhiteSpace(text))
                {
                    errors.Add($"Note {index}: 'text' is empty.");
                    continue;
                }

                var position = PlacementHelpers.PointFromMm(
                    PlacementHelpers.TokenDouble(token, "x"),
                    PlacementHelpers.TokenDouble(token, "y"),
                    PlacementHelpers.TokenDouble(token, "z"));
                var widthMm = PlacementHelpers.TokenDouble(token, "widthMm");
                var rotationDegrees = PlacementHelpers.TokenDouble(token, "rotationDegrees");

                try
                {
                    var options = new TextNoteOptions(textTypeId)
                    {
                        Rotation = rotationDegrees * Math.PI / 180.0
                    };

                    TextNote note;
                    if (widthMm > 0)
                    {
                        var width = PlacementHelpers.MmToFt(widthMm);
                        var min = TextNote.GetMinimumAllowedWidth(doc, textTypeId);
                        var max = TextNote.GetMaximumAllowedWidth(doc, textTypeId);
                        var clamped = Math.Min(Math.Max(width, min), max);
                        if (Math.Abs(clamped - width) > 1e-9)
                            errors.Add($"Note {index}: widthMm {widthMm} was outside the allowed range and was clamped.");
                        note = TextNote.Create(doc, view.Id, position, clamped, text, options);
                    }
                    else
                    {
                        note = TextNote.Create(doc, view.Id, position, text, options);
                    }

                    created.Add(new { elementId = note.Id.Value, text });
                }
                catch (Exception ex)
                {
                    errors.Add($"Note {index}: {ex.Message}");
                }
            }
        });

        sw.Stop();

        if (!txSuccess)
        {
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Message = diagnostics.OriginalError ?? "Transaction failed — no text notes were created.",
                Errors = errors,
                Data = new { transactionDiagnostics = diagnostics },
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = created.Count > 0,
            Message = created.Count > 0
                ? $"Created {created.Count} text note(s) in view '{view.Name}'."
                : "No text notes were created.",
            Errors = errors,
            Data = new { viewId = view.Id.Value, viewName = view.Name, createdCount = created.Count, created },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static (ElementId? TypeId, string? Error) ResolveTextNoteType(Document doc, long typeId, string typeName)
    {
        if (typeId > 0)
        {
            if (doc.GetElement(new ElementId(typeId)) is TextNoteType byId)
                return (byId.Id, null);
            return (null, $"Element {typeId} is not a text note type.");
        }

        if (!string.IsNullOrWhiteSpace(typeName))
        {
            var matches = new FilteredElementCollector(doc)
                .OfClass(typeof(TextNoteType))
                .Cast<TextNoteType>()
                .Where(t => t.Name.Contains(typeName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0)
                return (null, $"No text note type matches '{typeName}'.");
            var exact = matches.FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return (exact.Id, null);
            if (matches.Count > 1)
            {
                var sample = string.Join("; ", matches.Take(10).Select(t => $"{t.Name} (typeId {t.Id.Value})"));
                return (null, $"{matches.Count} text note types match '{typeName}' — narrow the name or pass typeId. Candidates: {sample}");
            }
            return (matches[0].Id, null);
        }

        var defaultId = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
        if (defaultId != null && defaultId != ElementId.InvalidElementId)
            return (defaultId, null);

        var first = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).FirstElement();
        return first != null
            ? (first.Id, null)
            : (null, "The document has no text note types.");
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
