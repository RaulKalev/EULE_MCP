using System.IO;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Families;

/// <summary>A point of the family sketch in millimetres, on the family's reference level (origin = family insertion point).</summary>
public readonly struct DetailPoint
{
    public DetailPoint(double x, double y)
    {
        X = x;
        Y = y;
    }

    public double X { get; }
    public double Y { get; }

    public double DistanceTo(DetailPoint other) => Math.Sqrt((X - other.X) * (X - other.X) + (Y - other.Y) * (Y - other.Y));

    public override string ToString() => $"({X:0.###}, {Y:0.###})";
}

/// <summary>One curve revit_create_detail_family draws (#147). Rectangles arrive here as four lines.</summary>
public sealed class DetailCurveSpec
{
    /// <summary>line | arc | circle.</summary>
    public string Kind { get; set; } = "line";
    /// <summary>Where it came from in the request, for messages: lines[0], rectangles[2], …</summary>
    public string Source { get; set; } = string.Empty;
    public DetailPoint Start { get; set; }
    public DetailPoint End { get; set; }
    /// <summary>Arc through Start, Mid and End (three-point arc).</summary>
    public DetailPoint? Mid { get; set; }
    public DetailPoint Center { get; set; }
    public double Radius { get; set; }
    /// <summary>Degrees, counter-clockwise from +X; the arc runs counter-clockwise from StartAngle to EndAngle.</summary>
    public double StartAngle { get; set; }
    public double EndAngle { get; set; }
    /// <summary>Line style name; empty = the family's default (the category's own line).</summary>
    public string LineStyle { get; set; } = string.Empty;
}

/// <summary>Everything revit_create_detail_family is asked to do (#147, phase 1: geometry, line styles, purge, save, load).</summary>
public sealed class DetailFamilySpec
{
    public string FamilyName { get; set; } = string.Empty;
    /// <summary>The full .rfa path the family is saved to (its file name is the family name).</summary>
    public string SavePath { get; set; } = string.Empty;
    public bool Overwrite { get; set; }
    public string TemplatePath { get; set; } = string.Empty;
    public List<DetailCurveSpec> Curves { get; } = new();
    public bool Purge { get; set; } = true;
    public bool LoadIntoProject { get; set; }
    public string IfExists { get; set; } = "skip";
    /// <summary>Family and shared parameters to add (#147, phase 2).</summary>
    public List<FamilyParameterSpec> Parameters { get; } = new();
    /// <summary>Text placed through nested Generic Annotation families (#147, phase 3).</summary>
    public List<DetailLabelSpec> Labels { get; } = new();
    /// <summary>The Generic Annotation template for labels; empty = Metric Generic Annotation.rft.</summary>
    public string AnnotationTemplatePath { get; set; } = string.Empty;
    /// <summary>Shared parameter file for shared parameters; empty = the config's, else Revit's setting.</summary>
    public string SharedParameterFile { get; set; } = string.Empty;

    /// <summary>Distinct line style names the curves ask for, in first-use order.</summary>
    public List<string> LineStyleNames() => Curves
        .Select(c => c.LineStyle)
        .Where(n => n.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public Dictionary<string, int> CountsByKind() => Curves
        .GroupBy(c => c.Kind)
        .ToDictionary(g => g.Key, g => g.Count());
}

/// <summary>
/// Reads and checks the arguments of revit_create_detail_family (#147). Geometry is in millimetres on the family's
/// reference level. Pure: no Revit types, unit tested.
/// </summary>
public static partial class DetailFamilySpecParser
{
    public const int MaxCurves = 2000;
    /// <summary>Revit's short-curve tolerance (Application.ShortCurveTolerance, about 0.78 mm), rounded up: anything shorter is refused.</summary>
    public const double MinCurveLengthMm = 0.8;
    public const string DefaultTemplateFileName = "Metric Detail Item.rft";

    /// <summary>Characters Revit refuses in family names, plus those Windows refuses in file names.</summary>
    private const string ForbiddenNameCharacters = "\\/:*?\"<>|{}[];`~";

    public static DetailFamilySpec Parse(IReadOnlyDictionary<string, object?> args, List<string> errors)
    {
        var spec = new DetailFamilySpec
        {
            FamilyName = Str(args, "familyName"),
            Overwrite = Bool(args, "overwrite", false),
            TemplatePath = Str(args, "templatePath"),
            Purge = Bool(args, "purge", true),
            LoadIntoProject = Bool(args, "loadIntoProject", false),
            IfExists = Str(args, "ifExists", "skip"),
            AnnotationTemplatePath = Str(args, "annotationTemplatePath"),
            SharedParameterFile = Str(args, "sharedParameterFile")
        };
        var defaultStyle = Str(args, "lineStyle");

        var (savePath, familyName, pathError) = ResolveSavePath(Str(args, "savePath"), spec.FamilyName);
        if (pathError != null) errors.Add(pathError);
        spec.SavePath = savePath ?? string.Empty;
        spec.FamilyName = familyName ?? spec.FamilyName;

        if (!string.Equals(spec.IfExists, "skip", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(spec.IfExists, "overwrite", StringComparison.OrdinalIgnoreCase))
            errors.Add($"ifExists '{spec.IfExists}' is not supported: skip | overwrite.");

        ReadLines(args, "lines", spec, defaultStyle, errors);
        ReadRectangles(args, "rectangles", spec, defaultStyle, errors);
        ReadArcs(args, "arcs", spec, defaultStyle, errors);
        ReadCircles(args, "circles", spec, defaultStyle, errors);
        ReadParameters(args, "parameters", spec, errors);
        ReadLabels(args, "labels", spec, errors);

        if (spec.Curves.Count == 0 && spec.Labels.Count == 0)
            errors.Add("Nothing to draw: give lines, rectangles, arcs, circles or labels (millimetres, on the family's reference level).");
        else if (spec.Curves.Count > MaxCurves)
            errors.Add($"{spec.Curves.Count} curves were requested; at most {MaxCurves} per family.");
        return spec;
    }

    /// <summary>
    /// savePath is either the full .rfa path (its file name is the family name) or a folder, joined with familyName.
    /// There is no default folder: the user says where the family goes.
    /// </summary>
    public static (string? Path, string? FamilyName, string? Error) ResolveSavePath(string savePath, string familyName)
    {
        savePath = savePath.Trim();
        familyName = familyName.Trim();
        if (savePath.Length == 0)
            return (null, null, "savePath is required: ask the user where to save the family (a folder, or the full .rfa path). There is no default location.");

        string folder;
        string name;
        if (savePath.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
        {
            folder = Path.GetDirectoryName(savePath) ?? string.Empty;
            name = Path.GetFileNameWithoutExtension(savePath);
            if (familyName.Length > 0 && !string.Equals(familyName, name, StringComparison.Ordinal))
                return (null, null, $"familyName '{familyName}' differs from the file name '{name}' in savePath: a family's name is its file name. Give one of them, or make them match.");
        }
        else if (Path.HasExtension(savePath) && !Directory.Exists(savePath))
        {
            return (null, null, $"savePath '{savePath}' must be a folder or a .rfa file.");
        }
        else
        {
            if (familyName.Length == 0)
                return (null, null, "familyName is required when savePath is a folder.");
            folder = savePath;
            name = familyName;
        }

        var nameError = CheckFamilyName(name);
        if (nameError != null) return (null, null, nameError);
        if (folder.Length == 0 || !Path.IsPathRooted(folder))
            return (null, null, $"savePath '{savePath}' must be an absolute path.");
        return (Path.Combine(folder, name + ".rfa"), name, null);
    }

    public static string? CheckFamilyName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "The family name is empty.";
        if (name != name.Trim()) return $"The family name '{name}' starts or ends with a space.";
        var bad = name.Where(c => ForbiddenNameCharacters.IndexOf(c) >= 0 || char.IsControl(c)).Distinct().ToList();
        if (bad.Count > 0) return $"The family name '{name}' contains characters Revit or Windows refuse: {string.Join(" ", bad)}.";
        if (name.Length > 200) return "The family name is longer than 200 characters.";
        return null;
    }

    /// <summary>
    /// Where the template is: the explicit path, else Metric Detail Item.rft in Revit's family template folder, its
    /// English folder, or anywhere below it (language packs put it in a sub-folder).
    /// </summary>
    public static (string? Path, string? Error) ResolveTemplate(string explicitPath, string familyTemplateRoot,
        Func<string, bool> fileExists, Func<string, IEnumerable<string>> findBelow) =>
        ResolveTemplate(explicitPath, familyTemplateRoot, fileExists, findBelow, DefaultTemplateFileName);

    /// <summary>
    /// Where a template is: the explicit path, else <paramref name="fileName"/> in Revit's family template folder, its
    /// English and Annotations folders, or anywhere below it.
    /// </summary>
    public static (string? Path, string? Error) ResolveTemplate(string explicitPath, string familyTemplateRoot,
        Func<string, bool> fileExists, Func<string, IEnumerable<string>> findBelow, string fileName)
    {
        explicitPath = explicitPath.Trim();
        if (explicitPath.Length > 0)
        {
            if (!explicitPath.EndsWith(".rft", StringComparison.OrdinalIgnoreCase))
                return (null, $"templatePath '{explicitPath}' is not a family template (.rft).");
            return fileExists(explicitPath) ? (explicitPath, null) : (null, $"The family template '{explicitPath}' does not exist.");
        }

        var argument = string.Equals(fileName, DefaultTemplateFileName, StringComparison.OrdinalIgnoreCase) ? "templatePath" : "annotationTemplatePath";
        if (string.IsNullOrWhiteSpace(familyTemplateRoot))
            return (null, $"Revit has no family template folder set; pass {argument} (the {fileName} file).");

        foreach (var candidate in new[]
                 {
                     Path.Combine(familyTemplateRoot, fileName),
                     Path.Combine(familyTemplateRoot, "Annotations", fileName),
                     Path.Combine(familyTemplateRoot, "English", fileName),
                     Path.Combine(familyTemplateRoot, "English", "Annotations", fileName),
                     Path.Combine(familyTemplateRoot, "English_I", fileName),
                     Path.Combine(familyTemplateRoot, "English_I", "Annotations", fileName)
                 })
        {
            if (fileExists(candidate)) return (candidate, null);
        }

        var found = findBelow(familyTemplateRoot).OrderBy(p => p.Length).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        return found != null
            ? (found, null)
            : (null, $"{fileName} was not found under Revit's family template folder '{familyTemplateRoot}'; pass {argument}.");
    }

    // ── Geometry ─────────────────────────────────────────────────────────────

    private static void ReadLines(IReadOnlyDictionary<string, object?> args, string key, DetailFamilySpec spec, string defaultStyle, List<string> errors)
    {
        var index = 0;
        foreach (var item in Items(args, key, errors))
        {
            var source = $"{key}[{index++}]";
            var start = Point(item?["start"], source + ".start", errors);
            var end = Point(item?["end"], source + ".end", errors);
            if (start == null || end == null) continue;
            if (start.Value.DistanceTo(end.Value) < MinCurveLengthMm)
            {
                errors.Add($"{source} is shorter than Revit's minimum curve length ({MinCurveLengthMm} mm).");
                continue;
            }
            spec.Curves.Add(new DetailCurveSpec { Kind = "line", Source = source, Start = start.Value, End = end.Value, LineStyle = Style(item, defaultStyle) });
        }
    }

    private static void ReadRectangles(IReadOnlyDictionary<string, object?> args, string key, DetailFamilySpec spec, string defaultStyle, List<string> errors)
    {
        var index = 0;
        foreach (var item in Items(args, key, errors))
        {
            var source = $"{key}[{index++}]";
            var a = Point(item?["min"], source + ".min", errors);
            var b = Point(item?["max"], source + ".max", errors);
            if (a == null || b == null) continue;
            double x0 = Math.Min(a.Value.X, b.Value.X), x1 = Math.Max(a.Value.X, b.Value.X);
            double y0 = Math.Min(a.Value.Y, b.Value.Y), y1 = Math.Max(a.Value.Y, b.Value.Y);
            if (x1 - x0 < MinCurveLengthMm || y1 - y0 < MinCurveLengthMm)
            {
                errors.Add($"{source} has a side shorter than Revit's minimum curve length ({MinCurveLengthMm} mm).");
                continue;
            }
            var style = Style(item, defaultStyle);
            var corners = new[] { new DetailPoint(x0, y0), new DetailPoint(x1, y0), new DetailPoint(x1, y1), new DetailPoint(x0, y1) };
            for (var i = 0; i < 4; i++)
                spec.Curves.Add(new DetailCurveSpec { Kind = "line", Source = source, Start = corners[i], End = corners[(i + 1) % 4], LineStyle = style });
        }
    }

    private static void ReadArcs(IReadOnlyDictionary<string, object?> args, string key, DetailFamilySpec spec, string defaultStyle, List<string> errors)
    {
        var index = 0;
        foreach (var item in Items(args, key, errors))
        {
            var source = $"{key}[{index++}]";
            if (item?["mid"] != null)
            {
                var start = Point(item["start"], source + ".start", errors);
                var mid = Point(item["mid"], source + ".mid", errors);
                var end = Point(item["end"], source + ".end", errors);
                if (start == null || mid == null || end == null) continue;
                var cross = (mid.Value.X - start.Value.X) * (end.Value.Y - start.Value.Y) - (mid.Value.Y - start.Value.Y) * (end.Value.X - start.Value.X);
                if (Math.Abs(cross) < 1e-6 || start.Value.DistanceTo(end.Value) < MinCurveLengthMm)
                {
                    errors.Add($"{source}: start, mid and end are on one line or too close together for an arc.");
                    continue;
                }
                spec.Curves.Add(new DetailCurveSpec { Kind = "arc", Source = source, Start = start.Value, Mid = mid.Value, End = end.Value, LineStyle = Style(item, defaultStyle) });
                continue;
            }

            var center = Point(item?["center"], source + ".center", errors);
            var radius = Num(item?["radius"]);
            var startAngle = Num(item?["startAngle"]);
            var endAngle = Num(item?["endAngle"]);
            if (center == null) continue;
            if (radius == null || radius <= 0) { errors.Add($"{source}.radius must be a positive number (mm)."); continue; }
            if (startAngle == null || endAngle == null) { errors.Add($"{source} needs startAngle and endAngle (degrees), or start, mid and end points."); continue; }
            var sweep = Sweep(startAngle.Value, endAngle.Value);
            if (sweep <= 0 || sweep >= 360) { errors.Add($"{source}: the arc from {startAngle}° to {endAngle}° is empty or a full circle (use circles)."); continue; }
            if (radius.Value * sweep * Math.PI / 180 < MinCurveLengthMm) { errors.Add($"{source} is shorter than Revit's minimum curve length ({MinCurveLengthMm} mm)."); continue; }
            spec.Curves.Add(new DetailCurveSpec
            {
                Kind = "arc", Source = source, Center = center.Value, Radius = radius.Value,
                StartAngle = startAngle.Value, EndAngle = endAngle.Value, LineStyle = Style(item, defaultStyle)
            });
        }
    }

    private static void ReadCircles(IReadOnlyDictionary<string, object?> args, string key, DetailFamilySpec spec, string defaultStyle, List<string> errors)
    {
        var index = 0;
        foreach (var item in Items(args, key, errors))
        {
            var source = $"{key}[{index++}]";
            var center = Point(item?["center"], source + ".center", errors);
            var radius = Num(item?["radius"]);
            if (center == null) continue;
            if (radius == null || radius <= 0) { errors.Add($"{source}.radius must be a positive number (mm)."); continue; }
            if (radius.Value * 2 * Math.PI < MinCurveLengthMm) { errors.Add($"{source} is smaller than Revit's minimum curve length."); continue; }
            spec.Curves.Add(new DetailCurveSpec { Kind = "circle", Source = source, Center = center.Value, Radius = radius.Value, LineStyle = Style(item, defaultStyle) });
        }
    }

    /// <summary>Counter-clockwise sweep in degrees from start to end, in (0, 360].</summary>
    public static double Sweep(double startAngle, double endAngle)
    {
        var sweep = (endAngle - startAngle) % 360;
        if (sweep < 0) sweep += 360;
        return Math.Abs(endAngle - startAngle) >= 360 ? 360 : sweep;
    }

    // ── JSON helpers ─────────────────────────────────────────────────────────

    private static IEnumerable<JObject?> Items(IReadOnlyDictionary<string, object?> args, string key, List<string> errors)
    {
        if (!args.TryGetValue(key, out var raw) || raw == null) return Array.Empty<JObject?>();
        JToken? token = raw switch
        {
            JToken t => t,
            string s when s.Trim().Length == 0 => null,
            string s => TryParse(s),
            _ => TryFrom(raw)
        };
        if (token == null || token.Type == JTokenType.Null) return Array.Empty<JObject?>();
        if (token is not JArray array)
        {
            errors.Add($"{key} must be an array.");
            return Array.Empty<JObject?>();
        }
        var list = new List<JObject?>();
        var index = 0;
        foreach (var item in array)
        {
            if (item is JObject obj) list.Add(obj);
            else errors.Add($"{key}[{index}] must be an object.");
            index++;
        }
        return list;
    }

    private static DetailPoint? Point(JToken? token, string where, List<string> errors)
    {
        double? x = null, y = null;
        if (token is JArray array && array.Count >= 2)
        {
            x = Num(array[0]);
            y = Num(array[1]);
        }
        else if (token is JObject obj)
        {
            x = Num(obj["x"]);
            y = Num(obj["y"]);
        }
        if (x == null || y == null)
        {
            errors.Add($"{where} must be a point [x, y] or {{x, y}} in millimetres.");
            return null;
        }
        return new DetailPoint(x.Value, y.Value);
    }

    private static double? Num(JToken? token)
    {
        if (token == null) return null;
        if (token.Type is JTokenType.Integer or JTokenType.Float) return (double)token;
        if (token.Type == JTokenType.String
            && double.TryParse((string?)token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
            return v;
        return null;
    }

    private static string Style(JObject? item, string defaultStyle)
    {
        var token = item?["lineStyle"];
        var text = token == null || token.Type == JTokenType.Null ? string.Empty : ((string?)token ?? string.Empty).Trim();
        return text.Length > 0 ? text : defaultStyle;
    }

    private static string Str(IReadOnlyDictionary<string, object?> args, string key, string fallback = "")
    {
        if (!args.TryGetValue(key, out var raw) || raw == null) return fallback;
        var text = raw is JValue value ? value.Value?.ToString() : raw is JToken token ? token.ToString() : raw.ToString();
        return string.IsNullOrWhiteSpace(text) ? fallback : text!.Trim();
    }

    private static bool Bool(IReadOnlyDictionary<string, object?> args, string key, bool fallback)
    {
        if (!args.TryGetValue(key, out var raw) || raw == null) return fallback;
        return raw switch
        {
            bool b => b,
            JValue { Type: JTokenType.Boolean } v => (bool)v,
            _ => bool.TryParse(raw is JValue jv ? jv.Value?.ToString() : raw.ToString(), out var parsed) ? parsed : fallback
        };
    }

    private static JToken? TryParse(string text)
    {
        try { return JToken.Parse(text); }
        catch { return null; }
    }

    private static JToken? TryFrom(object raw)
    {
        try { return JToken.FromObject(raw); }
        catch { return null; }
    }
}

/// <summary>A line style of the project, as revit_create_detail_family copies it into the family.</summary>
public sealed class ProjectLineStyleInfo
{
    public string Name { get; set; } = string.Empty;
    public int? Weight { get; set; }
    /// <summary>#RRGGBB.</summary>
    public string Color { get; set; } = string.Empty;
    /// <summary>The line pattern's name; empty = solid.</summary>
    public string Pattern { get; set; } = string.Empty;
}

/// <summary>What happens to one line style the spec asks for.</summary>
public sealed class LineStylePlanItem
{
    public string Requested { get; set; } = string.Empty;
    /// <summary>project (copied from the project's style of that name) | missing (the project has no such style).</summary>
    public string Status { get; set; } = string.Empty;
    public ProjectLineStyleInfo? Project { get; set; }
    public List<string> UsedBy { get; set; } = new();
}

/// <summary>Matches the spec's line styles to the project's (#147). Pure.</summary>
public static class DetailFamilyLineStylePlanner
{
    public static List<LineStylePlanItem> Plan(DetailFamilySpec spec, IReadOnlyCollection<ProjectLineStyleInfo> projectStyles)
    {
        var byName = projectStyles
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        return spec.LineStyleNames().Select(name =>
        {
            byName.TryGetValue(name, out var style);
            return new LineStylePlanItem
            {
                Requested = name,
                Status = style != null ? "project" : "missing",
                Project = style,
                UsedBy = spec.Curves.Where(c => string.Equals(c.LineStyle, name, StringComparison.OrdinalIgnoreCase))
                    .Select(c => c.Source).Distinct().Take(10).ToList()
            };
        }).ToList();
    }

    /// <summary>Closest project style names, for the "missing" message.</summary>
    public static List<string> Suggest(string requested, IEnumerable<ProjectLineStyleInfo> projectStyles, int max = 5)
    {
        var needle = requested.Trim();
        return projectStyles
            .Select(s => s.Name)
            .Where(n => n.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 || needle.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0
                        || SharedPrefix(n, needle) >= Math.Min(4, needle.Length))
            .OrderByDescending(n => SharedPrefix(n, needle))
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
    }

    private static int SharedPrefix(string a, string b)
    {
        var i = 0;
        while (i < a.Length && i < b.Length && char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[i])) i++;
        return i;
    }
}
