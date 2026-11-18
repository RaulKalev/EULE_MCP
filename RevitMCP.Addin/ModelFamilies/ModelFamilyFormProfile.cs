using Newtonsoft.Json.Linq;
using static RevitMCP.Addin.ModelFamilies.ModelFamilyFormGeometry;

namespace RevitMCP.Addin.ModelFamilies;

public enum ModelFamilyFormSegmentKind { Line, Arc }

/// <summary>
/// One curve of a profile loop or path: a line (start, end) or an arc through three points (start, mid, end).
/// Points are <c>double[3]</c> in millimetres: family coordinates before <see cref="ModelFamilyFormProfile.OnPlane"/>,
/// plane coordinates <c>[u, v, 0]</c> or family coordinates on the plane after it.
/// </summary>
public sealed class ModelFamilyFormSegment
{
    private ModelFamilyFormSegment(ModelFamilyFormSegmentKind kind, double[] start, double[]? mid, double[] end)
    {
        Kind = kind;
        Start = start;
        Mid = mid;
        End = end;
    }

    public ModelFamilyFormSegmentKind Kind { get; }
    /// <summary>Settable so a gap within the closure tolerance can be snapped shut.</summary>
    public double[] Start { get; set; }
    /// <summary>A point on an arc between start and end; null for a line.</summary>
    public double[]? Mid { get; }
    public double[] End { get; set; }

    public static ModelFamilyFormSegment Line(double[] start, double[] end) => new(ModelFamilyFormSegmentKind.Line, start, null, end);
    public static ModelFamilyFormSegment Arc(double[] start, double[] mid, double[] end) => new(ModelFamilyFormSegmentKind.Arc, start, mid, end);

    public ModelFamilyFormSegment Map(Func<double[], double[]> map) =>
        new(Kind, map(Start), Mid == null ? null : map(Mid), map(End));

    /// <summary>The segment in the wire format (<c>{line:{start,end}}</c> or <c>{arc:{start,mid,end}}</c>), millimetres.</summary>
    public JObject ToJson() => Kind == ModelFamilyFormSegmentKind.Line
        ? new JObject { ["line"] = new JObject { ["start"] = Point(Start), ["end"] = Point(End) } }
        : new JObject { ["arc"] = new JObject { ["start"] = Point(Start), ["mid"] = Point(Mid!), ["end"] = Point(End) } };

    private static JArray Point(double[] p) => new(p.Take(3).Select(v => Math.Round(v, 3)));

    /// <summary>
    /// Reads <c>{"line": {"start": p, "end": p}}</c> or <c>{"arc": {"start": p, "mid": p, "end": p}}</c>; points
    /// <c>[x, y]</c> or <c>[x, y, z]</c> millimetres.
    /// </summary>
    public static ModelFamilyFormSegment? Parse(JToken? token, string source, List<string> problems)
    {
        if (token is not JObject obj)
        {
            problems.Add($"{source}: must be {{\"line\": {{start, end}}}} or {{\"arc\": {{start, mid, end}}}}.");
            return null;
        }
        ModelFamilyJson.CheckKeys(obj, source, problems, "line", "arc");
        var line = ModelFamilyJson.Get(obj, "line") as JObject;
        var arc = ModelFamilyJson.Get(obj, "arc") as JObject;
        if (line != null && arc != null || line == null && arc == null)
        {
            if (obj.Properties().Any(p => p.Name.Equals("line", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("arc", StringComparison.OrdinalIgnoreCase)) || obj.Count == 0)
                problems.Add($"{source}: give exactly one of line {{start, end}} or arc {{start, mid, end}}.");
            return null;
        }
        if (line != null)
        {
            ModelFamilyJson.CheckKeys(line, $"{source}.line", problems, "start", "end");
            var start = ModelFamilyJson.Point(line, "start", $"{source}.line", problems);
            var end = ModelFamilyJson.Point(line, "end", $"{source}.line", problems);
            return start == null || end == null ? null : Line(start, end);
        }
        ModelFamilyJson.CheckKeys(arc!, $"{source}.arc", problems, "start", "mid", "end");
        var aStart = ModelFamilyJson.Point(arc!, "start", $"{source}.arc", problems);
        var aMid = ModelFamilyJson.Point(arc!, "mid", $"{source}.arc", problems);
        var aEnd = ModelFamilyJson.Point(arc!, "end", $"{source}.arc", problems);
        return aStart == null || aMid == null || aEnd == null ? null : Arc(aStart, aMid, aEnd);
    }
}

/// <summary>
/// A plane as plain doubles in millimetres: origin and two perpendicular unit axes. Revit's <c>Plane</c> converts to
/// it (<c>ModelFamilyFormBuilder.Frame</c>); pure code maps family points to plane coordinates <c>[u, v]</c> and back.
/// </summary>
public sealed class ModelFamilyFormFrame
{
    public ModelFamilyFormFrame(double[] origin, double[] xAxis, double[] yAxis)
    {
        Origin = Copy(origin);
        XAxis = Normalize(Copy(xAxis));
        // Make the y axis exactly perpendicular to x, so u and v are true plane coordinates.
        var y = Copy(yAxis);
        YAxis = Normalize(Sub(y, Scale(XAxis, Dot(y, XAxis))));
        Normal = Normalize(Cross(XAxis, YAxis));
    }

    /// <summary>The family's XY plane through the origin: plane coordinates equal family x and y.</summary>
    public static ModelFamilyFormFrame XY => new(Vec(0, 0, 0), Vec(1, 0, 0), Vec(0, 1, 0));

    /// <summary>
    /// A frame for a plane with <paramref name="normal"/> whose axes do not depend on how the plane was made. Revit
    /// documents the X and Y axes of <c>Plane.CreateByNormalAndOrigin</c> as arbitrary, so a plane square to the family
    /// axes gets fixed ones: x along family X (along family Y for a plane facing ±X) and y = normal × x, keeping the
    /// plane's normal. Rectangles then follow the family axes and width/height are measured along them. Other planes
    /// keep <paramref name="xAxis"/> and <paramref name="yAxis"/>.
    /// </summary>
    public static ModelFamilyFormFrame AlignedToFamily(double[] origin, double[] normal, double[] xAxis, double[] yAxis)
    {
        var n = Normalize(Copy(normal));
        double[]? x = null;
        if (Math.Abs(Math.Abs(n[2]) - 1) < 1e-9 || Math.Abs(Math.Abs(n[1]) - 1) < 1e-9) x = Vec(1, 0, 0);
        else if (Math.Abs(Math.Abs(n[0]) - 1) < 1e-9) x = Vec(0, 1, 0);
        return x == null ? new ModelFamilyFormFrame(origin, xAxis, yAxis) : new ModelFamilyFormFrame(origin, x, Cross(n, x));
    }

    public double[] Origin { get; }
    public double[] XAxis { get; }
    public double[] YAxis { get; }
    public double[] Normal { get; }

    /// <summary>A family point's plane coordinates <c>[u, v, 0]</c> (projected onto the plane).</summary>
    public double[] ToUv(double[] point)
    {
        var d = Sub(Copy(point), Origin);
        return Vec(Dot(d, XAxis), Dot(d, YAxis));
    }

    /// <summary>The signed distance of a family point from the plane, along <see cref="Normal"/>.</summary>
    public double OffsetOf(double[] point) => Dot(Sub(Copy(point), Origin), Normal);

    /// <summary>The family point of plane coordinates <c>[u, v]</c>, lifted by <paramref name="offset"/> along the normal.</summary>
    public double[] ToWorld(double[] uv, double offset = 0) =>
        Add(Add(Add(Origin, Scale(XAxis, uv[0])), Scale(YAxis, uv[1])), Scale(Normal, offset));
}

/// <summary>The tolerances a profile is checked with. Revit's own short-curve tolerance is about 0.79 mm.</summary>
public sealed class ModelFamilyFormCheck
{
    /// <summary>Revit's <c>Application.ShortCurveTolerance</c> (1/32 inch, about 0.79 mm), the default.</summary>
    public const double RevitShortCurveMm = 25.4 / 32;

    /// <summary>Shorter segments are refused (Revit cannot make them).</summary>
    public double MinSegmentMm { get; set; } = RevitShortCurveMm;
    /// <summary>A point further than this from the work plane is a problem; closer ones are projected silently.</summary>
    public double OffPlaneMm { get; set; } = 0.5;
    /// <summary>Gaps between segments up to this are snapped shut; larger gaps are problems.</summary>
    public double ClosureMm { get; set; } = 0.01;
    /// <summary>A loop enclosing less than this (mm²) has no area.</summary>
    public double MinAreaMm2 { get; set; } = 0.01;
}

public enum ModelFamilyFormLoopKind { Points, Rectangle, Circle, Segments }

/// <summary>
/// One loop of a profile as given: a polygon, a rectangle, a circle or chained segments, in family millimetres. It
/// becomes curves only on a plane (<see cref="ModelFamilyFormProfile.OnPlane"/>), because a rectangle's edges and a
/// circle's halves follow the plane's axes.
/// </summary>
public sealed class ModelFamilyFormLoop
{
    public ModelFamilyFormLoopKind Kind { get; init; }
    public string Source { get; init; } = string.Empty;
    public List<double[]> Points { get; init; } = new();
    public double[]? Min { get; init; }
    public double[]? Max { get; init; }
    public double[]? Center { get; init; }
    public double Radius { get; init; }
    public List<ModelFamilyFormSegment> Segments { get; init; } = new();

    /// <summary>Every point the loop was given with, for the off-plane check.</summary>
    public IEnumerable<(double[] Point, string Source)> GivenPoints()
    {
        switch (Kind)
        {
            case ModelFamilyFormLoopKind.Points:
                for (var i = 0; i < Points.Count; i++) yield return (Points[i], $"{Source}.points[{i}]");
                break;
            case ModelFamilyFormLoopKind.Rectangle:
                yield return (Min!, $"{Source}.rectangle.min");
                yield return (Max!, $"{Source}.rectangle.max");
                break;
            case ModelFamilyFormLoopKind.Circle:
                yield return (Center!, $"{Source}.circle.center");
                break;
            default:
                for (var i = 0; i < Segments.Count; i++)
                {
                    yield return (Segments[i].Start, $"{Source}.segments[{i}].start");
                    if (Segments[i].Mid != null) yield return (Segments[i].Mid!, $"{Source}.segments[{i}].mid");
                    yield return (Segments[i].End, $"{Source}.segments[{i}].end");
                }
                break;
        }
    }

    /// <summary>
    /// Reads a loop: <c>{"points": [p, …]}</c> (closed polygon, first point not repeated, at least 3),
    /// <c>{"rectangle": {"min": p, "max": p}}</c>, <c>{"circle": {"center": p, "radius": r}}</c> or
    /// <c>{"segments": [{"line": …} | {"arc": …}, …]}</c>.
    /// </summary>
    public static ModelFamilyFormLoop? Parse(JToken? token, string source, List<string> problems)
    {
        if (token is not JObject obj)
        {
            problems.Add($"{source}: a loop must be {{points}}, {{rectangle}}, {{circle}} or {{segments}}.");
            return null;
        }
        ModelFamilyJson.CheckKeys(obj, source, problems, "points", "rectangle", "circle", "segments");
        var given = new[] { "points", "rectangle", "circle", "segments" }.Where(k => ModelFamilyJson.Has(obj, k)).ToList();
        if (given.Count != 1)
        {
            problems.Add(given.Count == 0
                ? $"{source}: give one of points [[x,y,z], …], rectangle {{min, max}}, circle {{center, radius}} or segments [{{line}} | {{arc}}, …]."
                : $"{source}: give only one of {string.Join(", ", given)}.");
            return null;
        }
        switch (given[0])
        {
            case "points":
            {
                if (ModelFamilyJson.Get(obj, "points") is not JArray array)
                {
                    problems.Add($"{source}.points: must be a list of points [[x, y, z], …].");
                    return null;
                }
                var points = new List<double[]>();
                var before = problems.Count;
                for (var i = 0; i < array.Count; i++)
                    if (ModelFamilyJson.Point(array[i], $"{source}.points[{i}]", problems) is { } p) points.Add(p);
                if (problems.Count > before) return null;
                // A repeated first point closes the polygon twice; drop it rather than make a zero-length edge.
                if (points.Count > 3 && Distance(points[0], points[points.Count - 1]) < 1e-9) points.RemoveAt(points.Count - 1);
                if (points.Count < 3)
                {
                    problems.Add($"{source}.points: a closed polygon needs at least 3 points (got {points.Count}).");
                    return null;
                }
                return new ModelFamilyFormLoop { Kind = ModelFamilyFormLoopKind.Points, Source = source, Points = points };
            }
            case "rectangle":
            {
                if (ModelFamilyJson.Get(obj, "rectangle") is not JObject rect)
                {
                    problems.Add($"{source}.rectangle: must be {{\"min\": [x,y,z], \"max\": [x,y,z]}}.");
                    return null;
                }
                ModelFamilyJson.CheckKeys(rect, $"{source}.rectangle", problems, "min", "max");
                var min = ModelFamilyJson.Point(rect, "min", $"{source}.rectangle", problems);
                var max = ModelFamilyJson.Point(rect, "max", $"{source}.rectangle", problems);
                return min == null || max == null ? null : new ModelFamilyFormLoop { Kind = ModelFamilyFormLoopKind.Rectangle, Source = source, Min = min, Max = max };
            }
            case "circle":
            {
                if (ModelFamilyJson.Get(obj, "circle") is not JObject circle)
                {
                    problems.Add($"{source}.circle: must be {{\"center\": [x,y,z], \"radius\": r}}.");
                    return null;
                }
                ModelFamilyJson.CheckKeys(circle, $"{source}.circle", problems, "center", "radius");
                var center = ModelFamilyJson.Point(circle, "center", $"{source}.circle", problems);
                var radius = ModelFamilyJson.Number(circle, "radius", $"{source}.circle", problems);
                if (radius == null && !ModelFamilyJson.Has(circle, "radius")) problems.Add($"{source}.circle.radius is required (millimetres).");
                else if (radius is <= 0) problems.Add($"{source}.circle.radius: must be greater than 0 (got {Mm(radius.Value)}).");
                return center == null || radius is null or <= 0 ? null
                    : new ModelFamilyFormLoop { Kind = ModelFamilyFormLoopKind.Circle, Source = source, Center = center, Radius = radius.Value };
            }
            default:
            {
                var segments = ParseSegments(ModelFamilyJson.Get(obj, "segments"), $"{source}.segments", problems);
                return segments == null ? null : new ModelFamilyFormLoop { Kind = ModelFamilyFormLoopKind.Segments, Source = source, Segments = segments };
            }
        }
    }

    internal static List<ModelFamilyFormSegment>? ParseSegments(JToken? token, string source, List<string> problems)
    {
        if (token is not JArray array || array.Count == 0)
        {
            problems.Add($"{source}: must be a non-empty list of {{\"line\": {{start, end}}}} or {{\"arc\": {{start, mid, end}}}}.");
            return null;
        }
        var result = new List<ModelFamilyFormSegment>();
        var before = problems.Count;
        for (var i = 0; i < array.Count; i++)
            if (ModelFamilyFormSegment.Parse(array[i], $"{source}[{i}]", problems) is { } segment) result.Add(segment);
        return problems.Count > before ? null : result;
    }

    /// <summary>
    /// The loop's segments in plane coordinates <c>[u, v, 0]</c>. Checks the given points against the plane first:
    /// further than <see cref="ModelFamilyFormCheck.OffPlaneMm"/> is a problem, closer is projected.
    /// </summary>
    internal List<ModelFamilyFormSegment>? ToUv(ModelFamilyFormFrame frame, ModelFamilyFormCheck check, List<string> problems)
    {
        var before = problems.Count;
        foreach (var (point, source) in GivenPoints())
        {
            var offset = frame.OffsetOf(point);
            if (Math.Abs(offset) > check.OffPlaneMm)
                problems.Add($"{source}: {Format(point)} is {Mm(Math.Abs(offset))} mm off the work plane (more than {Mm(check.OffPlaneMm)} mm); is it the wrong workPlane, or a point of another plane?");
        }
        if (problems.Count > before) return null;
        switch (Kind)
        {
            case ModelFamilyFormLoopKind.Points:
            {
                var uv = Points.Select(frame.ToUv).ToList();
                return uv.Select((p, i) => ModelFamilyFormSegment.Line(p, uv[(i + 1) % uv.Count])).ToList();
            }
            case ModelFamilyFormLoopKind.Rectangle:
            {
                var a = frame.ToUv(Min!);
                var b = frame.ToUv(Max!);
                var u0 = Math.Min(a[0], b[0]); var u1 = Math.Max(a[0], b[0]);
                var v0 = Math.Min(a[1], b[1]); var v1 = Math.Max(a[1], b[1]);
                if (u1 - u0 < check.MinSegmentMm || v1 - v0 < check.MinSegmentMm)
                {
                    problems.Add($"{Source}.rectangle: it is {Mm(u1 - u0)} × {Mm(v1 - v0)} mm on the work plane; both sides must be at least {Mm(check.MinSegmentMm)} mm (are min and max on the plane's axes?).");
                    return null;
                }
                var corners = new[] { Vec(u0, v0), Vec(u1, v0), Vec(u1, v1), Vec(u0, v1) };
                return corners.Select((p, i) => ModelFamilyFormSegment.Line(p, corners[(i + 1) % 4])).ToList();
            }
            case ModelFamilyFormLoopKind.Circle:
            {
                // Revit refuses one closed curve in a blend or swept blend loop, so a circle is always two half arcs.
                var c = frame.ToUv(Center!);
                var r = Radius;
                var east = Vec(c[0] + r, c[1]);
                var west = Vec(c[0] - r, c[1]);
                return new List<ModelFamilyFormSegment>
                {
                    ModelFamilyFormSegment.Arc(east, Vec(c[0], c[1] + r), west),
                    ModelFamilyFormSegment.Arc(Copy(west), Vec(c[0], c[1] - r), Copy(east))
                };
            }
            default:
                return Segments.Select(s => s.Map(frame.ToUv)).ToList();
        }
    }
}

/// <summary>
/// A form profile (#171, reused by #177): a list of loops in family millimetres. The first loop is the outer
/// boundary; further loops are holes (Revit decides solid and hole by containment). <see cref="OnPlane"/> projects it
/// onto a work plane and checks it the way Revit will: segment length, closure, zero area, self-intersection, holes
/// inside the outer loop and loops that do not cross.
/// </summary>
public sealed class ModelFamilyFormProfile
{
    public string Source { get; init; } = string.Empty;
    public List<ModelFamilyFormLoop> Loops { get; init; } = new();

    /// <summary>Reads a profile: a list of loops, or one loop object (see <see cref="ModelFamilyFormLoop.Parse"/>).</summary>
    public static ModelFamilyFormProfile? Parse(JToken? token, string source, List<string> problems)
    {
        if (token == null)
        {
            problems.Add($"{source} is required: a list of loops ({{points}}, {{rectangle}}, {{circle}} or {{segments}}); the first is the outer loop.");
            return null;
        }
        var items = token is JArray array ? array.ToList() : new List<JToken> { token };
        if (items.Count == 0)
        {
            problems.Add($"{source}: give at least one loop.");
            return null;
        }
        var loops = new List<ModelFamilyFormLoop>();
        var before = problems.Count;
        for (var i = 0; i < items.Count; i++)
            if (ModelFamilyFormLoop.Parse(items[i], token is JArray ? $"{source}[{i}]" : source, problems) is { } loop) loops.Add(loop);
        return problems.Count > before ? null : new ModelFamilyFormProfile { Source = source, Loops = loops };
    }

    /// <summary>
    /// The profile on a plane: each loop in plane coordinates and in family coordinates on the plane, with its area
    /// and bounds. Every check failure is a problem naming the loop; null when there is any.
    /// </summary>
    /// <param name="singleLoop">Blends and swept blends take exactly one loop.</param>
    public ModelFamilyFormPlanarProfile? OnPlane(ModelFamilyFormFrame frame, ModelFamilyFormCheck check, List<string> problems, bool singleLoop = false)
    {
        var before = problems.Count;
        if (singleLoop && Loops.Count != 1)
        {
            problems.Add($"{Source}: must be exactly one loop (got {Loops.Count}); holes are not possible here.");
            return null;
        }
        var loops = new List<ModelFamilyFormPlanarLoop>();
        foreach (var loop in Loops)
        {
            var uv = loop.ToUv(frame, check, problems);
            if (uv == null) continue;
            if (!ModelFamilyFormChain.Close(uv, loop.Source, check, problems, closed: true)) continue;
            var planar = ModelFamilyFormPlanarLoop.Create(loop.Source, uv, frame, check, problems);
            if (planar != null) loops.Add(planar);
        }
        if (problems.Count > before) return null;

        // Holes inside the outer loop; no two loops crossing.
        var outer = loops[0];
        for (var i = 1; i < loops.Count; i++)
        {
            var hole = loops[i];
            if (ModelFamilyFormGeometry.PolygonsIntersect(outer.Polygon, hole.Polygon, 1e-6))
                problems.Add($"{hole.Source}: crosses or touches the outer loop {outer.Source}; a hole must lie inside it with a gap.");
            else if (!hole.Polygon.All(p => ModelFamilyFormGeometry.Inside(p, outer.Polygon)))
                problems.Add(outer.Polygon.All(p => ModelFamilyFormGeometry.Inside(p, hole.Polygon))
                    ? $"{hole.Source}: encloses the first loop {outer.Source}; put the outer loop first."
                    : $"{hole.Source}: lies outside the outer loop {outer.Source}; a further loop is a hole and must be inside the first one. Make a separate form instead.");
            for (var j = 1; j < i; j++)
                if (ModelFamilyFormGeometry.PolygonsIntersect(loops[j].Polygon, hole.Polygon, 1e-6))
                    problems.Add($"{hole.Source}: crosses or touches {loops[j].Source}; loops must not intersect.");
        }
        return problems.Count > before ? null : new ModelFamilyFormPlanarProfile(frame, loops);
    }
}

/// <summary>Closure and chaining of a list of 2D segments: gaps within the tolerance are snapped, larger gaps are problems.</summary>
internal static class ModelFamilyFormChain
{
    public static bool Close(List<ModelFamilyFormSegment> segments, string source, ModelFamilyFormCheck check, List<string> problems, bool closed)
    {
        var ok = true;
        var count = closed ? segments.Count : segments.Count - 1;
        for (var i = 0; i < count; i++)
        {
            var next = (i + 1) % segments.Count;
            var gap = Distance2D(segments[i].End, segments[next].Start);
            if (gap <= check.ClosureMm) segments[next].Start = Copy(segments[i].End);
            else
            {
                ok = false;
                problems.Add(next == 0
                    ? $"{source}: the loop does not close: the last segment ends at {Format(segments[i].End)} (plane coordinates), {Mm(gap)} mm from where the first starts {Format(segments[0].Start)}. A loop must end where it starts."
                    : $"{source}: segment {i} ends at {Format(segments[i].End)} but segment {next} starts at {Format(segments[next].Start)} (plane coordinates, gap {Mm(gap)} mm); segments must chain end to start (tolerance {Mm(check.ClosureMm)} mm).");
            }
        }
        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            if (s.Kind == ModelFamilyFormSegmentKind.Arc && ModelFamilyFormGeometry.Arc2D(s.Start, s.Mid!, s.End) == null)
            {
                ok = false;
                problems.Add($"{source}: segment {i} is an arc whose three points are on one line, or whose start and end coincide; use a line, or a circle loop for a full circle.");
                continue;
            }
            var length = ModelFamilyFormGeometry.Length2D(s);
            if (length < check.MinSegmentMm)
            {
                ok = false;
                problems.Add($"{source}: segment {i} is {Mm(length)} mm long on the work plane; Revit refuses curves shorter than {Mm(check.MinSegmentMm)} mm.");
            }
        }
        return ok;
    }
}

/// <summary>A checked loop on a plane: its segments in plane and family coordinates, polygon, signed area and bounds.</summary>
public sealed class ModelFamilyFormPlanarLoop
{
    public string Source { get; private set; } = string.Empty;
    /// <summary>Segments in plane coordinates <c>[u, v, 0]</c>, closed.</summary>
    public List<ModelFamilyFormSegment> Uv { get; private set; } = new();
    /// <summary>The same segments in family coordinates, exactly on the plane: what Revit's curves are made from.</summary>
    public List<ModelFamilyFormSegment> World { get; private set; } = new();
    /// <summary>The loop as a polygon in plane coordinates (arcs discretized), for containment checks.</summary>
    public List<double[]> Polygon { get; private set; } = new();
    /// <summary>Signed area in mm² (positive counter-clockwise in plane coordinates).</summary>
    public double Area { get; private set; }
    public double[] Min { get; private set; } = Vec(0, 0);
    public double[] Max { get; private set; } = Vec(0, 0);

    internal static ModelFamilyFormPlanarLoop? Create(string source, List<ModelFamilyFormSegment> uv, ModelFamilyFormFrame frame,
        ModelFamilyFormCheck check, List<string> problems)
    {
        var polygon = ModelFamilyFormGeometry.Polygon(uv);
        var area = ModelFamilyFormGeometry.SignedArea(uv);
        if (Math.Abs(area) < check.MinAreaMm2)
        {
            problems.Add($"{source}: the loop encloses no area ({Mm(Math.Abs(area))} mm²); its points are on one line or repeat.");
            return null;
        }
        if (ModelFamilyFormGeometry.SelfIntersection(polygon, 1e-6) is { } crossing)
        {
            problems.Add($"{source}: the loop crosses itself (near {Format(polygon[crossing.First])} and {Format(polygon[crossing.Second])} in plane coordinates); order the points around the outline.");
            return null;
        }
        var (min, max) = ModelFamilyFormGeometry.Bounds(uv);
        return new ModelFamilyFormPlanarLoop
        {
            Source = source,
            Uv = uv,
            World = uv.Select(s => s.Map(p => frame.ToWorld(p))).ToList(),
            Polygon = polygon,
            Area = area,
            Min = min,
            Max = max
        };
    }

    public JObject Describe() => new()
    {
        ["source"] = Source,
        ["segments"] = Uv.Count,
        ["width"] = Math.Round(Max[0] - Min[0], 3),
        ["height"] = Math.Round(Max[1] - Min[1], 3),
        ["area"] = Math.Round(Math.Abs(Area), 3)
    };
}

/// <summary>A checked profile on a plane: the outer loop first, then holes.</summary>
public sealed class ModelFamilyFormPlanarProfile
{
    public ModelFamilyFormPlanarProfile(ModelFamilyFormFrame frame, List<ModelFamilyFormPlanarLoop> loops)
    {
        Frame = frame;
        Loops = loops;
    }

    public ModelFamilyFormFrame Frame { get; }
    public List<ModelFamilyFormPlanarLoop> Loops { get; }
    public ModelFamilyFormPlanarLoop Outer => Loops[0];
    /// <summary>Plane-coordinate bounds of the outer loop, mm.</summary>
    public double[] Min => Outer.Min;
    public double[] Max => Outer.Max;
    /// <summary>Extent along the plane's x axis, mm.</summary>
    public double Width => Outer.Max[0] - Outer.Min[0];
    /// <summary>Extent along the plane's y axis, mm.</summary>
    public double Height => Outer.Max[1] - Outer.Min[1];
    /// <summary>Net area, mm²: the outer loop minus its holes.</summary>
    public double Area => Math.Abs(Outer.Area) - Loops.Skip(1).Sum(l => Math.Abs(l.Area));

    /// <summary>
    /// Whether the profile lies on both sides of the line through plane points <paramref name="axisStart"/> and
    /// <paramref name="axisEnd"/> (<c>[u, v]</c>). A revolve's profile may touch its axis but not cross it.
    /// </summary>
    public bool CrossesLine(double[] axisStart, double[] axisEnd, double tolerance = 1e-3)
    {
        var direction = ModelFamilyFormGeometry.Normalize(ModelFamilyFormGeometry.Vec(axisEnd[0] - axisStart[0], axisEnd[1] - axisStart[1]));
        double Side(double[] p) => direction[0] * (p[1] - axisStart[1]) - direction[1] * (p[0] - axisStart[0]);
        var sides = Loops.SelectMany(l => l.Polygon).Select(Side).ToList();
        return sides.Any(s => s > tolerance) && sides.Any(s => s < -tolerance);
    }

    /// <summary>What the preview shows: measured width, height and area, and the loops.</summary>
    public JObject Describe() => new()
    {
        ["width"] = Math.Round(Width, 3),
        ["height"] = Math.Round(Height, 3),
        ["area"] = Math.Round(Area, 3),
        ["loops"] = Loops.Count,
        ["holes"] = Loops.Count - 1
    };
}

/// <summary>
/// A sweep path: chained line and arc segments on the work plane, open or closed. Accepts
/// <c>{"segments": [...]}</c>, a plain list of segments, or one segment object.
/// </summary>
public sealed class ModelFamilyFormPath
{
    public string Source { get; init; } = string.Empty;
    public List<ModelFamilyFormSegment> Segments { get; init; } = new();

    public static ModelFamilyFormPath? Parse(JToken? token, string source, List<string> problems)
    {
        if (token == null)
        {
            problems.Add($"{source} is required: segments [{{\"line\": {{start, end}}}} | {{\"arc\": {{start, mid, end}}}}, …] on the work plane.");
            return null;
        }
        List<ModelFamilyFormSegment>? segments;
        if (token is JObject obj && ModelFamilyJson.Has(obj, "segments"))
        {
            ModelFamilyJson.CheckKeys(obj, source, problems, "segments");
            segments = ModelFamilyFormLoop.ParseSegments(ModelFamilyJson.Get(obj, "segments"), $"{source}.segments", problems);
        }
        else if (token is JObject single)
        {
            var one = ModelFamilyFormSegment.Parse(single, source, problems);
            segments = one == null ? null : new List<ModelFamilyFormSegment> { one };
        }
        else segments = ModelFamilyFormLoop.ParseSegments(token, source, problems);
        return segments == null ? null : new ModelFamilyFormPath { Source = source, Segments = segments };
    }

    /// <summary>The path on a plane: projected (points off the plane by more than the tolerance are problems), chained, lengths checked.</summary>
    public ModelFamilyFormPlanarPath? OnPlane(ModelFamilyFormFrame frame, ModelFamilyFormCheck check, List<string> problems, bool singleCurve = false)
    {
        var before = problems.Count;
        if (singleCurve && Segments.Count != 1)
        {
            problems.Add($"{Source}: must be one curve (a line or an arc), got {Segments.Count}.");
            return null;
        }
        for (var i = 0; i < Segments.Count; i++)
        {
            var s = Segments[i];
            foreach (var (point, name) in new[] { (s.Start, "start"), (s.Mid, "mid"), (s.End, "end") })
            {
                if (point == null) continue;
                var offset = frame.OffsetOf(point);
                if (Math.Abs(offset) > check.OffPlaneMm)
                    problems.Add($"{Source}[{i}].{name}: {Format(point)} is {Mm(Math.Abs(offset))} mm off the work plane (more than {Mm(check.OffPlaneMm)} mm); a path lies on its workPlane.");
            }
        }
        if (problems.Count > before) return null;
        var uv = Segments.Select(s => s.Map(frame.ToUv)).ToList();
        var closed = uv.Count > 1 && Distance2D(uv[uv.Count - 1].End, uv[0].Start) <= check.ClosureMm;
        if (!ModelFamilyFormChain.Close(uv, Source, check, problems, closed)) return null;
        return new ModelFamilyFormPlanarPath
        {
            Source = Source,
            Uv = uv,
            World = uv.Select(s => s.Map(p => frame.ToWorld(p))).ToList(),
            Closed = closed,
            Length = uv.Sum(ModelFamilyFormGeometry.Length2D)
        };
    }
}

/// <summary>A checked path on a plane.</summary>
public sealed class ModelFamilyFormPlanarPath
{
    public string Source { get; init; } = string.Empty;
    public List<ModelFamilyFormSegment> Uv { get; init; } = new();
    public List<ModelFamilyFormSegment> World { get; init; } = new();
    public bool Closed { get; init; }
    /// <summary>Total length, mm.</summary>
    public double Length { get; init; }

    public JObject Describe() => new()
    {
        ["segments"] = Uv.Count,
        ["closed"] = Closed,
        ["length"] = Math.Round(Length, 3)
    };
}
