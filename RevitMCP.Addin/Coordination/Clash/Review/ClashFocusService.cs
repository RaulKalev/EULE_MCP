using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Coordination.Clash.DTOs;

namespace RevitMCP.Addin.Coordination.Clash.Review;

public class ClashFocusService
{
    private readonly ClashReviewViewService _viewService = new();

    /// <summary>
    /// Activates the MCP Clash Review view, frames the section box around the clash,
    /// and selects the clash elements.
    /// Manages its own transaction internally — the caller must NOT have an open transaction.
    /// </summary>
    public (string message, List<string> warnings) Focus(
        UIApplication uiApp,
        Document doc,
        ClashResultDto clash,
        double paddingMm)
    {
        var warnings = new List<string>();
        var uidoc = uiApp.ActiveUIDocument;

        // Transaction: create-or-reuse view + set section box.
        // uidoc.ActiveView must NOT be changed inside an open transaction.
        View3D reviewView;
        using (var t = new Transaction(doc, "Revit MCP - Focus Clash"))
        {
            t.Start();
            reviewView = _viewService.CreateOrReuseView(doc);
            _viewService.SetSectionBox(doc, reviewView, clash, paddingMm);
            RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(t);
        }

        // Activate the view only after the transaction is committed.
        uidoc.ActiveView = reviewView;

        // Select elements (UI-only, no transaction needed).
        var elementIds = new List<ElementId>();

        var srcElem = doc.GetElement(new ElementId(clash.Source.ElementId));
        if (srcElem != null) elementIds.Add(srcElem.Id);

        if (!clash.Target.LinkInstanceId.HasValue)
        {
            var tgtElem = doc.GetElement(new ElementId(clash.Target.ElementId));
            if (tgtElem != null) elementIds.Add(tgtElem.Id);
        }
        else
        {
            var linkInstance = doc.GetElement(new ElementId(clash.Target.LinkInstanceId.Value));
            if (linkInstance != null)
            {
                elementIds.Add(linkInstance.Id);
                warnings.Add($"Target element is in a linked model. Selected the link instance '{clash.Target.LinkName ?? "(unknown)"}' instead. Navigate inside the link to inspect element {clash.Target.ElementId}.");
            }
        }

        if (elementIds.Count > 0)
            uidoc.Selection.SetElementIds(elementIds);

        var msg = $"Navigated to clash {clash.ClashId}: {clash.Source.Category} vs {clash.Target.Category} in view '{ClashReviewViewService.ViewName}'.";
        return (msg, warnings);
    }
}

