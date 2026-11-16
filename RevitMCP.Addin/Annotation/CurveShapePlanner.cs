using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Annotation;

/// <summary>A point in mm. For detail curves X and Y are view-plane coordinates and Z is always 0.</summary>
public readonly struct CurvePoint
{
    public CurvePoint(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public double X { get; }
    public double Y { get; }
    public double Z { get; }

    public static CurvePoint operator +(CurvePoint a, CurvePoint b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static CurvePoint operator -(CurvePoint a, CurvePoint b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static CurvePoint operator *(CurvePoint a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public double Dot(CurvePoint o) => X * o.X + Y * o.Y + Z * o.Z;
    public CurvePoint Cross(CurvePoint o) => new(Y * o.Z - Z * o.Y, Z * o.X - X * o.Z, X * o.Y - Y * o.X);
    public double DistanceTo(CurvePoint o) => (this - o).Length;
    public CurvePoint Unit() { var l = Length; return l < 1e-12 ? this : this * (1 / l); }
}

/// <summary>One native Revit curve to create: a line, an arc (by start, mid, end) or a full circle.</summary>
public sealed class PlannedCurve
{
    /// <summary>"line", "arc" or "circle".</summary>
    public string Kind { get; set; } = "line";
    public CurvePoint Start { get; set; }
    public CurvePoint Mid { get; set; }
    public CurvePoint End { get; set; }
    public CurvePoint Center { get; set; }
    public double Radius { get; set; }
    /// <summary>The circle's axes in its plane (model curves, 3D); for detail curves the view's own right and up.</summary>
    public CurvePoint XAxis { get; set; }
    public CurvePoint YAxis { get; set; }
    public double LengthMm { get; set; }
    /// <summary>Signed sweep of an arc in degrees (counter-clockwise positive); 360 for a circle; 0 for a line.</summary>
    public double SweepDeg { get; set; }
}

/// <summary>One entry of <c>curves</c>: what it asked for, the Revit curves it becomes, or why it cannot be drawn.</summary>
public sealed class PlannedShape
{
    public int Index { get; set; }
    /// <summary>"line", "arc", "circle" or "polyline"; the text given when it is not recognised.</summary>
    public string Shape { get; set; } = string.Empty;
    public string? LineStyle { get; set; }
    public bool Closed { get; set; }
    public List<PlannedCurve> Curves { get; } = new();
    public string? Error { get; set; }
    public double TotalLengthMm => Curves.Sum(c => c.LengthMm);
}

/// <summary>
/// Argument parsing and geometry of revit_create_curves (#161): lines, circular arcs (centre or three points), full circles
/// and polylines, in mm. Detail curves take view-plane x and y; model curves take x, y, z. Pure, unit tested.
/// </summary>
public static class CurveShapePlanner
{
    public const int MaxShapes = 500;
    public const int MaxCurves = 5000;
    /// <summary>How far a point may sit off a plane in mm before it is rejected rather than silently projected.</summary>
    public const double OffPlaneToleranceMm = 0.2;
    private const double CollinearSine = 1e-6;

    private static readonly string[] Planes = { "xy", "xz", "yz" };

    /// <summary>Plans every entry; an entry that cannot be drawn carries its error and the others are unaffected.</summary>
    public static List<PlannedShape>? Plan(JArray? curves, bool detail, double shortCurveMm, out string? error)
    {
        error = null;
        if (curves == null || curves.Count == 0)
        {
            error = "Provide 'curves': a non-empty JSON array of {shape: line|arc|circle|polyline, ...} in millimetres.";
            return null;
        }
        if (curves.Count > MaxShapes)
        {
            error = $"'curves' has {curves.Count} entries; the limit is {MaxShapes} per call.";
            return null;
        }

        var shapes = new List<PlannedShape>();
        var total = 0;
        for (var i = 0; i < curves.Count; i++)
        {
            var shape = PlanOne(curves[i] as JObject, i + 1, detail, shortCurveMm);
            total += shape.Curves.Count;
            if (total > MaxCurves)
            {
                error = $"The entries make more than {MaxCurves} Revit curves; split the call.";
                return null;
            }
            shapes.Add(shape);
        }
        return shapes;
    }

    public static PlannedShape PlanOne(JObject? item, int index, bool detail, double shortCurveMm)
    {
        var shape = new PlannedShape { Index = index };
        if (item == null)
        {
            shape.Error = "Each entry must be a JSON object.";
            return shape;
        }

        var name = (item["shape"]?.Type == JTokenType.String ? item["shape"]!.Value<string>() : null)?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(name))
            name = item["points"] != null ? "polyline" : item["radius"] != null && item["start"] == null ? "arc" : "line";
        shape.Shape = name!;
        var style = item["lineStyle"]?.Type == JTokenType.String ? item["lineStyle"]!.Value<string>() : null;
        shape.LineStyle = string.IsNullOrWhiteSpace(style) ? null : style!.Trim();

        string? failure;
        switch (name)
        {
            case "line": failure = PlanLine(item, detail, shortCurveMm, shape); break;
            case "arc": failure = PlanArc(item, detail, shortCurveMm, shape); break;
            case "circle": failure = PlanCircle(item, detail, shortCurveMm, shape); break;
            case "polyline": failure = PlanPolyline(item, detail, shortCurveMm, shape); break;
            default: failure = $"Unknown shape '{name}'. Use line, arc, circle or polyline."; break;
        }
        if (failure != null)
        {
            shape.Curves.Clear();
            shape.Error = failure;
        }
        return shape;
    }

    // ── line ──

    private static string? PlanLine(JObject item, bool detail, double shortMm, PlannedShape shape)
    {
        var z = ZRejection(item, detail, "z1", "z2");
        if (z != null) return z;
        var a = Coordinates(item, "x1", "y1", "z1", detail, out var e1);
        if (e1 != null) return e1;
        var b = Coordinates(item, "x2", "y2", "z2", detail, out var e2);
        if (e2 != null) return e2;
        var length = a.DistanceTo(b);
        if (length < shortMm) return $"The line is {length:0.###} mm long, below Revit's short curve tolerance ({shortMm:0.###} mm).";
        shape.Curves.Add(new PlannedCurve { Kind = "line", Start = a, End = b, LengthMm = length });
        return null;
    }

    // ── arc ──

    private static string? PlanArc(JObject item, bool detail, double shortMm, PlannedShape shape)
    {
        var hasThrough = item["through"] != null || item["start"] != null || item["end"] != null;
        var hasCenter = item["center"] != null || item["radius"] != null || item["startAngleDeg"] != null;
        if (hasThrough && hasCenter)
            return "Give an arc either as start, through and end points or as center, radius, startAngleDeg and endAngleDeg/sweepDeg, not both.";
        return hasThrough ? ArcThroughPoints(item, detail, shortMm, shape) : ArcFromCenter(item, detail, shortMm, shape);
    }

    private static string? ArcThroughPoints(JObject item, bool detail, double shortMm, PlannedShape shape)
    {
        if (item["plane"] != null) return "'plane' applies to centre arcs only; three points define their own plane.";
        var a = PointOf(item["start"], "start", detail, out var ea);
        if (ea != null) return ea;
        var m = PointOf(item["through"], "through", detail, out var em);
        if (em != null) return em;
        var b = PointOf(item["end"], "end", detail, out var eb);
        if (eb != null) return eb;

        foreach (var (p, q, what) in new[] { (a, m, "start and through"), (m, b, "through and end"), (a, b, "start and end") })
            if (p.DistanceTo(q) < shortMm)
                return $"The {what} points are {p.DistanceTo(q):0.###} mm apart: below Revit's short curve tolerance, or the same point.";

        var ab = b - a;
        var am = m - a;
        var cross = am.Cross(ab);
        var sine = cross.Length / (am.Length * ab.Length);
        if (sine < CollinearSine) return "The three points are collinear: they do not define an arc. Use a line.";

        // Orient the normal towards +Z (else +X, +Y) so the sign of the sweep reads the same for every point order:
        // counter-clockwise seen from above is positive.
        var normal = cross.Unit();
        var lead = Math.Abs(normal.Z) > 1e-9 ? normal.Z : Math.Abs(normal.X) > 1e-9 ? normal.X : normal.Y;
        if (lead < 0) normal = normal * -1;
        // Circumcentre of a, m, b: a + ((|am|^2 ab - |ab|^2 am) x (am x ab)) / (2 |am x ab|^2)
        var numerator = (ab * am.Dot(am) - am * ab.Dot(ab)).Cross(cross);
        var center = a + numerator * (1 / (2 * cross.Dot(cross)));
        var radius = center.DistanceTo(a);
        var u = (a - center).Unit();
        var v = normal.Cross(u);
        double Angle(CurvePoint p)
        {
            var t = Math.Atan2((p - center).Dot(v), (p - center).Dot(u));
            return t < 0 ? t + 2 * Math.PI : t;
        }
        var thetaM = Angle(m);
        var thetaB = Angle(b);
        var sweep = thetaM < thetaB ? thetaB : thetaB - 2 * Math.PI;
        var sweepDeg = sweep * 180 / Math.PI;
        // The plane of a-m-b read with the normal above runs counter-clockwise when sweep > 0; keep the sign for the reader.
        shape.Curves.Add(new PlannedCurve
        {
            Kind = "arc", Start = a, Mid = m, End = b, Center = center, Radius = radius,
            XAxis = u, YAxis = v, SweepDeg = sweepDeg, LengthMm = Math.Abs(sweep) * radius
        });
        return null;
    }

    private static string? ArcFromCenter(JObject item, bool detail, double shortMm, PlannedShape shape)
    {
        var geometry = CenterGeometry(item, detail, shortMm, out var radius, out var axisA, out var axisB, out var center);
        if (geometry != null) return geometry;

        var startDeg = Num(item["startAngleDeg"], "startAngleDeg", out var es);
        if (es != null) return es;
        if (startDeg == null) return "'startAngleDeg' is required: degrees counter-clockwise from the plane's first axis (x for xy and xz, y for yz; the view's right for detail).";
        double sweepDeg;
        if (item["sweepDeg"] != null && item["endAngleDeg"] != null)
            return "Give endAngleDeg or sweepDeg, not both.";
        if (item["sweepDeg"] != null)
        {
            var sw = Num(item["sweepDeg"], "sweepDeg", out var ew);
            if (ew != null) return ew;
            sweepDeg = sw ?? 0;
        }
        else if (item["endAngleDeg"] != null)
        {
            var endDeg = Num(item["endAngleDeg"], "endAngleDeg", out var ee);
            if (ee != null) return ee;
            sweepDeg = ((endDeg ?? 0) - startDeg.Value) % 360.0;
            if (sweepDeg < 0) sweepDeg += 360.0;
            if (Math.Abs(sweepDeg) < 1e-9)
                return "startAngleDeg and endAngleDeg are the same angle: the sweep is 0. Use shape 'circle' for a full circle.";
        }
        else return "An arc needs endAngleDeg or sweepDeg (degrees, counter-clockwise from the start angle).";

        if (Math.Abs(sweepDeg) < 0.01) return $"The sweep is {sweepDeg:0.####} degrees: too small to be an arc.";
        if (Math.Abs(sweepDeg) >= 360.0 - 1e-9) return "A sweep of 360 degrees or more is a circle: use shape 'circle'.";

        var start0 = startDeg.Value *Math.PI / 180.0;
        var sweep = sweepDeg * Math.PI / 180.0;
        CurvePoint At(double t) => center + (axisA * Math.Cos(t) + axisB * Math.Sin(t)) * radius;
        var s = At(start0);
        var m = At(start0 + sweep / 2);
        var e = At(start0 + sweep);
        var arcLength = Math.Abs(sweep) * radius;
        if (s.DistanceTo(e) < shortMm || arcLength < shortMm)
            return $"The arc is too short ({arcLength:0.###} mm long, chord {s.DistanceTo(e):0.###} mm): below Revit's short curve tolerance ({shortMm:0.###} mm).";
        shape.Curves.Add(new PlannedCurve
        {
            Kind = "arc", Start = s, Mid = m, End = e, Center = center, Radius = radius,
            XAxis = axisA, YAxis = axisB, SweepDeg = sweepDeg, LengthMm = arcLength
        });
        return null;
    }

    // ── circle ──

    private static string? PlanCircle(JObject item, bool detail, double shortMm, PlannedShape shape)
    {
        var geometry = CenterGeometry(item, detail, shortMm, out var radius, out var axisA, out var axisB, out var center);
        if (geometry != null) return geometry;
        shape.Curves.Add(new PlannedCurve
        {
            Kind = "circle", Start = center + axisA * radius, End = center + axisA * radius, Mid = center - axisA * radius,
            Center = center, Radius = radius, XAxis = axisA, YAxis = axisB, SweepDeg = 360, LengthMm = 2 * Math.PI * radius
        });
        return null;
    }

    /// <summary>Centre, radius and the plane's axes of an arc or circle given by a centre; the reason when they are unusable.</summary>
    private static string? CenterGeometry(JObject item, bool detail, double shortMm,
        out double radius, out CurvePoint axisA, out CurvePoint axisB, out CurvePoint center)
    {
        radius = 0;
        axisA = new CurvePoint(1, 0, 0);
        axisB = new CurvePoint(0, 1, 0);
        center = default;

        if (item["center"] == null) return "'center' {x, y} (model curves also z) is required.";
        center = PointOf(item["center"], "center", detail, out var ec);
        if (ec != null) return ec;
        if (item["radius"] == null) return "'radius' (mm) is required.";
        var r = Num(item["radius"], "radius", out var er);
        if (er != null) return er;
        radius = r!.Value;
        if (radius <= 0) return $"The radius must be above zero (got {radius:0.###} mm).";
        if (radius < shortMm) return $"The radius {radius:0.###} mm is below Revit's short curve tolerance ({shortMm:0.###} mm).";

        var plane = item["plane"]?.Type == JTokenType.String ? item["plane"]!.Value<string>()!.Trim().ToLowerInvariant() : null;
        if (item["plane"] != null && plane == null) return "'plane' must be \"xy\", \"xz\" or \"yz\".";
        if (detail)
            return plane != null ? "'plane' is for model curves: a detail curve lies in the view's plane." : null;
        plane ??= "xy";
        if (Array.IndexOf(Planes, plane) < 0) return $"Unknown plane '{plane}'. Use xy, xz or yz.";
        if (plane == "xz") axisB = new CurvePoint(0, 0, 1);
        else if (plane == "yz") { axisA = new CurvePoint(0, 1, 0); axisB = new CurvePoint(0, 0, 1); }
        return null;
    }

    // ── polyline ──

    private static string? PlanPolyline(JObject item, bool detail, double shortMm, PlannedShape shape)
    {
        if (item["points"] is not JArray raw || raw.Count < 2)
            return "A polyline needs 'points': an array of at least 2 {x, y} points (model curves also z).";
        var closed = item["closed"] != null && item["closed"]!.Type == JTokenType.Boolean && item["closed"]!.Value<bool>();
        if (item["closed"] != null && item["closed"]!.Type != JTokenType.Boolean) return "'closed' must be true or false.";
        if (closed && raw.Count < 3) return "A closed polyline needs at least 3 points.";
        shape.Closed = closed;

        var points = new List<CurvePoint>();
        for (var i = 0; i < raw.Count; i++)
        {
            var p = PointOf(raw[i], $"points[{i + 1}]", detail, out var ep);
            if (ep != null) return ep;
            points.Add(p);
        }

        var segments = closed ? points.Count : points.Count - 1;
        for (var i = 0; i < segments; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            var length = a.DistanceTo(b);
            if (length < shortMm)
            {
                var closing = closed && i == segments - 1;
                return closing && length < 1e-9
                    ? "The last point repeats the first. Leave it out and set closed=true."
                    : $"Segment {i + 1} (points {i + 1} to {(i + 1) % points.Count + 1}) is {length:0.###} mm long, below Revit's short curve tolerance ({shortMm:0.###} mm).";
            }
        }

        if (closed && !detail)
        {
            var planar = CoplanarityError(points);
            if (planar != null) return planar;
        }

        for (var i = 0; i < segments; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            shape.Curves.Add(new PlannedCurve { Kind = "line", Start = a, End = b, LengthMm = a.DistanceTo(b) });
        }
        return null;
    }

    /// <summary>Null when the points lie on one plane (within <see cref="OffPlaneToleranceMm"/>); otherwise why not.</summary>
    public static string? CoplanarityError(IReadOnlyList<CurvePoint> points)
    {
        if (points.Count < 4) return CollinearError(points);
        // Normal from the pair of edges with the largest cross product: robust to a first few nearly collinear points.
        var normal = default(CurvePoint);
        var best = 0.0;
        for (var i = 1; i < points.Count - 1; i++)
        {
            var c = (points[i] - points[0]).Cross(points[i + 1] - points[0]);
            if (c.Length > best) { best = c.Length; normal = c; }
        }
        if (best < 1e-6) return "The points are collinear: they do not enclose a shape.";
        normal = normal.Unit();
        for (var i = 0; i < points.Count; i++)
        {
            var off = Math.Abs((points[i] - points[0]).Dot(normal));
            if (off > OffPlaneToleranceMm)
                return $"Point {i + 1} is {off:0.###} mm off the plane of the other points (tolerance {OffPlaneToleranceMm} mm): a closed polyline must be flat. Fix the z values or draw it open.";
        }
        return null;
    }

    private static string? CollinearError(IReadOnlyList<CurvePoint> points) =>
        points.Count >= 3 && (points[1] - points[0]).Cross(points[2] - points[0]).Length < 1e-6
            ? "The points are collinear: they do not enclose a shape."
            : null;

    // ── reading numbers and points ──

    private static string? ZRejection(JObject item, bool detail, params string[] keys)
    {
        if (!detail) return null;
        foreach (var key in keys)
            if (item[key] != null && item[key]!.Type != JTokenType.Null)
                return $"'{key}' is not accepted for detail curves: they lie in the view's plane. Use x and y in the view's coordinates, or kind=model.";
        return null;
    }

    private static CurvePoint Coordinates(JObject item, string kx, string ky, string kz, bool detail, out string? error)
    {
        error = null;
        var x = Num(item[kx], kx, out var ex);
        if (ex != null) { error = ex; return default; }
        var y = Num(item[ky], ky, out var ey);
        if (ey != null) { error = ey; return default; }
        if (x == null || y == null) { error = $"{kx} and {ky} are required."; return default; }
        double z = 0;
        if (!detail)
        {
            var zz = Num(item[kz], kz, out var ez);
            if (ez != null) { error = ez; return default; }
            z = zz ?? 0;
        }
        return new CurvePoint(x.Value, y.Value, z);
    }

    /// <summary>A point from {x, y, z?} or [x, y, z?]; model z defaults to 0, a detail point with a z is refused.</summary>
    private static CurvePoint PointOf(JToken? token, string what, bool detail, out string? error)
    {
        error = null;
        JToken? tx, ty, tz;
        if (token is JObject o) { tx = o["x"]; ty = o["y"]; tz = o["z"]; }
        else if (token is JArray a && (a.Count == 2 || a.Count == 3))
        {
            tx = a[0]; ty = a[1]; tz = a.Count == 3 ? a[2] : null;
        }
        else
        {
            error = $"'{what}' must be a point {{x, y}} (model curves also z).";
            return default;
        }

        var x = Num(tx, $"{what}.x", out var ex);
        if (ex != null) { error = ex; return default; }
        var y = Num(ty, $"{what}.y", out var ey);
        if (ey != null) { error = ey; return default; }
        if (x == null || y == null) { error = $"'{what}' needs x and y."; return default; }
        double z = 0;
        if (tz != null && tz.Type != JTokenType.Null)
        {
            if (detail)
            {
                error = $"'{what}' has a z: detail curves lie in the view's plane, so give x and y only (or use kind=model).";
                return default;
            }
            var zz = Num(tz, $"{what}.z", out var ez);
            if (ez != null) { error = ez; return default; }
            z = zz ?? 0;
        }
        return new CurvePoint(x.Value, y.Value, z);
    }

    /// <summary>A finite number; null with no error when absent, null with the reason when present but not a finite number.</summary>
    private static double? Num(JToken? token, string what, out string? error)
    {
        error = null;
        if (token == null || token.Type == JTokenType.Null) return null;
        if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float &&
            !(token.Type == JTokenType.String && double.TryParse(token.Value<string>(),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)))
        {
            error = $"'{what}' must be a number.";
            return null;
        }
        double value;
        try { value = token.Value<double>(); }
        catch (Exception) { error = $"'{what}' must be a number."; return null; }
        if (double.IsNaN(value) || double.IsInfinity(value)) { error = $"'{what}' must be a finite number."; return null; }
        return value;
    }
}
