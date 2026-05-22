using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Coordination.Clash.Geometry;

/// <summary>
/// Revit-API wrapper around <see cref="ClashBoundingBoxMath"/>.
/// </summary>
public static class ClashBoundingBoxHelper
{
    private const double MmToFeet = 1.0 / 304.8;

    public static bool Overlaps(BoundingBoxXYZ a, BoundingBoxXYZ b) =>
        ClashBoundingBoxMath.Overlaps(
            a.Min.X, a.Min.Y, a.Min.Z,
            a.Max.X, a.Max.Y, a.Max.Z,
            b.Min.X, b.Min.Y, b.Min.Z,
            b.Max.X, b.Max.Y, b.Max.Z);

    public static BoundingBoxXYZ Expand(BoundingBoxXYZ box, double paddingMm)
    {
        var pad = paddingMm * MmToFeet;
        var (mnX, mnY, mnZ, mxX, mxY, mxZ) = ClashBoundingBoxMath.Expand(
            box.Min.X, box.Min.Y, box.Min.Z,
            box.Max.X, box.Max.Y, box.Max.Z, pad);
        var bb = new BoundingBoxXYZ();
        bb.Min = new XYZ(mnX, mnY, mnZ);
        bb.Max = new XYZ(mxX, mxY, mxZ);
        return bb;
    }

    public static double ApproximateDistanceMm(BoundingBoxXYZ a, BoundingBoxXYZ b)
    {
        var distFeet = ClashBoundingBoxMath.ApproximateDistance(
            a.Min.X, a.Min.Y, a.Min.Z,
            a.Max.X, a.Max.Y, a.Max.Z,
            b.Min.X, b.Min.Y, b.Min.Z,
            b.Max.X, b.Max.Y, b.Max.Z);
        return distFeet / MmToFeet;
    }
}
