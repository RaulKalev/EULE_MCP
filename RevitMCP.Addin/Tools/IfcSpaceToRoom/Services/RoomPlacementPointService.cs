using Autodesk.Revit.DB;
using RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>
/// Finds a point guaranteed to lie inside a footprint CurveLoop for later Room placement.
/// Phase 2: read-only, no document modifications.
/// </summary>
public class RoomPlacementPointService
{
    // Grid sampling parameters
    private const int   SampleGridDivisions = 8;
    private const double MinDimensionFt     = 1.0 / 304.8; // 1 mm

    /// <summary>
    /// Finds an interior point for <paramref name="loop"/> at the given elevation.
    /// </summary>
    /// <param name="loop">Cleaned, validated footprint CurveLoop in host coordinates.</param>
    /// <param name="elevationFeet">Z elevation for the returned point (usually the matched level).</param>
    public PlacementPointResult Find(CurveLoop loop, double elevationFeet)
    {
        var pts = LoopAreaCalculator.TessellateLoop(loop);
        if (pts.Count < 3)
        {
            return new PlacementPointResult
            {
                Success = false,
                Method  = "Failed",
                Warning = "Could not tessellate loop for placement point calculation."
            };
        }

        // ── Attempt 1: polygon centroid ───────────────────────────────────────
        var centroid = ComputeCentroid2D(pts);
        if (IsPointInPolygon2D(pts, centroid))
        {
            return new PlacementPointResult
            {
                Success = true,
                Point   = new XYZ(centroid.X, centroid.Y, elevationFeet),
                Method  = "Centroid"
            };
        }

        // ── Attempt 2: bounding box center ────────────────────────────────────
        var (bboxCenter, bboxMin, bboxMax) = ComputeBoundingBox2D(pts);
        if (IsPointInPolygon2D(pts, bboxCenter))
        {
            return new PlacementPointResult
            {
                Success = true,
                Point   = new XYZ(bboxCenter.X, bboxCenter.Y, elevationFeet),
                Method  = "BoundingBoxCenter",
                Warning = "Centroid was outside the polygon; using bounding box centre."
            };
        }

        // ── Attempt 3: sampled grid ────────────────────────────────────────────
        double stepX = (bboxMax.X - bboxMin.X) / SampleGridDivisions;
        double stepY = (bboxMax.Y - bboxMin.Y) / SampleGridDivisions;

        if (stepX < MinDimensionFt) stepX = MinDimensionFt;
        if (stepY < MinDimensionFt) stepY = MinDimensionFt;

        for (double y = bboxMin.Y + stepY; y < bboxMax.Y; y += stepY)
        for (double x = bboxMin.X + stepX; x < bboxMax.X; x += stepX)
        {
            var candidate = new XYZ(x, y, 0);
            if (IsPointInPolygon2D(pts, candidate))
            {
                return new PlacementPointResult
                {
                    Success = true,
                    Point   = new XYZ(x, y, elevationFeet),
                    Method  = "SampledGridPoint",
                    Warning = "Centroid and bounding box centre were outside polygon; " +
                              "using first valid sampled grid point."
                };
            }
        }

        // ── All attempts failed ───────────────────────────────────────────────
        return new PlacementPointResult
        {
            Success = false,
            Method  = "Failed",
            Warning = "Could not find any interior point. The footprint may be very narrow or have an unusual shape."
        };
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static XYZ ComputeCentroid2D(IList<XYZ> pts)
    {
        double sumX = 0, sumY = 0;
        foreach (var p in pts) { sumX += p.X; sumY += p.Y; }
        return new XYZ(sumX / pts.Count, sumY / pts.Count, 0);
    }

    private static (XYZ Center, XYZ Min, XYZ Max) ComputeBoundingBox2D(IList<XYZ> pts)
    {
        double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
        double minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        return (new XYZ((minX + maxX) / 2, (minY + maxY) / 2, 0),
                new XYZ(minX, minY, 0),
                new XYZ(maxX, maxY, 0));
    }

    /// <summary>
    /// Ray-casting point-in-polygon test (XY projection only).
    /// </summary>
    private static bool IsPointInPolygon2D(IList<XYZ> polygon, XYZ point)
    {
        bool inside = false;
        int j = polygon.Count - 1;
        for (int i = 0; i < polygon.Count; i++)
        {
            double xi = polygon[i].X, yi = polygon[i].Y;
            double xj = polygon[j].X, yj = polygon[j].Y;

            bool crossesY = (yi > point.Y) != (yj > point.Y);
            bool leftOfEdge = point.X < (xj - xi) * (point.Y - yi) / (yj - yi) + xi;

            if (crossesY && leftOfEdge)
                inside = !inside;

            j = i;
        }
        return inside;
    }
}
