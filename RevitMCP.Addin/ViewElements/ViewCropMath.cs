namespace RevitMCP.Addin.ViewElements;

/// <summary>
/// Is an element's box inside the view's crop region (#136)? The Revit API says a view's collector may pass elements
/// outside the crop and tells callers to compare the boundaries, which is done here. Pure; everything is in the crop's
/// own coordinates and the crop shape is one or more closed polygons. Conservative: a box touching the crop stays.
/// </summary>
public static class ViewCropMath
{
    public const double Tolerance = 1e-6;

    public static bool BoxOverlapsCrop(
        double minX, double minY, double maxX, double maxY,
        IReadOnlyList<IReadOnlyList<(double X, double Y)>> cropLoops)
    {
        foreach (var loop in cropLoops)
            if (BoxOverlapsPolygon(minX, minY, maxX, maxY, loop)) return true;
        return false;
    }

    public static bool BoxOverlapsPolygon(double minX, double minY, double maxX, double maxY, IReadOnlyList<(double X, double Y)> polygon)
    {
        if (polygon.Count < 3) return false;

        foreach (var p in polygon)
            if (p.X >= minX - Tolerance && p.X <= maxX + Tolerance && p.Y >= minY - Tolerance && p.Y <= maxY + Tolerance)
                return true;

        (double X, double Y)[] corners = { (minX, minY), (maxX, minY), (maxX, maxY), (minX, maxY) };
        foreach (var corner in corners)
            if (Inside(corner, polygon)) return true;

        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            for (var j = 0; j < 4; j++)
                if (SegmentsCross(a, b, corners[j], corners[(j + 1) % 4])) return true;
        }
        return false;
    }

    private static bool Inside((double X, double Y) point, IReadOnlyList<(double X, double Y)> polygon)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];
            if ((a.Y > point.Y) != (b.Y > point.Y) &&
                point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    private static bool SegmentsCross((double X, double Y) a, (double X, double Y) b, (double X, double Y) c, (double X, double Y) d)
    {
        var d1 = Cross(c, d, a);
        var d2 = Cross(c, d, b);
        var d3 = Cross(a, b, c);
        var d4 = Cross(a, b, d);
        return ((d1 > 0) != (d2 > 0)) && ((d3 > 0) != (d4 > 0));
    }

    private static double Cross((double X, double Y) o, (double X, double Y) p, (double X, double Y) q) =>
        (p.X - o.X) * (q.Y - o.Y) - (p.Y - o.Y) * (q.X - o.X);
}
