using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>
/// Creates a horizontal SketchPlane at a specified elevation for use with
/// <c>doc.Create.NewRoomBoundaryLines()</c>.
/// The SketchPlane is always created at exactly the Level's elevation so that
/// Revit correctly associates the separation lines with the Level's Room bounding region.
/// Phase 3.
/// </summary>
public static class SketchPlaneResolver
{
    /// <summary>
    /// Creates a horizontal SketchPlane at <paramref name="levelElevationFeet"/> in the
    /// host document. Must be called inside an open Transaction.
    /// </summary>
    /// <param name="doc">Host document (transaction must be open).</param>
    /// <param name="levelElevationFeet">
    /// Z-elevation of the host Level in Revit internal feet.
    /// </param>
    /// <returns>A new SketchPlane whose normal is <c>XYZ.BasisZ</c>.</returns>
    public static SketchPlane CreateAtElevation(Document doc, double levelElevationFeet)
    {
        var origin = new XYZ(0.0, 0.0, levelElevationFeet);
        var plane  = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, origin);
        return SketchPlane.Create(doc, plane);
    }
}
