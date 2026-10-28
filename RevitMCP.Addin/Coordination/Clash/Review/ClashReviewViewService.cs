using Autodesk.Revit.DB;
using RevitMCP.Addin.Coordination.Clash.DTOs;
using RevitMCP.Addin.Coordination.Clash.Geometry;
using RevitMCP.Addin.Documentation.Views;

namespace RevitMCP.Addin.Coordination.Clash.Review;

public class ClashReviewViewService
{
    internal const string ViewName = "MCP Clash Review";
    private const double FeetToMeters = 0.3048;

    /// <summary>
    /// Finds an existing 3D view named "MCP Clash Review" or creates one.
    /// MUST be called inside an open transaction.
    /// </summary>
    public View3D CreateOrReuseView(Document doc)
    {
        // Try to find existing
        var existing = new FilteredElementCollector(doc)
            .OfClass(typeof(View3D))
            .Cast<View3D>()
            .FirstOrDefault(v => !v.IsTemplate && v.Name == ViewName);

        if (existing != null) return existing;

        // Create a new isometric 3D view with the document's default 3D type, else the first one (#104).
        var viewFamilyType = ViewFamilyTypeResolver.ResolveDefault(doc, ViewFamily.ThreeDimensional)
            ?? throw new InvalidOperationException("No 3D view family type found in document.");

        var view = View3D.CreateIsometric(doc, viewFamilyType.Id);
        view.Name = ViewName;
        return view;
    }

    /// <summary>
    /// Sets the section box on the review view to frame the two clash elements.
    /// MUST be called inside an open transaction.
    /// </summary>
    public void SetSectionBox(
        Document doc,
        View3D reviewView,
        ClashResultDto clash,
        double paddingMm)
    {
        BoundingBoxXYZ? box = null;

        // Try to compute from elements; a linked element's box is moved into host coordinates.
        var srcBox = HostBox(doc, clash.Source);
        var tgtBox = HostBox(doc, clash.Target);

        if (srcBox != null && tgtBox != null)
        {
            box = ViewExtentService.Union(srcBox, tgtBox);
        }
        else if (srcBox != null || tgtBox != null)
        {
            box = srcBox ?? tgtBox;
        }
        else if (clash.Location != null)
        {
            // Fall back to clash location (convert from meters to feet)
            double x = clash.Location.X / FeetToMeters;
            double y = clash.Location.Y / FeetToMeters;
            double z = clash.Location.Z / FeetToMeters;
            box = new BoundingBoxXYZ();
            box.Min = new XYZ(x - 1, y - 1, z - 1);
            box.Max = new XYZ(x + 1, y + 1, z + 1);
        }

        if (box == null) return;

        // The shared view extent service (#104): pad every side, then set and activate the section box.
        ViewExtentService.ApplySectionBox(reviewView, ViewExtentService.Pad(box, paddingMm));
    }

    /// <summary>The element's bounding box in host coordinates, for host and linked elements alike.</summary>
    internal static BoundingBoxXYZ? HostBox(Document doc, ClashElementRefDto reference)
    {
        if (reference.LinkInstanceId is not { } linkId)
            return doc.GetElement(new ElementId(reference.ElementId))?.get_BoundingBox(null);

        if (doc.GetElement(new ElementId(linkId)) is not RevitLinkInstance link) return null;
        var box = link.GetLinkDocument()?.GetElement(new ElementId(reference.ElementId))?.get_BoundingBox(null);
        return box == null ? null : ClashTransformHelper.TransformBoundingBox(box, link.GetTotalTransform());
    }
}
