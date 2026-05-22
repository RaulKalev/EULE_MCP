using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Documentation.Placement;

/// <summary>
/// Resolves the center-point (sheet coordinates, in feet) to place a viewport on a sheet.
/// Uses the titleblock bounding box as the reference for sheet extent.
/// Falls back to a hardcoded A1-approximate center if no titleblock is found.
/// </summary>
public static class PlacementPointResolver
{
    private static readonly XYZ DefaultCenter = new(0.40, 0.27, 0); // ~A1 centre, feet

    /// <summary>Returns the sheet-coordinate centre for viewport placement.</summary>
    public static XYZ GetSheetCenter(Document doc, ViewSheet sheet)
    {
        try
        {
            var tb = new FilteredElementCollector(doc)
                .OwnedByView(sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .Cast<FamilyInstance>()
                .FirstOrDefault();

            if (tb != null)
            {
                var bb = tb.get_BoundingBox(sheet);
                if (bb != null)
                    return new XYZ((bb.Min.X + bb.Max.X) / 2.0,
                                   (bb.Min.Y + bb.Max.Y) / 2.0, 0);
            }
        }
        catch { /* use default */ }

        return DefaultCenter;
    }
}
