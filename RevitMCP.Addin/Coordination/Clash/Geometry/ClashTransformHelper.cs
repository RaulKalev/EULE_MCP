using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Coordination.Clash.Geometry;

public static class ClashTransformHelper
{
    /// <summary>
    /// Transforms a bounding box from link-local coordinates into host-document
    /// coordinates by transforming all 8 corners and computing a new AABB.
    /// </summary>
    public static BoundingBoxXYZ TransformBoundingBox(BoundingBoxXYZ box, Transform transform)
    {
        if (transform.IsIdentity) return box;

        var corners = new[]
        {
            new XYZ(box.Min.X, box.Min.Y, box.Min.Z),
            new XYZ(box.Max.X, box.Min.Y, box.Min.Z),
            new XYZ(box.Min.X, box.Max.Y, box.Min.Z),
            new XYZ(box.Max.X, box.Max.Y, box.Min.Z),
            new XYZ(box.Min.X, box.Min.Y, box.Max.Z),
            new XYZ(box.Max.X, box.Min.Y, box.Max.Z),
            new XYZ(box.Min.X, box.Max.Y, box.Max.Z),
            new XYZ(box.Max.X, box.Max.Y, box.Max.Z)
        };

        var pts = corners.Select(c => transform.OfPoint(c)).ToArray();
        var bb = new BoundingBoxXYZ();
        bb.Min = new XYZ(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Min(p => p.Z));
        bb.Max = new XYZ(pts.Max(p => p.X), pts.Max(p => p.Y), pts.Max(p => p.Z));
        return bb;
    }
}
