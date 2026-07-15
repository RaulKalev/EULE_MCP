using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Selects elements that live inside a linked model in the host Revit UI,
/// mirroring how a user picks linked elements interactively. Uses link
/// references (Selection.SetReferences) — plain element ids cannot address
/// elements of another document.
/// </summary>
public class SelectLinkedElementsTool : IRevitMcpTool
{
    public string Name => "revit_select_linked_elements";
    public string Description =>
        "Selects elements INSIDE a linked model (Revit link or IFC converted to a Revit link) in the host Revit UI. " +
        "Required: linkInstanceId (from revit_list_clashable_links / ifc_list_links), elementIds (ids in the LINKED document, e.g. from revit_query_linked_elements). " +
        "Optional: replaceSelection (default true — false keeps the current selection), zoomToSelection (bool). " +
        "Does not modify model data.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Selection;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null)
            return Task.FromResult(Fail(request, "No active document."));
        var doc = uidoc.Document;

        var linkInstanceId = ToolArguments.GetLong(request.Arguments, "linkInstanceId");
        var elementIds = ToolArguments.GetLongArray(request.Arguments, "elementIds");
        var replaceSelection = ToolArguments.GetBool(request.Arguments, "replaceSelection", true);
        var zoomToSelection = ToolArguments.GetBool(request.Arguments, "zoomToSelection");

        if (linkInstanceId <= 0)
            return Task.FromResult(Fail(request, "linkInstanceId is required — use revit_list_clashable_links or ifc_list_links to find it."));
        if (elementIds.Length == 0)
            return Task.FromResult(Fail(request, "elementIds is required (element ids inside the linked document)."));

        if (doc.GetElement(new ElementId(linkInstanceId)) is not RevitLinkInstance link)
            return Task.FromResult(Fail(request, $"Element {linkInstanceId} is not a Revit link instance."));
        var linkDoc = link.GetLinkDocument();
        if (linkDoc == null)
            return Task.FromResult(Fail(request, $"Link '{link.Name}' is not loaded — load it in Manage Links first."));

        var references = new List<Reference>();
        var selectedElements = new List<Element>();
        var invalidIds = new List<long>();
        foreach (var id in elementIds.Distinct())
        {
            var el = linkDoc.GetElement(new ElementId(id));
            if (el == null)
            {
                invalidIds.Add(id);
                continue;
            }
            try
            {
                references.Add(new Reference(el).CreateLinkReference(link));
                selectedElements.Add(el);
            }
            catch
            {
                invalidIds.Add(id);
            }
        }

        if (references.Count == 0)
            return Task.FromResult(Fail(request, $"None of the provided element IDs could be referenced in link '{link.Name}'."));

        if (!replaceSelection)
        {
            // Preserve what is already selected: linked picks come back via GetReferences,
            // host elements are re-wrapped as references so one SetReferences call keeps both.
            var existing = new Dictionary<string, Reference>();
            foreach (var r in uidoc.Selection.GetReferences())
                TryAddStable(existing, doc, r);
            foreach (var hostId in uidoc.Selection.GetElementIds())
            {
                var hostEl = doc.GetElement(hostId);
                if (hostEl != null)
                    try { TryAddStable(existing, doc, new Reference(hostEl)); } catch { }
            }
            foreach (var r in references)
                TryAddStable(existing, doc, r);
            references = existing.Values.ToList();
        }

        uidoc.Selection.SetReferences(references);

        if (zoomToSelection)
            TryZoomTo(uidoc, link, selectedElements);

        var warnings = new List<string>();
        if (invalidIds.Count > 0)
            warnings.Add($"{invalidIds.Count} element ID(s) not found in link '{link.Name}': {string.Join(", ", invalidIds.Take(10))}");

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Selected {selectedElements.Count} element(s) from link '{link.Name}'.",
            Data = new
            {
                linkInstanceId,
                linkName = link.Name,
                selectedCount = selectedElements.Count,
                selectedElementIds = selectedElements.Select(e => e.Id.Value).ToList(),
                invalidCount = invalidIds.Count
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static void TryAddStable(Dictionary<string, Reference> map, Document doc, Reference reference)
    {
        try
        {
            var key = reference.ConvertToStableRepresentation(doc);
            map.TryAdd(key, reference);
        }
        catch { /* unstable references are skipped */ }
    }

    /// <summary>Zooms the active view to the host-space bounding box of the linked elements.</summary>
    private static void TryZoomTo(UIDocument uidoc, RevitLinkInstance link, List<Element> linkedElements)
    {
        try
        {
            var transform = link.GetTotalTransform();
            XYZ? min = null, max = null;
            foreach (var el in linkedElements)
            {
                var bb = el.get_BoundingBox(null);
                if (bb == null) continue;
                foreach (var corner in Corners(bb))
                {
                    var p = transform.OfPoint(bb.Transform.OfPoint(corner));
                    min = min == null ? p : new XYZ(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
                    max = max == null ? p : new XYZ(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
                }
            }
            if (min == null || max == null) return;

            // Pad 20% so the elements are not glued to the viewport edge.
            var pad = (max - min) * 0.2 + new XYZ(1, 1, 1) * 0.5;
            var uiView = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == uidoc.ActiveView.Id);
            uiView?.ZoomAndCenterRectangle(min - pad, max + pad);
        }
        catch { /* zoom is best-effort */ }
    }

    private static IEnumerable<XYZ> Corners(BoundingBoxXYZ bb)
    {
        yield return new XYZ(bb.Min.X, bb.Min.Y, bb.Min.Z);
        yield return new XYZ(bb.Max.X, bb.Min.Y, bb.Min.Z);
        yield return new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z);
        yield return new XYZ(bb.Min.X, bb.Min.Y, bb.Max.Z);
        yield return new XYZ(bb.Max.X, bb.Max.Y, bb.Min.Z);
        yield return new XYZ(bb.Max.X, bb.Min.Y, bb.Max.Z);
        yield return new XYZ(bb.Min.X, bb.Max.Y, bb.Max.Z);
        yield return new XYZ(bb.Max.X, bb.Max.Y, bb.Max.Z);
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
