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

        // Either element may be in a link (a BCF clash between two linked models): select the link instance then.
        foreach (var (reference, role) in new[] { (clash.Source, "Source"), (clash.Target, "Target") })
        {
            if (!reference.LinkInstanceId.HasValue)
            {
                var element = doc.GetElement(new ElementId(reference.ElementId));
                if (element != null) elementIds.Add(element.Id);
                continue;
            }

            var linkInstance = doc.GetElement(new ElementId(reference.LinkInstanceId.Value));
            if (linkInstance != null && !elementIds.Contains(linkInstance.Id))
            {
                elementIds.Add(linkInstance.Id);
                warnings.Add($"{role} element is in a linked model. Selected the link instance '{reference.LinkName ?? "(unknown)"}' instead. Navigate inside the link to inspect element {reference.ElementId}.");
            }
        }

        if (elementIds.Count > 0)
            uidoc.Selection.SetElementIds(elementIds);

        var msg = $"Navigated to clash {clash.ClashId}: {clash.Source.Category} vs {clash.Target.Category} in view '{ClashReviewViewService.ViewName}'.";
        return (msg, warnings);
    }
}

