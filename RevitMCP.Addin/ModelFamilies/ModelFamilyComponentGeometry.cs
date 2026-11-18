using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.ModelFamilies;

/// <summary>
/// One curve of a symbolic or model line list (#175): a line, a three-point arc or a circle. Pure; points are
/// millimetres. A curve is validated before it is projected onto its plane (what the caller wrote) and again after
/// (a line that runs straight along the plane normal collapses to a point on it).
/// </summary>
public sealed class ComponentCurveSpec
{
    public const double MinimumSizeMm = 0.1;

    /// <summary>"line", "arc" or "circle".</summary>
    public string Kind { get; set; } = string.Empty;
    /// <summary>Line: start, end. Arc: start, mid, end. Circle: centre.</summary>
    public List<double[]> Points { get; set; } = new();
    public double Radius { get; set; }
    public string Source { get; set; } = string.Empty;

    public static readonly string[] Kinds = { "line", "arc", "circle" };

    /// <summary>Reads <c>{"line":{start,end}}</c>, <c>{"arc":{start,mid,end}}</c> or <c>{"circle":{center,radius}}</c>.</summary>
    public static ComponentCurveSpec? Parse(JToken? token, string source, List<string> problems)
    {
        if (token is not JObject item)
        {
            problems.Add($"{source}: must be an object like {{\"line\":{{\"start\":[0,0],\"end\":[100,0]}}}}, {{\"arc\":{{start,mid,end}}}} or {{\"circle\":{{center,radius}}}}.");
            return null;
        }
        var present = Kinds.Where(k => ModelFamilyJson.Has(item, k)).ToList();
        foreach (var property in item.Properties())
            if (!Kinds.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                problems.Add($"{source}.{property.Name}: unknown key. Give exactly one of line, arc, circle.");
        if (present.Count != 1)
        {
            if (present.Count == 0) problems.Add($"{source}: give one of line, arc, circle.");
            else problems.Add($"{source}: give only one of {string.Join(", ", present)} per curve.");
            return null;
        }
        var kind = present[0];
        var body = ModelFamilyJson.Get(item, kind) as JObject;
        if (body == null)
        {
            problems.Add($"{source}.{kind}: must be an object.");
            return null;
        }
        var spec = new ComponentCurveSpec { Kind = kind, Source = source };
        var before = problems.Count;
        switch (kind)
        {
            case "line":
                ModelFamilyJson.CheckKeys(body, $"{source}.line", problems, "start", "end");
                AddPoint(spec, body, "start", $"{source}.line", problems);
                AddPoint(spec, body, "end", $"{source}.line", problems);
                break;
            case "arc":
                ModelFamilyJson.CheckKeys(body, $"{source}.arc", problems, "start", "mid", "end");
                AddPoint(spec, body, "start", $"{source}.arc", problems);
                AddPoint(spec, body, "mid", $"{source}.arc", problems);
                AddPoint(spec, body, "end", $"{source}.arc", problems);
                break;
            default:
                ModelFamilyJson.CheckKeys(body, $"{source}.circle", problems, "center", "radius");
                AddPoint(spec, body, "center", $"{source}.circle", problems);
                var radius = ModelFamilyJson.Number(body, "radius", $"{source}.circle", problems);
                if (radius == null && !ModelFamilyJson.Has(body, "radius")) problems.Add($"{source}.circle.radius is required (millimetres).");
                spec.Radius = radius ?? 0;
                break;
        }
        if (problems.Count > before) return null;
        var error = spec.Validate();
        if (error != null)
        {
            problems.Add($"{source}: {error}");
            return null;
        }
        return spec;
    }

    private static void AddPoint(ComponentCurveSpec spec, JObject body, string key, string source, List<string> problems)
    {
        var point = ModelFamilyJson.Point(body, key, source, problems);
        if (point != null) spec.Points.Add(point);
    }

    /// <summary>Null when the curve can be drawn; otherwise why it cannot (a degenerate line, arc or circle).</summary>
    public string? Validate()
    {
        switch (Kind)
        {
            case "line":
                if (Points.Count != 2) return "a line needs start and end.";
                return ModelFamilyComponentGeometry.Distance(Points[0], Points[1]) < MinimumSizeMm
                    ? $"the line is shorter than {MinimumSizeMm} mm (start and end are the same point, or differ only along the plane's normal). Give two different points on the plane."
                    : null;
            case "arc":
                if (Points.Count != 3) return "an arc needs start, mid and end.";
                if (ModelFamilyComponentGeometry.Distance(Points[0], Points[1]) < MinimumSizeMm ||
                    ModelFamilyComponentGeometry.Distance(Points[1], Points[2]) < MinimumSizeMm ||
                    ModelFamilyComponentGeometry.Distance(Points[0], Points[2]) < MinimumSizeMm)
                    return "the arc needs three different points (start, mid and end are too close). A full circle is {\"circle\":{center,radius}}.";
                return ModelFamilyComponentGeometry.AreCollinear(Points[0], Points[1], Points[2])
                    ? "the arc's start, mid and end lie on one line; move mid off the chord, or use a line."
                    : null;
            case "circle":
                if (Points.Count != 1) return "a circle needs center and radius.";
                return Radius < MinimumSizeMm ? $"the circle's radius must be at least {MinimumSizeMm} mm." : null;
            default:
                return $"unknown curve kind '{Kind}'.";
        }
    }

    /// <summary>The same curve with every point moved onto the plane through <paramref name="origin"/> with <paramref name="normal"/>.</summary>
    public ComponentCurveSpec Project(double[] origin, double[] normal) => new()
    {
        Kind = Kind,
        Radius = Radius,
        Source = Source,
        Points = Points.Select(p => ModelFamilyComponentGeometry.ProjectOntoPlane(p, origin, normal)).ToList()
    };
}

/// <summary>Plain-double vector maths for the symbolic and model line lists (#175). Millimetres, no Revit types.</summary>
public static class ModelFamilyComponentGeometry
{
    public static double[] Subtract(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };

    public static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

    public static double[] Cross(double[] a, double[] b) => new[]
    {
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0]
    };

    public static double Length(double[] v) => Math.Sqrt(Dot(v, v));

    public static double Distance(double[] a, double[] b) => Length(Subtract(a, b));

    public static double[] Normalize(double[] v)
    {
        var length = Length(v);
        return length < 1e-12 ? new double[] { 0, 0, 0 } : new[] { v[0] / length, v[1] / length, v[2] / length };
    }

    /// <summary>The point on the plane (through <paramref name="origin"/>, with <paramref name="normal"/>) nearest to <paramref name="point"/>.</summary>
    public static double[] ProjectOntoPlane(double[] point, double[] origin, double[] normal)
    {
        var n = Normalize(normal);
        var distance = Dot(Subtract(point, origin), n);
        return new[] { point[0] - distance * n[0], point[1] - distance * n[1], point[2] - distance * n[2] };
    }

    /// <summary>Signed distance of a point from the plane, in the unit of the inputs.</summary>
    public static double DistanceToPlane(double[] point, double[] origin, double[] normal) =>
        Dot(Subtract(point, origin), Normalize(normal));

    /// <summary>Three points on (almost) one line: the triangle they span has a negligible height.</summary>
    public static bool AreCollinear(double[] a, double[] b, double[] c)
    {
        var ab = Subtract(b, a);
        var ac = Subtract(c, a);
        var area2 = Length(Cross(ab, ac));
        var scale = Length(ab) * Length(ac);
        return scale < 1e-12 || area2 / scale < 1e-6;
    }
}
