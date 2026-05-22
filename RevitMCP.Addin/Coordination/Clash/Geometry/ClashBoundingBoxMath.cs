namespace RevitMCP.Addin.Coordination.Clash.Geometry;

/// <summary>
/// Pure bounding-box math using raw min/max coordinate values.
/// No Revit API dependency — safe to unit-test directly.
/// All inputs in the same unit (Revit internal feet unless stated otherwise).
/// </summary>
public static class ClashBoundingBoxMath
{
    public static bool Overlaps(
        double aMinX, double aMinY, double aMinZ,
        double aMaxX, double aMaxY, double aMaxZ,
        double bMinX, double bMinY, double bMinZ,
        double bMaxX, double bMaxY, double bMaxZ)
    {
        return aMinX <= bMaxX && aMaxX >= bMinX
            && aMinY <= bMaxY && aMaxY >= bMinY
            && aMinZ <= bMaxZ && aMaxZ >= bMinZ;
    }

    /// <summary>Returns positive gap between boxes along each axis; 0 if overlapping on that axis.</summary>
    public static double ApproximateDistance(
        double aMinX, double aMinY, double aMinZ,
        double aMaxX, double aMaxY, double aMaxZ,
        double bMinX, double bMinY, double bMinZ,
        double bMaxX, double bMaxY, double bMaxZ)
    {
        var dx = Math.Max(0, Math.Max(aMinX - bMaxX, bMinX - aMaxX));
        var dy = Math.Max(0, Math.Max(aMinY - bMaxY, bMinY - aMaxY));
        var dz = Math.Max(0, Math.Max(aMinZ - bMaxZ, bMinZ - aMaxZ));
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>Expands min/max coords by pad on all sides. Returns (minX,minY,minZ,maxX,maxY,maxZ).</summary>
    public static (double mnX, double mnY, double mnZ, double mxX, double mxY, double mxZ) Expand(
        double minX, double minY, double minZ,
        double maxX, double maxY, double maxZ,
        double pad)
    {
        return (minX - pad, minY - pad, minZ - pad,
                maxX + pad, maxY + pad, maxZ + pad);
    }
}
