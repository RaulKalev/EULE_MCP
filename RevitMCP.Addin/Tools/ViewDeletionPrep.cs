using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Revit refuses to delete the active view, and some versions refuse views open in tabs.
/// Before a delete transaction, switches the active view to one that is not being deleted
/// and closes open tabs of targeted views so the deletion can proceed.
/// </summary>
internal static class ViewDeletionPrep
{
    /// <summary>
    /// Returns ids of targeted views that still cannot be deleted (the active view when
    /// no other view is available to switch to). Must be called OUTSIDE a transaction.
    /// </summary>
    public static HashSet<ElementId> PrepareForDeletion(
        UIDocument uidoc, HashSet<ElementId> targetIds, List<string> warnings)
    {
        var undeletable = new HashSet<ElementId>();
        var openViews = uidoc.GetOpenUIViews();
        var openTargeted = openViews.Where(uv => targetIds.Contains(uv.ViewId)).ToList();
        var activeView = uidoc.ActiveView;
        var activeTargeted = activeView != null && targetIds.Contains(activeView.Id);

        if (!activeTargeted && openTargeted.Count == 0)
            return undeletable;

        if (activeTargeted)
        {
            var safe = FindSafeView(uidoc, targetIds, openViews);
            if (safe == null)
            {
                warnings.Add($"View '{activeView!.Name}' is the active view and no other view exists to switch to — skipped. Open a different view and retry.");
                undeletable.Add(activeView.Id);
            }
            else
            {
                try
                {
                    uidoc.ActiveView = safe;
                }
                catch (Exception ex)
                {
                    warnings.Add($"View '{activeView!.Name}' is the active view and switching to '{safe.Name}' failed ({ex.Message}) — skipped.");
                    undeletable.Add(activeView.Id);
                }
            }
        }

        // Close tabs of targeted views that are still open. A failed Close is not fatal —
        // deletion may still succeed, and per-view delete errors are reported downstream.
        foreach (var uv in openTargeted)
        {
            if (undeletable.Contains(uv.ViewId))
                continue;
            try { uv.Close(); } catch { }
        }

        return undeletable;
    }

    private static View? FindSafeView(UIDocument uidoc, HashSet<ElementId> targetIds, IList<UIView> openViews)
    {
        var doc = uidoc.Document;

        // Prefer a view that is already open in another tab.
        foreach (var uv in openViews)
        {
            if (targetIds.Contains(uv.ViewId))
                continue;
            if (doc.GetElement(uv.ViewId) is View open && !open.IsTemplate)
                return open;
        }

        return new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .Where(v => !v.IsTemplate
                        && !targetIds.Contains(v.Id)
                        && v.ViewType != ViewType.ProjectBrowser
                        && v.ViewType != ViewType.SystemBrowser
                        && v.ViewType != ViewType.Internal
                        && v.ViewType != ViewType.Undefined)
            .OrderBy(v => v.ViewType == ViewType.FloorPlan ? 0 : 1)
            .FirstOrDefault();
    }
}
