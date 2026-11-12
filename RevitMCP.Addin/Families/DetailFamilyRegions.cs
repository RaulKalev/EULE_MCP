using System.Globalization;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Families;

/// <summary>One closed boundary of a filled region in the family sketch: a polygon or a circle (mm, reference level).</summary>
public sealed class DetailRegionLoop
{
    /// <summary>Polygon corners in order, without repeating the first at the end. Empty for a circle.</summary>
    public List<DetailPoint> Points { get; } = new();
    public bool IsCircle { get; set; }
    public DetailPoint Center { get; set; }
    public double Radius { get; set; }
}

/// <summary>One filled region revit_create_detail_family draws in the family.</summary>
public sealed class DetailRegionSpec
{
    /// <summary>Where it came from in the request, for messages: filledRegions[0], …</summary>
    public string Source { get; set; } = string.Empty;
    /// <summary>The outer boundary first (counter-clockwise), then the holes (clockwise).</summary>
    public List<DetailRegionLoop> Loops { get; } = new();
    /// <summary>Filled region type name; empty = the family's default type.</summary>
    public string Type { get; set; } = string.Empty;
    /// <summary>Line style of the boundary; empty = the type's own.</summary>
    public string LineStyle { get; set; } = string.Empty;
    /// <summary>A yes/no family parameter that switches the region on and off; empty = always visible.</summary>
    public string VisibleParameter { get; set; } = string.Empty;
}

/// <summary>
/// A filled region type the family gets: created (or, when the template already has the name, edited). A null field
/// is taken from <see cref="CopyFrom"/> (a filled region type of the project), else left as the family's base type has it.
/// </summary>
public sealed class DetailRegionTypeSpec
{
    public string Source { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>A filled region type of the project to start from; empty = the project's type of the same name, if any.</summary>
    public string CopyFrom { get; set; } = string.Empty;
    /// <summary>A fill pattern name of the project, "solid" or "none"; null = not given.</summary>
    public string? ForegroundPattern { get; set; }
    public string? BackgroundPattern { get; set; }
    /// <summary>#RRGGBB; null = not given.</summary>
    public string? ForegroundColor { get; set; }
    public string? BackgroundColor { get; set; }
    public int? LineWeight { get; set; }
    public bool? IsMasking { get; set; }

    public bool HasOverrides =>
        ForegroundPattern != null || BackgroundPattern != null || ForegroundColor != null || BackgroundColor != null ||
        LineWeight != null || IsMasking != null;
}

/// <summary>
/// Filled regions, their types and the parameter references of revit_create_detail_family. Pure: no Revit types, unit tested.
/// </summary>
public static partial class DetailFamilySpecParser
{
    public const int MaxFilledRegions = 200;
    public const int MaxFilledRegionTypes = 50;
    public const int MaxRegionPoints = 500;
    public const int MaxRegionHoles = 50;
    /// <summary>Smallest region area in mm²; anything below is a degenerate boundary.</summary>
    public const double MinRegionAreaMm2 = 0.01;

    private static void ReadFilledRegionTypes(IReadOnlyDictionary<string, object?> args, string key, DetailFamilySpec spec, List<string> errors)
    {
        var index = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Items(args, key, errors))
        {
            var source = $"{key}[{index++}]";
            if (item == null) continue;
            var type = new DetailRegionTypeSpec
            {
                Source = source,
                Name = Text(item["name"]),
                CopyFrom = Text(item["copyFrom"] ?? item["duplicateFrom"]),
                ForegroundPattern = OptionalText(item["foregroundPattern"]),
                BackgroundPattern = OptionalText(item["backgroundPattern"]),
                IsMasking = Flag(item["isMasking"] ?? item["masking"])
            };
            if (type.Name.Length == 0) { errors.Add($"{source} needs a name."); continue; }
            var nameError = CheckFamilyName(type.Name);
            if (nameError != null) { errors.Add($"{source}: {nameError.Replace("family name", "type name")}"); continue; }
            if (!seen.Add(type.Name)) { errors.Add($"{source}: the filled region type '{type.Name}' is listed twice."); continue; }

            var ok = true;
            type.ForegroundColor = Colour(item["foregroundColor"] ?? item["foregroundColour"], source + ".foregroundColor", errors, ref ok);
            type.BackgroundColor = Colour(item["backgroundColor"] ?? item["backgroundColour"], source + ".backgroundColor", errors, ref ok);
            var weight = item["lineWeight"];
            if (weight != null && weight.Type != JTokenType.Null)
            {
                var number = Num(weight);
                if (number is >= 1 and <= 16 && Math.Abs(number.Value - Math.Round(number.Value)) < 1e-9) type.LineWeight = (int)Math.Round(number.Value);
                else { errors.Add($"{source}.lineWeight must be a whole number from 1 to 16."); ok = false; }
            }
            if (ok) spec.FilledRegionTypes.Add(type);
        }
        if (spec.FilledRegionTypes.Count > MaxFilledRegionTypes)
            errors.Add($"{spec.FilledRegionTypes.Count} filled region types were requested; at most {MaxFilledRegionTypes} per family.");
    }

    private static void ReadFilledRegions(IReadOnlyDictionary<string, object?> args, string key, DetailFamilySpec spec, string defaultStyle, List<string> errors)
    {
        var index = 0;
        foreach (var item in Items(args, key, errors))
        {
            var source = $"{key}[{index++}]";
            if (item == null) continue;
            var before = errors.Count;
            var outer = ReadLoop(item, source, errors);
            if (outer == null)
            {
                if (errors.Count == before)
                    errors.Add($"{source} needs a boundary: points [[x,y], …] (three or more), min and max (a rectangle), or center and radius (a circle).");
                continue;
            }

            var region = new DetailRegionSpec
            {
                Source = source,
                Type = Text(item["type"] ?? item["filledRegionType"]),
                // A region's boundary keeps the type's own line unless it names a style itself: the default lineStyle is for curves.
                LineStyle = Text(item["lineStyle"]),
                VisibleParameter = Visible(item)
            };
            region.Loops.Add(outer);

            var failed = false;
            if (item["holes"] is JArray holes)
            {
                var holeIndex = 0;
                foreach (var holeToken in holes)
                {
                    var holeSource = $"{source}.holes[{holeIndex++}]";
                    // A hole is a point list, or an object like the region itself ({points} | {min, max} | {center, radius}).
                    var holeObject = holeToken as JObject ?? (holeToken is JArray ? new JObject { ["points"] = holeToken } : null);
                    var holeBefore = errors.Count;
                    var hole = holeObject == null ? null : ReadLoop(holeObject, holeSource, errors);
                    if (hole == null)
                    {
                        if (errors.Count == holeBefore) errors.Add($"{holeSource} must be a point list, or {{min, max}} or {{center, radius}}.");
                        failed = true;
                        continue;
                    }
                    if (!IsInside(hole, outer))
                    {
                        errors.Add($"{holeSource} is not inside the region's outer boundary.");
                        failed = true;
                        continue;
                    }
                    if (region.Loops.Skip(1).Any(other => Overlap(hole, other)))
                    {
                        errors.Add($"{holeSource} overlaps another hole.");
                        failed = true;
                        continue;
                    }
                    Orient(hole, counterClockwise: false);
                    region.Loops.Add(hole);
                }
                if (holes.Count > MaxRegionHoles)
                {
                    errors.Add($"{source} has {holes.Count} holes; at most {MaxRegionHoles}.");
                    failed = true;
                }
            }
            else if (item["holes"] is { Type: not JTokenType.Null })
            {
                errors.Add($"{source}.holes must be an array.");
                failed = true;
            }
            if (failed) continue;

            Orient(outer, counterClockwise: true);
            spec.FilledRegions.Add(region);
        }
        if (spec.FilledRegions.Count > MaxFilledRegions)
            errors.Add($"{spec.FilledRegions.Count} filled regions were requested; at most {MaxFilledRegions} per family.");
    }

    /// <summary>A loop from {points} | {min, max} | {center, radius}; null (with errors, or none when no shape was given).</summary>
    private static DetailRegionLoop? ReadLoop(JObject item, string source, List<string> errors)
    {
        if (item["points"] is { Type: not JTokenType.Null } pointsToken)
        {
            if (pointsToken is not JArray array) { errors.Add($"{source}.points must be an array of points."); return null; }
            var loop = new DetailRegionLoop();
            var i = 0;
            foreach (var token in array)
            {
                var point = Point(token, $"{source}.points[{i++}]", errors);
                if (point == null) return null;
                loop.Points.Add(point.Value);
            }
            // A closing point that repeats the first is dropped: the loop closes by itself.
            if (loop.Points.Count > 1 && loop.Points[0].DistanceTo(loop.Points[loop.Points.Count - 1]) < 1e-9)
                loop.Points.RemoveAt(loop.Points.Count - 1);
            var problem = PolygonProblem(loop.Points);
            if (problem != null) { errors.Add($"{source}: {problem}"); return null; }
            return loop;
        }

        if (item["min"] != null || item["max"] != null)
        {
            var a = Point(item["min"], source + ".min", errors);
            var b = Point(item["max"], source + ".max", errors);
            if (a == null || b == null) return null;
            double x0 = Math.Min(a.Value.X, b.Value.X), x1 = Math.Max(a.Value.X, b.Value.X);
            double y0 = Math.Min(a.Value.Y, b.Value.Y), y1 = Math.Max(a.Value.Y, b.Value.Y);
            if (x1 - x0 < MinCurveLengthMm || y1 - y0 < MinCurveLengthMm)
            {
                errors.Add($"{source} has a side shorter than Revit's minimum curve length ({MinCurveLengthMm} mm).");
                return null;
            }
            var loop = new DetailRegionLoop();
            loop.Points.AddRange(new[] { new DetailPoint(x0, y0), new DetailPoint(x1, y0), new DetailPoint(x1, y1), new DetailPoint(x0, y1) });
            return loop;
        }

        if (item["center"] != null || item["radius"] != null)
        {
            var center = Point(item["center"], source + ".center", errors);
            if (center == null) return null;
            var radius = Num(item["radius"]);
            if (radius == null || radius <= 0) { errors.Add($"{source}.radius must be a positive number (mm)."); return null; }
            if (radius.Value * Math.PI < MinCurveLengthMm) { errors.Add($"{source} is smaller than Revit's minimum curve length."); return null; }
            return new DetailRegionLoop { IsCircle = true, Center = center.Value, Radius = radius.Value };
        }
        return null;
    }

    /// <summary>Why a polygon cannot bound a region, or null: too few corners, a short side, no area, or sides that cross.</summary>
    public static string? PolygonProblem(IReadOnlyList<DetailPoint> points)
    {
        if (points.Count < 3) return "a boundary needs at least three points.";
        if (points.Count > MaxRegionPoints) return $"a boundary has {points.Count} points; at most {MaxRegionPoints}.";
        for (var i = 0; i < points.Count; i++)
        {
            if (points[i].DistanceTo(points[(i + 1) % points.Count]) < MinCurveLengthMm)
                return $"the side from point {i} to point {(i + 1) % points.Count} is shorter than Revit's minimum curve length ({MinCurveLengthMm} mm).";
        }
        var n = points.Count;
        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                // Neighbouring sides share a corner; that is not a crossing.
                if (j == i + 1 || (i == 0 && j == n - 1)) continue;
                if (SegmentsMeet(points[i], points[(i + 1) % n], points[j], points[(j + 1) % n]))
                    return $"the sides starting at points {i} and {j} cross or touch: a boundary must not intersect itself.";
            }
        }
        if (Math.Abs(SignedArea(points)) < MinRegionAreaMm2) return "the boundary encloses no area (its points are on one line).";
        return null;
    }

    /// <summary>Twice-signed-area halved: positive when the points run counter-clockwise.</summary>
    public static double SignedArea(IReadOnlyList<DetailPoint> points)
    {
        double sum = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            sum += a.X * b.Y - b.X * a.Y;
        }
        return sum / 2;
    }

    private static void Orient(DetailRegionLoop loop, bool counterClockwise)
    {
        if (loop.IsCircle) return;
        if ((SignedArea(loop.Points) > 0) != counterClockwise) loop.Points.Reverse();
    }

    private static bool SegmentsMeet(DetailPoint a, DetailPoint b, DetailPoint c, DetailPoint d)
    {
        static double Cross(DetailPoint o, DetailPoint p, DetailPoint q) => (p.X - o.X) * (q.Y - o.Y) - (p.Y - o.Y) * (q.X - o.X);
        static bool OnSegment(DetailPoint p, DetailPoint q, DetailPoint r) =>
            Math.Min(p.X, q.X) - 1e-9 <= r.X && r.X <= Math.Max(p.X, q.X) + 1e-9 &&
            Math.Min(p.Y, q.Y) - 1e-9 <= r.Y && r.Y <= Math.Max(p.Y, q.Y) + 1e-9;

        double d1 = Cross(c, d, a), d2 = Cross(c, d, b), d3 = Cross(a, b, c), d4 = Cross(a, b, d);
        if (((d1 > 1e-9 && d2 < -1e-9) || (d1 < -1e-9 && d2 > 1e-9)) && ((d3 > 1e-9 && d4 < -1e-9) || (d3 < -1e-9 && d4 > 1e-9))) return true;
        if (Math.Abs(d1) <= 1e-9 && OnSegment(c, d, a)) return true;
        if (Math.Abs(d2) <= 1e-9 && OnSegment(c, d, b)) return true;
        if (Math.Abs(d3) <= 1e-9 && OnSegment(a, b, c)) return true;
        if (Math.Abs(d4) <= 1e-9 && OnSegment(a, b, d)) return true;
        return false;
    }

    /// <summary>The loop as a polygon: its own corners, or 32 points around a circle.</summary>
    private static List<DetailPoint> Outline(DetailRegionLoop loop)
    {
        if (!loop.IsCircle) return loop.Points;
        var points = new List<DetailPoint>(32);
        for (var i = 0; i < 32; i++)
        {
            var angle = 2 * Math.PI * i / 32;
            points.Add(new DetailPoint(loop.Center.X + loop.Radius * Math.Cos(angle), loop.Center.Y + loop.Radius * Math.Sin(angle)));
        }
        return points;
    }

    private static bool Contains(DetailRegionLoop loop, DetailPoint p)
    {
        if (loop.IsCircle) return loop.Center.DistanceTo(p) < loop.Radius - 1e-9;
        var inside = false;
        var points = loop.Points;
        for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
        {
            if ((points[i].Y > p.Y) != (points[j].Y > p.Y) &&
                p.X < (points[j].X - points[i].X) * (p.Y - points[i].Y) / (points[j].Y - points[i].Y) + points[i].X)
                inside = !inside;
        }
        return inside;
    }

    private static bool EdgesMeet(DetailRegionLoop a, DetailRegionLoop b)
    {
        var pa = Outline(a);
        var pb = Outline(b);
        for (var i = 0; i < pa.Count; i++)
        for (var j = 0; j < pb.Count; j++)
            if (SegmentsMeet(pa[i], pa[(i + 1) % pa.Count], pb[j], pb[(j + 1) % pb.Count])) return true;
        return false;
    }

    /// <summary>True when every point of <paramref name="inner"/> is strictly inside <paramref name="outer"/> and the boundaries do not meet.</summary>
    private static bool IsInside(DetailRegionLoop inner, DetailRegionLoop outer) =>
        Outline(inner).All(p => Contains(outer, p)) && !EdgesMeet(inner, outer);

    private static bool Overlap(DetailRegionLoop a, DetailRegionLoop b) =>
        EdgesMeet(a, b) || Outline(a).Any(p => Contains(b, p)) || Outline(b).Any(p => Contains(a, p));

    /// <summary>The name in visibleParameter (or visible, when it is a name rather than true/false); empty when none.</summary>
    private static string Visible(JObject? item)
    {
        var token = item?["visibleParameter"] ?? item?["visibilityParameter"];
        return token == null || token.Type == JTokenType.Null ? string.Empty : Text(token);
    }

    private static string? OptionalText(JToken? token) =>
        token == null || token.Type == JTokenType.Null ? null : Text(token);

    /// <summary>"#RRGGBB", "r,g,b", [r,g,b] or {r,g,b} as #RRGGBB; null when not given.</summary>
    private static string? Colour(JToken? token, string where, List<string> errors, ref bool ok)
    {
        if (token == null || token.Type == JTokenType.Null) return null;
        double? r = null, g = null, b = null;
        if (token is JArray array && array.Count == 3)
        {
            r = Num(array[0]); g = Num(array[1]); b = Num(array[2]);
        }
        else if (token is JObject obj)
        {
            r = Num(obj["r"] ?? obj["red"]); g = Num(obj["g"] ?? obj["green"]); b = Num(obj["b"] ?? obj["blue"]);
        }
        else
        {
            var text = Text(token);
            var hex = text.StartsWith("#", StringComparison.Ordinal) ? text.Substring(1) : text;
            if (hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                return "#" + value.ToString("X6", CultureInfo.InvariantCulture);
            var parts = text.Split(',');
            if (parts.Length == 3)
            {
                r = Num(new JValue(parts[0].Trim())); g = Num(new JValue(parts[1].Trim())); b = Num(new JValue(parts[2].Trim()));
            }
        }
        if (IsChannel(r) && IsChannel(g) && IsChannel(b))
            return $"#{(int)r!.Value:X2}{(int)g!.Value:X2}{(int)b!.Value:X2}";
        errors.Add($"{where} is not a colour: use \"#RRGGBB\", \"r,g,b\" or [r, g, b].");
        ok = false;
        return null;
    }

    private static bool IsChannel(double? value) => value is >= 0 and <= 255 && Math.Abs(value.Value - Math.Round(value.Value)) < 1e-9;

    /// <summary>
    /// Checks what refers to a parameter by name: visibleParameter (curves, filled regions, labels) must be a yes/no
    /// parameter of <c>parameters</c>, and a label's parameter must be listed there too and comes with a label family.
    /// A shared parameter's data type is only known from the file, so its type is checked on the Revit side.
    /// </summary>
    public static void CheckParameterReferences(DetailFamilySpec spec, List<string> errors)
    {
        var byName = spec.Parameters.Where(p => p.Name.Length > 0)
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var (source, name) in spec.VisibilityReferences())
        {
            if (!byName.TryGetValue(name, out var parameter))
                errors.Add($"{source}.visibleParameter '{name}' is not in parameters: add it there as a yesNo parameter.");
            else if (parameter.Spec?.Friendly != null && parameter.Spec.Friendly != "yesNo")
                errors.Add($"{source}.visibleParameter '{name}' is a {parameter.Spec.Friendly} parameter; visibility needs a yesNo parameter.");
        }

        foreach (var label in spec.ParameterLabels)
        {
            if (!byName.ContainsKey(label.Parameter))
                errors.Add($"{label.Source}.parameter '{label.Parameter}' is not in parameters: add the parameter the label shows there.");
            if (label.LabelFamily.Length == 0)
                errors.Add($"{label.Source} shows a parameter, which needs labelFamily: a Generic Annotation family that already holds a label " +
                           "(a family loaded in the project, or a .rfa path). Revit's API cannot create a label, so the tool nests yours and links its parameter.");
            else if (string.Equals(LabelFamilyName(label.LabelFamily), spec.FamilyName, StringComparison.OrdinalIgnoreCase))
                errors.Add($"{label.Source}.labelFamily has the same name as the family being created.");
        }
    }

    /// <summary>The family name a labelFamily reference stands for: the file name of a .rfa path, else the name itself.</summary>
    public static string LabelFamilyName(string reference) =>
        reference.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)
            ? System.IO.Path.GetFileNameWithoutExtension(reference)
            : reference;

    public static bool IsLabelFamilyPath(string reference) => reference.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase);
}
