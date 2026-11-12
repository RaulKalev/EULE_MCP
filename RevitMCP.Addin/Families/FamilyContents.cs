using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Parameters;

namespace RevitMCP.Addin.Families;

/// <summary>
/// What revit_get_family_contents read from a family document: millimetres on the family's reference level and
/// degrees, values already converted from Revit's internal units. Pure: no Revit types, unit tested.
/// </summary>
public sealed class FamilyContentsSnapshot
{
    public string FamilyName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool IsDetailItem { get; set; }
    public bool IsGenericAnnotation { get; set; }
    /// <summary>The family's current type; empty when it has none.</summary>
    public string CurrentType { get; set; } = string.Empty;
    public List<FamilyContentsParameter> Parameters { get; } = new();
    public List<FamilyContentsType> Types { get; } = new();
    public List<FamilyContentsCurve> Curves { get; } = new();
    public List<FamilyContentsRegion> FilledRegions { get; } = new();
    public List<FamilyContentsText> TextNotes { get; } = new();
    public List<FamilyContentsNested> NestedInstances { get; } = new();
    /// <summary>Nested Generic Annotation families that were opened to read their text.</summary>
    public List<FamilyContentsNestedFamily> NestedFamilies { get; } = new();
    public List<FamilyContentsLineStyle> LineStyles { get; } = new();
    public List<FamilyContentsRegionType> FilledRegionTypes { get; } = new();
}

public sealed class FamilyContentsParameter
{
    public string Name { get; set; } = string.Empty;
    /// <summary>The friendly data type (text, length, yesNo, …) when known, else Revit's label of it.</summary>
    public string DataType { get; set; } = string.Empty;
    /// <summary>The friendly data type of the parameter tools, or null when the map does not know it.</summary>
    public string? Friendly { get; set; }
    /// <summary>Revit's label of the parameter group ("Other" when none).</summary>
    public string Group { get; set; } = string.Empty;
    public bool IsInstance { get; set; }
    public bool IsShared { get; set; }
    public string Guid { get; set; } = string.Empty;
    public string Formula { get; set; } = string.Empty;
    public bool IsReporting { get; set; }
    /// <summary>A built-in parameter of the family (negative id): never part of a spec.</summary>
    public bool IsBuiltIn { get; set; }
}

public sealed class FamilyContentsType
{
    public string Name { get; set; } = string.Empty;
    /// <summary>Parameter name → value (string, long, double, bool or null), lengths mm, angles degrees.</summary>
    public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);
}

/// <summary>A detail line, arc, circle or other curve of the family.</summary>
public sealed class FamilyContentsCurve
{
    public long Id { get; set; }
    /// <summary>line | arc | circle | other.</summary>
    public string Kind { get; set; } = "line";
    /// <summary>Revit's curve class for kind other (Ellipse, NurbSpline, …).</summary>
    public string CurveType { get; set; } = string.Empty;
    public DetailPoint Start { get; set; }
    public DetailPoint End { get; set; }
    public DetailPoint? Mid { get; set; }
    public DetailPoint Center { get; set; }
    public double Radius { get; set; }
    /// <summary>Degrees counter-clockwise from +X; the arc runs counter-clockwise from StartAngle to EndAngle.</summary>
    public double StartAngle { get; set; }
    public double EndAngle { get; set; }
    /// <summary>The line style (subcategory) name; empty = the category's own line.</summary>
    public string LineStyle { get; set; } = string.Empty;
    public string VisibleParameter { get; set; } = string.Empty;
}

/// <summary>One edge of a filled region boundary.</summary>
public sealed class FamilyContentsSegment
{
    /// <summary>line | arc | other.</summary>
    public string Kind { get; set; } = "line";
    public string CurveType { get; set; } = string.Empty;
    public DetailPoint Start { get; set; }
    public DetailPoint End { get; set; }
    public DetailPoint? Mid { get; set; }
    public DetailPoint Center { get; set; }
    public double Radius { get; set; }
}

public sealed class FamilyContentsLoop
{
    public List<FamilyContentsSegment> Segments { get; } = new();
}

public sealed class FamilyContentsRegion
{
    public long Id { get; set; }
    /// <summary>The boundaries as Revit returns them; the outer one is found by area.</summary>
    public List<FamilyContentsLoop> Loops { get; } = new();
    public string Type { get; set; } = string.Empty;
    /// <summary>The boundary's line style; empty when it could not be read.</summary>
    public string LineStyle { get; set; } = string.Empty;
    /// <summary>True when the boundary's edges use more than one line style (LineStyle is the first).</summary>
    public bool MixedLineStyles { get; set; }
    public string VisibleParameter { get; set; } = string.Empty;
}

public sealed class FamilyContentsText
{
    public long Id { get; set; }
    public string Text { get; set; } = string.Empty;
    public DetailPoint Position { get; set; }
    public string TextType { get; set; } = string.Empty;
    /// <summary>Text height on paper in mm; null when the type has none.</summary>
    public double? SizeMm { get; set; }
    public double RotationDeg { get; set; }
    /// <summary>left | center | right.</summary>
    public string Alignment { get; set; } = "left";
    public string VisibleParameter { get; set; } = string.Empty;
}

/// <summary>An element parameter of a nested instance (or its type) linked to a parameter of the family.</summary>
public sealed class FamilyContentsLink
{
    public string Parameter { get; set; } = string.Empty;
    public string FamilyParameter { get; set; } = string.Empty;
    /// <summary>instance | type.</summary>
    public string Scope { get; set; } = "instance";
}

public sealed class FamilyContentsNested
{
    public long Id { get; set; }
    public string Family { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool IsGenericAnnotation { get; set; }
    public DetailPoint Position { get; set; }
    public double RotationDeg { get; set; }
    public bool Mirrored { get; set; }
    public List<FamilyContentsLink> LinkedParameters { get; } = new();
    public string VisibleParameter { get; set; } = string.Empty;
}

/// <summary>A nested Generic Annotation family opened to read its text.</summary>
public sealed class FamilyContentsNestedFamily
{
    public string Name { get; set; } = string.Empty;
    public bool Opened { get; set; }
    /// <summary>Why it was not opened; empty when it was.</summary>
    public string Reason { get; set; } = string.Empty;
    public List<FamilyContentsText> Texts { get; } = new();
    /// <summary>Detail curves, filled regions and nested families it holds besides text (labels are not counted: the API cannot read them).</summary>
    public int OtherElements { get; set; }
}

public sealed class FamilyContentsLineStyle
{
    public string Name { get; set; } = string.Empty;
    /// <summary>The category the style belongs to: Lines, or the family's own category (Detail Items).</summary>
    public string Parent { get; set; } = string.Empty;
    public int? Weight { get; set; }
    /// <summary>#RRGGBB; empty when not set.</summary>
    public string Color { get; set; } = string.Empty;
    /// <summary>The line pattern name; empty = solid.</summary>
    public string Pattern { get; set; } = string.Empty;
    /// <summary>One of Revit's own styles (negative id), such as &lt;Thin Lines&gt;.</summary>
    public bool BuiltIn { get; set; }
}

public sealed class FamilyContentsRegionType
{
    public string Name { get; set; } = string.Empty;
    /// <summary>A fill pattern name, "solid" or "none".</summary>
    public string ForegroundPattern { get; set; } = "none";
    public string ForegroundColor { get; set; } = string.Empty;
    public string BackgroundPattern { get; set; } = "none";
    public string BackgroundColor { get; set; } = string.Empty;
    public int? LineWeight { get; set; }
    public bool IsMasking { get; set; }
}

/// <summary>Geometry and unit helpers of revit_get_family_contents. Pure.</summary>
public static class FamilyContentsGeometry
{
    public const double MmPerFoot = 304.8;
    private const double SquareFeetPerSquareMetre = 10.763910416709722;
    private const double CubicFeetPerCubicMetre = 35.31466672148859;

    /// <summary>Millimetres and degrees are reported to 1/10 000.</summary>
    public static double Round(double value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    /// <summary>
    /// A value in Revit's internal units as the create tool takes it: lengths in mm, areas in m², volumes in m³ and
    /// angles in degrees. Other types are returned as they are.
    /// </summary>
    public static double FromInternal(string? friendly, double value) => friendly switch
    {
        "length" => value * MmPerFoot,
        "area" => value / SquareFeetPerSquareMetre,
        "volume" => value / CubicFeetPerCubicMetre,
        "angle" => value * 180 / Math.PI,
        _ => value
    };

    /// <summary>Degrees in [0, 360) of the direction from <paramref name="center"/> to <paramref name="point"/>.</summary>
    public static double AngleOf(DetailPoint center, DetailPoint point)
    {
        var degrees = Math.Atan2(point.Y - center.Y, point.X - center.X) * 180 / Math.PI;
        if (degrees < 0) degrees += 360;
        return Round(degrees) >= 360 ? 0 : degrees;
    }

    /// <summary>
    /// Start and end angle of an arc, counter-clockwise: a clockwise arc (normal pointing down) runs from its end to
    /// its start when read counter-clockwise.
    /// </summary>
    public static (double Start, double End) ArcAngles(DetailPoint center, DetailPoint start, DetailPoint end, bool clockwise)
    {
        var a = AngleOf(center, start);
        var b = AngleOf(center, end);
        return clockwise ? (b, a) : (a, b);
    }

    /// <summary>A loop of arcs that all share one centre and radius: a closed loop like that is a full circle.</summary>
    public static bool TryCircle(FamilyContentsLoop loop, out DetailPoint center, out double radius)
    {
        center = default;
        radius = 0;
        if (loop.Segments.Count == 0 || loop.Segments.Any(s => s.Kind != "arc")) return false;
        var first = loop.Segments[0];
        if (loop.Segments.Any(s => s.Center.DistanceTo(first.Center) > 1e-3 || Math.Abs(s.Radius - first.Radius) > 1e-3)) return false;
        center = first.Center;
        radius = first.Radius;
        return radius > 0;
    }

    /// <summary>The corners of a loop whose edges are all straight; null when it has an arc or another curve.</summary>
    public static List<DetailPoint>? Polygon(FamilyContentsLoop loop) =>
        loop.Segments.Count == 0 || loop.Segments.Any(s => s.Kind != "line") ? null : loop.Segments.Select(s => s.Start).ToList();

    /// <summary>The loop's outline for area: corners, plus the mid point of each arc.</summary>
    private static List<DetailPoint> Outline(FamilyContentsLoop loop)
    {
        var points = new List<DetailPoint>();
        foreach (var segment in loop.Segments)
        {
            points.Add(segment.Start);
            if (segment.Mid != null) points.Add(segment.Mid.Value);
        }
        return points;
    }

    /// <summary>The index of the outer boundary: the loop that encloses the largest area.</summary>
    public static int OuterLoop(IReadOnlyList<FamilyContentsLoop> loops)
    {
        var best = 0;
        var bestArea = -1.0;
        for (var i = 0; i < loops.Count; i++)
        {
            var area = TryCircle(loops[i], out _, out var r) ? Math.PI * r * r : Math.Abs(DetailFamilySpecParser.SignedArea(Outline(loops[i])));
            if (area > bestArea) { best = i; bestArea = area; }
        }
        return best;
    }

    /// <summary>The outer boundary first, then the others in Revit's order.</summary>
    public static List<FamilyContentsLoop> OuterFirst(IReadOnlyList<FamilyContentsLoop> loops)
    {
        if (loops.Count == 0) return new List<FamilyContentsLoop>();
        var outer = OuterLoop(loops);
        return new[] { loops[outer] }.Concat(loops.Where((_, i) => i != outer)).ToList();
    }

    /// <summary>Turns <paramref name="point"/> by <paramref name="degrees"/> around the origin and moves it by <paramref name="offset"/>.</summary>
    public static DetailPoint Place(DetailPoint point, DetailPoint offset, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        return new DetailPoint(offset.X + point.X * cos - point.Y * sin, offset.Y + point.X * sin + point.Y * cos);
    }

    /// <summary>Degrees normalised to (-180, 180].</summary>
    public static double NormalizeRotation(double degrees)
    {
        var d = degrees % 360;
        if (d <= -180) d += 360;
        if (d > 180) d -= 360;
        return Math.Abs(d) < 1e-9 ? 0 : d;
    }
}

/// <summary>Turns a <see cref="FamilyContentsSnapshot"/> into the result of revit_get_family_contents. Pure.</summary>
public static class FamilyContentsView
{
    public const int DefaultLimit = 500;
    public const int MaxLimit = 5000;

    /// <summary>The sections a caller can pick with include.</summary>
    public static readonly IReadOnlyList<string> Sections = new[]
    {
        "parameters", "types", "curves", "filledRegions", "textNotes", "nestedInstances", "lineStyles", "filledRegionTypes"
    };

    /// <summary>The sections to return: every one when include is empty; "all" means every one too.</summary>
    public static HashSet<string> ParseInclude(IEnumerable<string> include, List<string> errors)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var given = include.SelectMany(i => (i ?? string.Empty).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            .Select(i => i.Trim()).Where(i => i.Length > 0).ToList();
        if (given.Count == 0 || given.Any(i => i.Equals("all", StringComparison.OrdinalIgnoreCase)))
        {
            result.UnionWith(Sections);
            return result;
        }
        foreach (var item in given)
        {
            var section = Sections.FirstOrDefault(s => s.Equals(item, StringComparison.OrdinalIgnoreCase)
                                                       || ParameterSpecMap.Normalize(s) == ParameterSpecMap.Normalize(item));
            if (section != null) result.Add(section);
            else errors.Add($"include '{item}' is not a section: {string.Join(", ", Sections)} (or all).");
        }
        return result;
    }

    public static int ClampLimit(int limit) => limit <= 0 ? DefaultLimit : Math.Min(limit, MaxLimit);

    /// <summary>The included sections, each cut to <paramref name="limit"/> items, plus counts and the truncated lists.</summary>
    public static JObject Describe(FamilyContentsSnapshot s, ISet<string> include, int limit)
    {
        var result = new JObject();
        var counts = new JObject();
        var truncated = new JArray();

        void Section<T>(string name, IReadOnlyList<T> items, Func<T, JToken> describe)
        {
            counts[name] = items.Count;
            if (!include.Contains(name)) return;
            result[name] = new JArray(items.Take(limit).Select(describe));
            if (items.Count > limit) truncated.Add(name);
        }

        Section("parameters", s.Parameters, DescribeParameter);
        Section("types", s.Types, t => DescribeType(t, s.Parameters));
        Section("curves", s.Curves, DescribeCurve);
        Section("filledRegions", s.FilledRegions, DescribeRegion);
        Section("textNotes", s.TextNotes, DescribeText);
        Section("nestedInstances", s.NestedInstances, n => DescribeNested(n, s.NestedFamilies));
        Section("lineStyles", s.LineStyles, DescribeLineStyle);
        Section("filledRegionTypes", s.FilledRegionTypes, DescribeRegionType);

        var head = new JObject { ["counts"] = counts };
        if (truncated.Count > 0)
        {
            head["truncated"] = truncated;
            head["limit"] = limit;
        }
        head.Merge(result);
        return head;
    }

    public static JArray Point(DetailPoint p) => new(FamilyContentsGeometry.Round(p.X), FamilyContentsGeometry.Round(p.Y));

    private static void Optional(JObject target, string key, string value)
    {
        if (value.Length > 0) target[key] = value;
    }

    public static JObject DescribeParameter(FamilyContentsParameter p)
    {
        var o = new JObject
        {
            ["name"] = p.Name,
            ["dataType"] = p.DataType,
            ["group"] = p.Group,
            ["instance"] = p.IsInstance
        };
        if (p.IsShared)
        {
            o["shared"] = true;
            Optional(o, "guid", p.Guid);
        }
        Optional(o, "formula", p.Formula);
        if (p.IsReporting) o["isReporting"] = true;
        if (p.IsBuiltIn) o["builtIn"] = true;
        return o;
    }

    public static JObject DescribeType(FamilyContentsType t, IReadOnlyList<FamilyContentsParameter> parameters)
    {
        var values = new JObject();
        foreach (var parameter in parameters)
        {
            if (!t.Values.TryGetValue(parameter.Name, out var value)) continue;
            values[parameter.Name] = value switch
            {
                null => JValue.CreateNull(),
                double d => FamilyContentsGeometry.Round(d),
                _ => JToken.FromObject(value)
            };
        }
        return new JObject { ["name"] = t.Name, ["values"] = values };
    }

    public static JObject DescribeCurve(FamilyContentsCurve c)
    {
        var o = new JObject { ["id"] = c.Id, ["kind"] = c.Kind };
        switch (c.Kind)
        {
            case "line":
                o["start"] = Point(c.Start);
                o["end"] = Point(c.End);
                break;
            case "arc":
                o["start"] = Point(c.Start);
                if (c.Mid != null) o["mid"] = Point(c.Mid.Value);
                o["end"] = Point(c.End);
                o["center"] = Point(c.Center);
                o["radius"] = FamilyContentsGeometry.Round(c.Radius);
                o["startAngle"] = FamilyContentsGeometry.Round(c.StartAngle);
                o["endAngle"] = FamilyContentsGeometry.Round(c.EndAngle);
                break;
            case "circle":
                o["center"] = Point(c.Center);
                o["radius"] = FamilyContentsGeometry.Round(c.Radius);
                break;
            default:
                Optional(o, "curveType", c.CurveType);
                o["start"] = Point(c.Start);
                o["end"] = Point(c.End);
                break;
        }
        Optional(o, "lineStyle", c.LineStyle);
        Optional(o, "visibleParameter", c.VisibleParameter);
        return o;
    }

    public static JObject DescribeLoop(FamilyContentsLoop loop)
    {
        if (FamilyContentsGeometry.TryCircle(loop, out var center, out var radius))
            return new JObject { ["center"] = Point(center), ["radius"] = FamilyContentsGeometry.Round(radius) };
        var polygon = FamilyContentsGeometry.Polygon(loop);
        if (polygon != null) return new JObject { ["points"] = new JArray(polygon.Select(Point)) };
        return new JObject
        {
            ["segments"] = new JArray(loop.Segments.Select(s =>
            {
                var o = new JObject { ["kind"] = s.Kind, ["start"] = Point(s.Start) };
                if (s.Mid != null) o["mid"] = Point(s.Mid.Value);
                o["end"] = Point(s.End);
                Optional(o, "curveType", s.CurveType);
                return o;
            }))
        };
    }

    public static JObject DescribeRegion(FamilyContentsRegion r)
    {
        var o = new JObject { ["id"] = r.Id };
        Optional(o, "type", r.Type);
        Optional(o, "lineStyle", r.LineStyle);
        if (r.MixedLineStyles) o["mixedLineStyles"] = true;
        Optional(o, "visibleParameter", r.VisibleParameter);
        // The outer boundary first, then the holes.
        o["loops"] = new JArray(FamilyContentsGeometry.OuterFirst(r.Loops).Select(DescribeLoop));
        return o;
    }

    public static JObject DescribeText(FamilyContentsText t)
    {
        var o = new JObject { ["id"] = t.Id, ["text"] = t.Text, ["position"] = Point(t.Position) };
        Optional(o, "textType", t.TextType);
        if (t.SizeMm != null) o["sizeMm"] = FamilyContentsGeometry.Round(t.SizeMm.Value);
        if (Math.Abs(t.RotationDeg) > 1e-9) o["rotation"] = FamilyContentsGeometry.Round(t.RotationDeg);
        o["alignment"] = t.Alignment;
        Optional(o, "visibleParameter", t.VisibleParameter);
        return o;
    }

    public static JObject DescribeNested(FamilyContentsNested n, IReadOnlyList<FamilyContentsNestedFamily> families)
    {
        var o = new JObject
        {
            ["id"] = n.Id,
            ["family"] = n.Family,
            ["type"] = n.Type,
            ["category"] = n.Category,
            ["position"] = Point(n.Position)
        };
        if (Math.Abs(n.RotationDeg) > 1e-9) o["rotation"] = FamilyContentsGeometry.Round(n.RotationDeg);
        if (n.Mirrored) o["mirrored"] = true;
        if (n.LinkedParameters.Count > 0)
            o["linkedParameters"] = new JArray(n.LinkedParameters.Select(l =>
            {
                var link = new JObject { ["parameter"] = l.Parameter, ["familyParameter"] = l.FamilyParameter };
                if (l.Scope != "instance") link["scope"] = l.Scope;
                return link;
            }));
        Optional(o, "visibleParameter", n.VisibleParameter);
        var family = families.FirstOrDefault(f => string.Equals(f.Name, n.Family, StringComparison.OrdinalIgnoreCase));
        if (family is { Opened: true, Texts.Count: > 0 })
            o["texts"] = new JArray(family.Texts.Select(t =>
            {
                var text = new JObject { ["text"] = t.Text, ["position"] = Point(t.Position), ["alignment"] = t.Alignment };
                if (t.SizeMm != null) text["sizeMm"] = FamilyContentsGeometry.Round(t.SizeMm.Value);
                return text;
            }));
        return o;
    }

    public static JObject DescribeLineStyle(FamilyContentsLineStyle l)
    {
        var o = new JObject { ["name"] = l.Name };
        Optional(o, "parent", l.Parent);
        if (l.Weight != null) o["weight"] = l.Weight;
        Optional(o, "color", l.Color);
        o["pattern"] = l.Pattern.Length > 0 ? l.Pattern : "Solid";
        if (l.BuiltIn) o["builtIn"] = true;
        return o;
    }

    public static JObject DescribeRegionType(FamilyContentsRegionType t)
    {
        var o = new JObject { ["name"] = t.Name, ["foregroundPattern"] = t.ForegroundPattern };
        Optional(o, "foregroundColor", t.ForegroundColor);
        o["backgroundPattern"] = t.BackgroundPattern;
        Optional(o, "backgroundColor", t.BackgroundColor);
        if (t.LineWeight != null) o["lineWeight"] = t.LineWeight;
        o["isMasking"] = t.IsMasking;
        return o;
    }

    /// <summary>Up to five names that look like <paramref name="requested"/>, for a "not found" message.</summary>
    public static List<string> Suggest(string requested, IEnumerable<string> names, int max = 5)
    {
        var needle = ParameterSpecMap.Normalize(requested);
        if (needle.Length == 0) return new List<string>();
        return names
            .Select(n => (Name: n, Norm: ParameterSpecMap.Normalize(n)))
            .Where(n => n.Norm.Length > 0 && (n.Norm.Contains(needle) || needle.Contains(n.Norm) || SharedPrefix(n.Norm, needle) >= Math.Min(4, needle.Length)))
            .OrderByDescending(n => SharedPrefix(n.Norm, needle))
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .Select(n => n.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
    }

    private static int SharedPrefix(string a, string b)
    {
        var i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        return i;
    }
}
