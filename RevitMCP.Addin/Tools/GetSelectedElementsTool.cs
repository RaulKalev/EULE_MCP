using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class GetSelectedElementsTool : IRevitMcpTool
{
    public string Name => "revit_get_selected_elements";
    public string Description => "Returns the currently selected elements from the active Revit document.";
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

        if (ids.Count == 0)
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = "No elements selected.",
                Data = new { selectedCount = 0, elements = Array.Empty<object>() },
                DurationMs = sw.ElapsedMilliseconds
            });

        var elements = new List<object>();
        var warnings = new List<string>();
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

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{ids.Count} element(s) selected. Returning {detailed} detailed.",
            Data = new { selectedCount = ids.Count, elements },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
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
