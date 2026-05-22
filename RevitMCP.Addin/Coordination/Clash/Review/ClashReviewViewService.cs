using Autodesk.Revit.DB;
using RevitMCP.Addin.Coordination.Clash.DTOs;
using RevitMCP.Addin.Coordination.Clash.Geometry;

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

        // Create a new isometric 3D view
        var viewFamilyType = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.ThreeDimensional)
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
        var padFeet = paddingMm / 304.8;
        BoundingBoxXYZ? box = null;

        // Try to compute from elements
        var srcElem = doc.GetElement(new ElementId(clash.Source.ElementId));
        var tgtElem = clash.Target.LinkInstanceId.HasValue
            ? null
            : doc.GetElement(new ElementId(clash.Target.ElementId));

        var srcBox = srcElem?.get_BoundingBox(null);
        var tgtBox = tgtElem?.get_BoundingBox(null);

        if (srcBox != null && tgtBox != null)
        {
            box = UnionBoundingBoxes(srcBox, tgtBox);
        }
        else if (srcBox != null)
        {
            box = srcBox;
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

        var expanded = ClashBoundingBoxHelper.Expand(box, paddingMm);
        reviewView.SetSectionBox(expanded);
        reviewView.IsSectionBoxActive = true;
    }

    private static BoundingBoxXYZ UnionBoundingBoxes(BoundingBoxXYZ a, BoundingBoxXYZ b)
    {
        var result = new BoundingBoxXYZ();
        result.Min = new XYZ(
            Math.Min(a.Min.X, b.Min.X),
            Math.Min(a.Min.Y, b.Min.Y),
            Math.Min(a.Min.Z, b.Min.Z));
        result.Max = new XYZ(
            Math.Max(a.Max.X, b.Max.X),
            Math.Max(a.Max.Y, b.Max.Y),
            Math.Max(a.Max.Z, b.Max.Z));
        return result;
    }
}
