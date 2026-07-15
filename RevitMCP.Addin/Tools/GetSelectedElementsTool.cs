using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Compat;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class GetSelectedElementsTool : IRevitMcpTool
{
    public string Name => "revit_get_selected_elements";
    public string Description =>
        "Returns the currently selected elements from the active Revit document. " +
        "Elements picked inside linked models are reported separately in linkedElements " +
        "with their linkInstanceId and the element id inside the linked document.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Selection;

    private const int DetailedLimit = 50;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;

        if (uidoc?.Document == null)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "No active document." });

        var doc = uidoc.Document;
        var ids = uidoc.Selection.GetElementIds();
        var warnings = new List<string>();
        var linkedElements = BuildLinkedSelectionSummaries(uidoc, doc, warnings);

        if (ids.Count == 0 && linkedElements.Count == 0)
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = "No elements selected.",
                Data = new { selectedCount = 0, elements = Array.Empty<object>(), linkedSelectedCount = 0, linkedElements = Array.Empty<object>() },
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            });

        var elements = new List<object>();
        int detailed = 0;

        foreach (var id in ids)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var elem = doc.GetElement(id);
            if (elem == null) continue;

            if (detailed >= DetailedLimit)
            {
                warnings.Add($"Output capped at {DetailedLimit} detailed elements. {ids.Count - DetailedLimit} more selected.");
                break;
            }

            elements.Add(BuildElementSummary(doc, elem));
            detailed++;
        }

        var linkedPart = linkedElements.Count > 0 ? $" Plus {linkedElements.Count} element(s) selected inside linked models." : string.Empty;

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{ids.Count} element(s) selected. Returning {detailed} detailed.{linkedPart}",
            Data = new { selectedCount = ids.Count, elements, linkedSelectedCount = linkedElements.Count, linkedElements },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    /// <summary>Elements picked inside linked models only surface through selection references.</summary>
    private static List<object> BuildLinkedSelectionSummaries(UIDocument uidoc, Document doc, List<string> warnings)
    {
        var linked = new List<object>();
        if (!SelectionReferenceCompat.TryGetReferences(uidoc.Selection, out var selectedReferences, out var error))
        {
            warnings.Add(error);
            return linked;
        }

        try
        {
            foreach (var reference in selectedReferences)
            {
                if (linked.Count >= DetailedLimit) break;
                if (reference.LinkedElementId == ElementId.InvalidElementId) continue;
                if (doc.GetElement(reference.ElementId) is not RevitLinkInstance link) continue;

                var linkDoc = link.GetLinkDocument();
                var element = linkDoc?.GetElement(reference.LinkedElementId);
                if (linkDoc == null || element == null) continue;

                string family = string.Empty, type = string.Empty;
                if (element is FamilyInstance fi)
                {
                    family = fi.Symbol?.Family?.Name ?? string.Empty;
                    type = fi.Symbol?.Name ?? string.Empty;
                }
                else
                {
                    type = linkDoc.GetElement(element.GetTypeId())?.Name ?? string.Empty;
                }

                linked.Add(new
                {
                    linkInstanceId = link.Id.Value,
                    linkName = link.Name,
                    elementId = element.Id.Value,
                    uniqueId = element.UniqueId,
                    category = element.Category?.Name ?? string.Empty,
                    family,
                    type,
                    name = element.Name
                });
            }
        }
        catch { /* selection references unavailable — report host elements only */ }
        return linked;
    }

    private static object BuildElementSummary(Document doc, Element elem)
    {
        // Category
        var categoryName = elem.Category?.Name ?? string.Empty;

        // Family / Type
        var familyName = string.Empty;
        var typeName = string.Empty;
        if (elem is FamilyInstance fi)
        {
            familyName = fi.Symbol?.Family?.Name ?? string.Empty;
            typeName = fi.Symbol?.Name ?? string.Empty;
        }
        else
        {
            var typeEl = doc.GetElement(elem.GetTypeId());
            typeName = typeEl?.Name ?? string.Empty;
        }

        // Level
        var levelName = string.Empty;
        if (elem.LevelId != ElementId.InvalidElementId)
            levelName = (doc.GetElement(elem.LevelId) as Level)?.Name ?? string.Empty;

        // Location
        var locationSummary = elem.Location switch
        {
            LocationPoint lp => $"Point ({lp.Point.X:F2}, {lp.Point.Y:F2}, {lp.Point.Z:F2})",
            LocationCurve lc => $"Curve from ({lc.Curve.GetEndPoint(0).X:F2}, {lc.Curve.GetEndPoint(0).Y:F2}) " +
                                $"to ({lc.Curve.GetEndPoint(1).X:F2}, {lc.Curve.GetEndPoint(1).Y:F2})",
            _ => string.Empty
        };

        // Bounding box
        var bbSummary = string.Empty;
        try
        {
            var bb = elem.get_BoundingBox(null);
            if (bb != null)
                bbSummary = $"({bb.Min.X:F2}, {bb.Min.Y:F2}, {bb.Min.Z:F2}) → ({bb.Max.X:F2}, {bb.Max.Y:F2}, {bb.Max.Z:F2})";
        }
        catch { }

        return new
        {
            elementId = elem.Id.Value,
            uniqueId = elem.UniqueId,
            category = categoryName,
            family = familyName,
            type = typeName,
            name = elem.Name,
            level = levelName,
            location = locationSummary,
            boundingBox = bbSummary
        };
    }
}
