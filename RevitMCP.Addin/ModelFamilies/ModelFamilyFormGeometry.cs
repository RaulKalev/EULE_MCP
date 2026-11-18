namespace RevitMCP.Addin.ModelFamilies;

/// <summary>
/// Plain vector and 2D curve maths for the form profiles of #171 (and #177's CAD profiles). Pure: points are
/// <c>double[3]</c> in millimetres; 2D work uses elements 0 and 1 (plane coordinates u, v).
/// </summary>
public static class ModelFamilyFormGeometry
{
    public static double[] Vec(double x, double y, double z = 0) => new[] { x, y, z };
    public static double[] Add(double[] a, double[] b) => new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] };
    public static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
    public static double[] Scale(double[] a, double s) => new[] { a[0] * s, a[1] * s, a[2] * s };
    public static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    public static double[] Cross(double[] a, double[] b) =>
        new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
    public static double Length(double[] a) => Math.Sqrt(Dot(a, a));
    public static double Distance(double[] a, double[] b) => Length(Sub(a, b));
    public static double Distance2D(double[] a, double[] b) => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]));

    public static double[] Normalize(double[] a)
    {
        var length = Length(a);
        return length < 1e-12 ? new[] { 0.0, 0.0, 0.0 } : Scale(a, 1 / length);
    }

    public static double[] Copy(double[] a) => new[] { a[0], a[1], a.Length > 2 ? a[2] : 0 };

    /// <summary>A point as text for messages: <c>[x, y, z]</c> rounded to 0.01 mm.</summary>
    public static string Format(double[] p) =>
        "[" + string.Join(", ", p.Take(3).Select(v => Math.Round(v, 2).ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";

    public static string Mm(double value) => Math.Round(value, 2).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The circle through an arc's start, mid and end point in 2D: centre, radius, start angle and signed sweep
    /// (positive counter-clockwise). Null when the points are collinear or start and end coincide.
    /// </summary>
    public static (double[] Center, double Radius, double StartAngle, double Sweep)? Arc2D(double[] start, double[] mid, double[] end)
    {
        var ax = start[0]; var ay = start[1];
        var bx = mid[0]; var by = mid[1];
        var cx = end[0]; var cy = end[1];
        var d = 2 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
        var scale = Math.Max(1, Math.Max(Distance2D(start, mid), Distance2D(mid, end)));
        if (Math.Abs(d) < 1e-9 * scale * scale || Distance2D(start, end) < 1e-9) return null;
        var a2 = ax * ax + ay * ay; var b2 = bx * bx + by * by; var c2 = cx * cx + cy * cy;
        var ux = (a2 * (by - cy) + b2 * (cy - ay) + c2 * (ay - by)) / d;
        var uy = (a2 * (cx - bx) + b2 * (ax - cx) + c2 * (bx - ax)) / d;
        var center = Vec(ux, uy);
        var radius = Distance2D(center, start);
        var a0 = Math.Atan2(ay - uy, ax - ux);
        var am = Math.Atan2(by - uy, bx - ux);
        var a1 = Math.Atan2(cy - uy, cx - ux);
        var ccwEnd = Positive(a1 - a0);
        var ccwMid = Positive(am - a0);
        var sweep = ccwMid < ccwEnd ? ccwEnd : ccwEnd - 2 * Math.PI;
        return (center, radius, a0, sweep);
    }

    private static double Positive(double angle)
    {
        var twoPi = 2 * Math.PI;
        angle %= twoPi;
        return angle < 0 ? angle + twoPi : angle;
    }

    /// <summary>The length of a 2D segment (an arc's arc length); 0 for an arc whose points are collinear.</summary>
    public static double Length2D(ModelFamilyFormSegment segment)
    {
        if (segment.Kind == ModelFamilyFormSegmentKind.Line) return Distance2D(segment.Start, segment.End);
        var arc = Arc2D(segment.Start, segment.Mid!, segment.End);
        return arc == null ? 0 : arc.Value.Radius * Math.Abs(arc.Value.Sweep);
    }

    /// <summary>The length of a 3D segment; an arc is measured through its three points.</summary>
    public static double Length3D(ModelFamilyFormSegment segment)
    {
        if (segment.Kind == ModelFamilyFormSegmentKind.Line) return Distance(segment.Start, segment.End);
        // Arc length from the chord and the sagitta-free formula: the circumradius of the three points.
        var a = Distance(segment.Start, segment.Mid!);
        var b = Distance(segment.Mid!, segment.End);
        var c = Distance(segment.Start, segment.End);
        var area2 = Length(Cross(Sub(segment.Mid!, segment.Start), Sub(segment.End, segment.Start)));
        if (area2 < 1e-12) return a + b;
        var radius = a * b * c / (2 * area2);
        // The angle at the mid point is the inscribed angle; the arc spans 2π − 2·that angle.
        var cosMid = Dot(Sub(segment.Start, segment.Mid!), Sub(segment.End, segment.Mid!)) / Math.Max(1e-12, a * b);
        var inscribed = Math.Acos(Math.Max(-1, Math.Min(1, cosMid)));
        return radius * (2 * Math.PI - 2 * inscribed);
    }

    /// <summary>
    /// A 2D segment as points along it (start included, end excluded), arcs split every <paramref name="maxStep"/>
    /// radians. Used for areas, containment and intersection checks.
    /// </summary>
    public static List<double[]> Discretize(ModelFamilyFormSegment segment, double maxStep = Math.PI / 16)
    {
        var points = new List<double[]> { Vec(segment.Start[0], segment.Start[1]) };
        if (segment.Kind == ModelFamilyFormSegmentKind.Line) return points;
        var arc = Arc2D(segment.Start, segment.Mid!, segment.End);
        if (arc == null) return points;
        var (center, radius, startAngle, sweep) = arc.Value;
        var steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweep) / maxStep));
        for (var i = 1; i < steps; i++)
        {
            var angle = startAngle + sweep * i / steps;
            points.Add(Vec(center[0] + radius * Math.Cos(angle), center[1] + radius * Math.Sin(angle)));
        }
        return points;
    }

    /// <summary>A closed 2D loop as a polygon (arcs discretized).</summary>
    public static List<double[]> Polygon(IEnumerable<ModelFamilyFormSegment> loop) => loop.SelectMany(s => Discretize(s)).ToList();

    /// <summary>
    /// A closed 2D loop as a polygon whose chords stay within <paramref name="sagittaMm"/> of its arcs (at most 1024
    /// chords per arc). The fixed step of <see cref="Polygon"/> cuts up to 0.5 % of the radius off an arc (5 mm on a
    /// 1 m radius), which makes a hole near a large arc look as if it touched or left the outline.
    /// </summary>
    public static List<double[]> FinePolygon(IEnumerable<ModelFamilyFormSegment> loop, double sagittaMm = 0.01)
    {
        var points = new List<double[]>();
        foreach (var segment in loop)
        {
            var step = Math.PI / 16;
            if (segment.Kind == ModelFamilyFormSegmentKind.Arc && Arc2D(segment.Start, segment.Mid!, segment.End) is { } arc && arc.Item2 > sagittaMm)
                step = Math.Min(step, Math.Max(2 * Math.Acos(1 - sagittaMm / arc.Item2), Math.Abs(arc.Item4) / 1024));
            points.AddRange(Discretize(segment, step));
        }
        return points;
    }

    /// <summary>
    /// The signed area of a closed 2D loop (positive counter-clockwise), exact for arcs: Green's theorem over each
    /// line and arc.
    /// </summary>
    public static double SignedArea(IEnumerable<ModelFamilyFormSegment> loop)
    {
        var twice = 0.0;
        foreach (var s in loop)
        {
            if (s.Kind == ModelFamilyFormSegmentKind.Line)
            {
                twice += s.Start[0] * s.End[1] - s.End[0] * s.Start[1];
                continue;
            }
            var arc = Arc2D(s.Start, s.Mid!, s.End);
            if (arc == null)
            {
                twice += s.Start[0] * s.End[1] - s.End[0] * s.Start[1];
                continue;
            }
            var (c, r, t0, sweep) = arc.Value;
            var t1 = t0 + sweep;
            twice += r * r * sweep + r * c[0] * (Math.Sin(t1) - Math.Sin(t0)) - r * c[1] * (Math.Cos(t1) - Math.Cos(t0));
        }
        return twice / 2;
    }

    /// <summary>The 2D bounds of segments, exact for arcs (their extreme points are included).</summary>
    public static (double[] Min, double[] Max) Bounds(IEnumerable<ModelFamilyFormSegment> segments)
    {
        var min = Vec(double.MaxValue, double.MaxValue);
        var max = Vec(double.MinValue, double.MinValue);
        void Include(double x, double y)
        {
            min[0] = Math.Min(min[0], x); min[1] = Math.Min(min[1], y);
            max[0] = Math.Max(max[0], x); max[1] = Math.Max(max[1], y);
        }
        foreach (var s in segments)
        {
            Include(s.Start[0], s.Start[1]);
            Include(s.End[0], s.End[1]);
            if (s.Kind != ModelFamilyFormSegmentKind.Arc) continue;
            var arc = Arc2D(s.Start, s.Mid!, s.End);
            if (arc == null) continue;
            var (c, r, t0, sweep) = arc.Value;
            for (var k = -8; k <= 8; k++)
            {
                var angle = k * Math.PI / 2;
                var offset = sweep >= 0 ? angle - t0 : t0 - angle;
                if (offset > 1e-12 && offset < Math.Abs(sweep) - 1e-12)
                    Include(c[0] + r * Math.Cos(angle), c[1] + r * Math.Sin(angle));
            }
        }
        return (min, max);
    }

    /// <summary>Whether 2D segments ab and cd touch or cross (within <paramref name="tolerance"/>).</summary>
    public static bool SegmentsIntersect(double[] a, double[] b, double[] c, double[] d, double tolerance = 1e-6)
    {
        double Orient(double[] p, double[] q, double[] r) => (q[0] - p[0]) * (r[1] - p[1]) - (q[1] - p[1]) * (r[0] - p[0]);
        var d1 = Orient(c, d, a);
        var d2 = Orient(c, d, b);
        var d3 = Orient(a, b, c);
        var d4 = Orient(a, b, d);
        if ((d1 > tolerance && d2 < -tolerance || d1 < -tolerance && d2 > tolerance) &&
            (d3 > tolerance && d4 < -tolerance || d3 < -tolerance && d4 > tolerance))
            return true;
        return DistancePointSegment(a, c, d) <= tolerance || DistancePointSegment(b, c, d) <= tolerance ||
               DistancePointSegment(c, a, b) <= tolerance || DistancePointSegment(d, a, b) <= tolerance;
    }

    public static double DistancePointSegment(double[] p, double[] a, double[] b)
    {
        var dx = b[0] - a[0]; var dy = b[1] - a[1];
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared < 1e-18) return Distance2D(p, a);
        var t = ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / lengthSquared;
        t = Math.Max(0, Math.Min(1, t));
        return Distance2D(p, Vec(a[0] + t * dx, a[1] + t * dy));
    }

    /// <summary>Whether a 2D point lies inside a polygon (ray casting; a point on an edge counts as outside).</summary>
    public static bool Inside(double[] point, IReadOnlyList<double[]> polygon)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var pi = polygon[i]; var pj = polygon[j];
            if (DistancePointSegment(point, pi, pj) < 1e-6) return false;
            if (pi[1] > point[1] != pj[1] > point[1] &&
                point[0] < (pj[0] - pi[0]) * (point[1] - pi[1]) / (pj[1] - pi[1]) + pi[0])
                inside = !inside;
        }
        return inside;
    }

    /// <summary>
    /// The first place a closed polygon crosses or touches itself (edges that are not neighbours), or null. The
    /// result is the pair of edge indices.
    /// </summary>
    public static (int First, int Second)? SelfIntersection(IReadOnlyList<double[]> polygon, double tolerance = 1e-6)
    {
        var n = polygon.Count;
        if (n < 4) return null;
        for (var i = 0; i < n; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % n];
            for (var j = i + 2; j < n; j++)
            {
                if (i == 0 && j == n - 1) continue;
                if (SegmentsIntersect(a, b, polygon[j], polygon[(j + 1) % n], tolerance)) return (i, j);
            }
        }
        return null;
    }

    /// <summary>Whether two closed polygons cross or touch.</summary>
    public static bool PolygonsIntersect(IReadOnlyList<double[]> first, IReadOnlyList<double[]> second, double tolerance = 1e-6)
    {
        for (var i = 0; i < first.Count; i++)
            for (var j = 0; j < second.Count; j++)
                if (SegmentsIntersect(first[i], first[(i + 1) % first.Count], second[j], second[(j + 1) % second.Count], tolerance))
                    return true;
        return false;
    }
}
