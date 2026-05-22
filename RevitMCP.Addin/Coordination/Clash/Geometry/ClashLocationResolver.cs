using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Coordination.Clash.Geometry;

public static class ClashLocationResolver
{
    private const double FeetToMeters = 0.3048;

    /// <summary>
    /// Returns the approximate clash location as the centroid of the two bounding boxes,
    /// converted to meters (X, Y, Z) in host-document coordinates.
    /// </summary>
    public static (double X, double Y, double Z) ResolveMeters(
        BoundingBoxXYZ sourceBox, BoundingBoxXYZ targetBox)
    {
        double cx = (sourceBox.Min.X + sourceBox.Max.X + targetBox.Min.X + targetBox.Max.X) / 4;
        double cy = (sourceBox.Min.Y + sourceBox.Max.Y + targetBox.Min.Y + targetBox.Max.Y) / 4;
        double cz = (sourceBox.Min.Z + sourceBox.Max.Z + targetBox.Min.Z + targetBox.Max.Z) / 4;
        return (Math.Round(cx * FeetToMeters, 3),
                Math.Round(cy * FeetToMeters, 3),
                Math.Round(cz * FeetToMeters, 3));
    }
}
