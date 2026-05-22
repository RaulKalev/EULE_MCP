using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitMCP.Addin.Coordination.Clash.DTOs;

namespace RevitMCP.Addin.Coordination.Clash.Review;

public class ClashFocusService
{
    private readonly ClashReviewViewService _viewService = new();

    /// <summary>
    /// Activates the MCP Clash Review view, frames the section box around the clash,
    /// and selects the clash elements.
    /// The transaction for SetSectionBox must be opened by the caller before calling Focus.
    /// </summary>
    public (string message, List<string> warnings) Focus(
        UIApplication uiApp,
        Document doc,
        ClashResultDto clash,
        double paddingMm,
        Transaction openTransaction)
    {
        var warnings = new List<string>();
        var uidoc = uiApp.ActiveUIDocument;

        // Create or reuse the review view (within the open transaction)
        var reviewView = _viewService.CreateOrReuseView(doc);

        // Activate the view
        uidoc.ActiveView = reviewView;

        // Set section box within the open transaction
        _viewService.SetSectionBox(doc, reviewView, clash, paddingMm);

        // Select elements (outside transaction — UI-only)
        var elementIds = new List<ElementId>();

        // Source element (always host)
        var srcElem = doc.GetElement(new ElementId(clash.Source.ElementId));
        if (srcElem != null) elementIds.Add(srcElem.Id);

        // Target element
        if (!clash.Target.LinkInstanceId.HasValue)
        {
            var tgtElem = doc.GetElement(new ElementId(clash.Target.ElementId));
            if (tgtElem != null) elementIds.Add(tgtElem.Id);
        }
        else
        {
            // Linked element — select the RevitLinkInstance instead
            var linkInstance = doc.GetElement(new ElementId(clash.Target.LinkInstanceId.Value));
            if (linkInstance != null)
            {
                elementIds.Add(linkInstance.Id);
                warnings.Add($"Target element is in a linked model. Selected the link instance '{clash.Target.LinkName ?? "(unknown)"}' instead. Navigate inside the link to inspect element {clash.Target.ElementId}.");
            }
        }

        if (elementIds.Count > 0)
        {
            uidoc.Selection.SetElementIds(elementIds);
        }

        var msg = $"Navigated to clash {clash.ClashId}: {clash.Source.Category} vs {clash.Target.Category} in view '{ClashReviewViewService.ViewName}'.";
        return (msg, warnings);
    }
}
