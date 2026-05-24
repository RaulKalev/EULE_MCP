using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>
/// Calculates the approximate area of a CurveLoop using the shoelace formula on
/// tessellated XY points.
/// Phase 2: read-only, no document modifications.
/// </summary>
public static class LoopAreaCalculator
{
    // Maximum tessellation points per curve to keep computation bounded
    private const int MaxTessellationPointsPerCurve = 32;

    /// <summary>
    /// Returns the approximate area of <paramref name="loop"/> in Revit internal square feet.
    /// Uses tessellation of each curve segment and the 2-D shoelace formula.
    /// Returns 0.0 if the loop cannot be tessellated.
    /// </summary>
    public static double ComputeAreaFt2(CurveLoop loop)
    {
        var points = TessellateLoop(loop);
        if (points.Count < 3) return 0.0;
        return Math.Abs(ShoelaceArea(points));
    }

    /// <summary>
    /// Returns the approximate area in square metres.
    /// </summary>
    public static double ComputeAreaM2(CurveLoop loop) =>
        ComputeAreaFt2(loop) * 0.092903;

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tessellates all curves in the loop into a flat list of unique XY points.
    /// Duplicate consecutive points are removed.
    /// </summary>
    public static List<XYZ> TessellateLoop(CurveLoop loop)
    {
        var points = new List<XYZ>();

        foreach (var curve in loop)
        {
            IList<XYZ> pts;
            try   { pts = curve.Tessellate(); }
            catch { continue; }

            // Skip the last point of each segment because it is the first point of the next
            int count = Math.Min(pts.Count - 1, MaxTessellationPointsPerCurve);
            for (int i = 0; i < count; i++)
                points.Add(pts[i]);
        }

        return points;
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 2-D shoelace formula. Returns a signed area (positive for CCW, negative for CW).
    /// Caller should take absolute value.
    /// </summary>
    private static double ShoelaceArea(IList<XYZ> pts)
    {
        double area = 0.0;
        int n = pts.Count;
        for (int i = 0; i < n; i++)
        {
            var p1 = pts[i];
            var p2 = pts[(i + 1) % n];
            area += p1.X * p2.Y - p2.X * p1.Y;
        }
        return area / 2.0;
    }
}
