using System.Globalization;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Patterns;

/// <summary>One segment of a line pattern or of a fill grid: a dash, a space or a dot (a dot has no length).</summary>
public sealed class PatternSegment
{
    public const string Dash = "dash";
    public const string Space = "space";
    public const string Dot = "dot";

    public string Type { get; set; } = Dash;
    /// <summary>Millimetres; 0 for a dot.</summary>
    public double LengthMm { get; set; }

    public PatternSegment() { }
    public PatternSegment(string type, double lengthMm) { Type = type; LengthMm = lengthMm; }

    public bool IsVisible => Type != Space;
}

/// <summary>A line pattern to create, or whose segments replace those of the pattern of that name.</summary>
public sealed class LinePatternDefinition
{
    /// <summary>Where it came from in the request, for messages: patterns[0], …</summary>
    public string Source { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<PatternSegment> Segments { get; set; } = new();
}

/// <summary>One family of parallel lines of a fill pattern, in millimetres and degrees.</summary>
public sealed class FillGridDefinition
{
    public double AngleDeg { get; set; }
    public double OriginXMm { get; set; }
    public double OriginYMm { get; set; }
    /// <summary>Distance between the parallel lines; never 0.</summary>
    public double OffsetMm { get; set; }
    /// <summary>Moves the dashes along each next line; ignored for solid lines.</summary>
    public double ShiftMm { get; set; }
    /// <summary>Empty: solid lines.</summary>
    public List<PatternSegment> Segments { get; set; } = new();
}

/// <summary>A fill pattern to create, or whose grids replace those of the non-solid pattern of that name and target.</summary>
public sealed class FillPatternDefinition
{
    public const string Drafting = "drafting";
    public const string Model = "model";
    public const string ToView = "toView";
    public const string ToHost = "toHost";
    public const string AsText = "asText";

    public string Source { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>drafting (sizes on paper) or model (sizes in the model).</summary>
    public string Target { get; set; } = Drafting;
    /// <summary>toView | toHost | asText; null = not given (a new pattern gets <see cref="PatternSpecParser.DefaultOrientation"/>).</summary>
    public string? Orientation { get; set; }
    /// <summary>True when the request used the simple form {angle, spacing, crosshatch?}.</summary>
    public bool Simple { get; set; }
    public List<FillGridDefinition> Grids { get; set; } = new();
}

/// <summary>
/// Arguments of revit_set_line_patterns and revit_set_fill_patterns, their validation and comparison. Pure: no Revit
/// types, unit tested. Lengths are millimetres: on paper for line patterns and drafting fill patterns, in the model for
/// model fill patterns. Angles are degrees, counter-clockwise from the x axis.
/// </summary>
public static class PatternSpecParser
{
    public const double MmPerFoot = 304.8;
    public const int MaxPatternsPerCall = 50;
    public const int MaxLineSegments = 40;
    public const int MaxGrids = 64;
    public const int MaxGridSegments = 40;
    /// <summary>The connector's limits for a dash, a space, a spacing or an offset, in millimetres.</summary>
    public const double MinLengthMm = 0.01;
    public const double MaxLengthMm = 10000;
    /// <summary>A drafting spacing below this draws nearly solid; the preview warns.</summary>
    public const double DenseDraftingSpacingMm = 0.3;
    public const double LengthToleranceMm = 0.001;
    public const double AngleToleranceDeg = 0.0001;

    /// <summary>Characters Revit refuses in element names.</summary>
    private const string ForbiddenNameCharacters = "\\:{}[]|;<>?`~";

    // ── Line patterns ────────────────────────────────────────────────────────

    /// <summary>Reads [{name, segments: [{type: dash|space|dot, length}] or [3, -1.5, 0, -1.5]}].</summary>
    public static List<LinePatternDefinition> ReadLinePatterns(IReadOnlyDictionary<string, object?> args, string key, List<string> errors)
    {
        var result = new List<LinePatternDefinition>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in Items(args, key, errors))
        {
            var source = $"{key}[{index++}]";
            if (item == null) continue;
            var name = Text(item["name"]);
            if (name.Length == 0) { errors.Add($"{source} needs a name."); continue; }
            var nameError = CheckName(name, "line pattern");
            if (nameError != null) { errors.Add($"{source}: {nameError}"); continue; }
            if (IsSolidLineName(name))
            {
                errors.Add($"{source}: '{name}' is Revit's built-in solid line; it cannot be created or edited. Use another name.");
                continue;
            }
            if (!seen.Add(name)) { errors.Add($"{source}: the line pattern '{name}' is listed twice."); continue; }

            var segments = ReadSegments(item["segments"], source + ".segments", errors);
            if (segments == null) continue;
            var problem = CheckSegments(segments, source + ".segments", lineOrSolid: false, MaxLineSegments);
            if (problem != null) { errors.Add(problem); continue; }
            result.Add(new LinePatternDefinition { Source = source, Name = name, Segments = segments });
        }
        if (result.Count > MaxPatternsPerCall)
            errors.Add($"{result.Count} line patterns were given; at most {MaxPatternsPerCall} per call.");
        return result;
    }

    public static bool IsSolidLineName(string name) => name.Trim().Equals("solid", StringComparison.OrdinalIgnoreCase);

    // ── Fill patterns ────────────────────────────────────────────────────────

    /// <summary>
    /// Reads [{name, target?, orientation?, angle?, spacing, crosshatch?}] (the simple form) or
    /// [{name, target?, orientation?, grids: [{angle?, origin?, offset, shift?, segments?}]}].
    /// </summary>
    public static List<FillPatternDefinition> ReadFillPatterns(IReadOnlyDictionary<string, object?> args, string key, List<string> errors)
    {
        var result = new List<FillPatternDefinition>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in Items(args, key, errors))
        {
            var source = $"{key}[{index++}]";
            if (item == null) continue;
            var name = Text(item["name"]);
            if (name.Length == 0) { errors.Add($"{source} needs a name."); continue; }
            if (IsSolidFillName(name))
            {
                errors.Add($"{source}: '{name}' is Revit's solid fill; it cannot be created or edited. Use another name.");
                continue;
            }
            var nameError = CheckName(name, "fill pattern");
            if (nameError != null) { errors.Add($"{source}: {nameError}"); continue; }

            var definition = new FillPatternDefinition { Source = source, Name = name };
            var ok = true;

            var targetText = Text(item["target"]);
            if (targetText.Length > 0)
            {
                var target = ParseTarget(targetText);
                if (target == null) { errors.Add($"{source}.target '{targetText}' is not drafting or model."); ok = false; }
                else definition.Target = target;
            }
            if (!seen.Add(definition.Target + "|" + name))
            {
                errors.Add($"{source}: the {definition.Target} fill pattern '{name}' is listed twice.");
                continue;
            }

            var orientationText = Text(item["orientation"] ?? item["hostOrientation"]);
            if (orientationText.Length > 0)
            {
                definition.Orientation = ParseOrientation(orientationText);
                if (definition.Orientation == null)
                {
                    errors.Add($"{source}.orientation '{orientationText}' is not toView (orient to view), toHost (align with element) or asText (keep readable).");
                    ok = false;
                }
            }

            var gridsToken = item["grids"];
            var hasGrids = gridsToken != null && gridsToken.Type != JTokenType.Null;
            var hasSimple = Given(item["spacing"]) || Given(item["angle"]) || Given(item["crosshatch"]);
            if (hasGrids && hasSimple)
            {
                errors.Add($"{source}: give either the simple form (angle, spacing, crosshatch) or grids, not both.");
                continue;
            }
            if (!hasGrids && !hasSimple)
            {
                errors.Add($"{source} needs spacing (mm, with angle and crosshatch optional) or grids.");
                continue;
            }

            var grids = hasGrids ? ReadGrids(gridsToken!, source + ".grids", errors) : ReadSimple(item, source, errors);
            if (grids == null) continue;
            definition.Grids = grids;
            definition.Simple = !hasGrids;
            if (ok) result.Add(definition);
        }
        if (result.Count > MaxPatternsPerCall)
            errors.Add($"{result.Count} fill patterns were given; at most {MaxPatternsPerCall} per call.");
        return result;
    }

    /// <summary>The orientation a new pattern of this target gets when none is given.</summary>
    public static string DefaultOrientation(string target) => target == FillPatternDefinition.Model ? FillPatternDefinition.ToHost : FillPatternDefinition.ToView;

    public static bool IsSolidFillName(string name)
    {
        var text = name.Trim().Trim('<', '>').Trim();
        return text.Equals("solid", StringComparison.OrdinalIgnoreCase) || text.Equals("solid fill", StringComparison.OrdinalIgnoreCase);
    }

    public static string? ParseTarget(string text) => text.Trim().ToLowerInvariant() switch
    {
        "drafting" or "draft" or "drafting pattern" => FillPatternDefinition.Drafting,
        "model" or "model pattern" => FillPatternDefinition.Model,
        _ => null
    };

    public static string? ParseOrientation(string text) => text.Trim().Replace(" ", string.Empty).Replace("_", string.Empty).ToLowerInvariant() switch
    {
        "toview" or "view" or "orienttoview" => FillPatternDefinition.ToView,
        "tohost" or "host" or "alignwithelement" or "element" => FillPatternDefinition.ToHost,
        "astext" or "text" or "keepreadable" => FillPatternDefinition.AsText,
        _ => null
    };

    /// <summary>Warnings for a definition Revit may draw nearly solid or refuse as too dense.</summary>
    public static List<string> DensityWarnings(FillPatternDefinition definition)
    {
        var warnings = new List<string>();
        if (definition.Target != FillPatternDefinition.Drafting) return warnings;
        var dense = definition.Grids.Where(g => Math.Abs(g.OffsetMm) < DenseDraftingSpacingMm).ToList();
        if (dense.Count > 0)
            warnings.Add($"{definition.Source} ('{definition.Name}'): a spacing below {DenseDraftingSpacingMm} mm on paper draws nearly solid, and Revit may refuse it as too dense.");
        return warnings;
    }

    private static List<FillGridDefinition>? ReadSimple(JObject item, string source, List<string> errors)
    {
        var angle = Num(item["angle"]) ?? 0;
        if (Given(item["angle"]) && Num(item["angle"]) == null) { errors.Add($"{source}.angle must be a number of degrees."); return null; }
        var spacing = Num(item["spacing"]);
        if (spacing == null) { errors.Add($"{source}.spacing must be the distance between the lines in mm."); return null; }
        var spacingError = CheckLength(spacing.Value, source + ".spacing");
        if (spacingError != null) { errors.Add(spacingError); return null; }

        var grids = new List<FillGridDefinition> { new() { AngleDeg = NormalizeAngle(angle), OffsetMm = spacing.Value } };
        var cross = item["crosshatch"];
        if (cross == null || cross.Type == JTokenType.Null || (cross.Type == JTokenType.Boolean && !(bool)cross)) return grids;

        var crossAngle = angle + 90;
        var crossSpacing = spacing.Value;
        if (cross is JObject crossObject)
        {
            if (Given(crossObject["angle"]))
            {
                var a = Num(crossObject["angle"]);
                if (a == null) { errors.Add($"{source}.crosshatch.angle must be a number of degrees."); return null; }
                crossAngle = a.Value;
            }
            if (Given(crossObject["spacing"]))
            {
                var s = Num(crossObject["spacing"]);
                var error = s == null ? $"{source}.crosshatch.spacing must be a distance in mm." : CheckLength(s.Value, source + ".crosshatch.spacing");
                if (error != null) { errors.Add(error); return null; }
                crossSpacing = s!.Value;
            }
        }
        else if (cross.Type != JTokenType.Boolean)
        {
            errors.Add($"{source}.crosshatch must be true or {{angle?, spacing?}}.");
            return null;
        }
        if (Math.Abs(NormalizeAngle(crossAngle) - NormalizeAngle(angle)) < AngleToleranceDeg
            || Math.Abs(Math.Abs(NormalizeAngle(crossAngle) - NormalizeAngle(angle)) - 180) < AngleToleranceDeg)
        {
            errors.Add($"{source}.crosshatch.angle is parallel to angle; a crosshatch needs a second direction.");
            return null;
        }
        grids.Add(new FillGridDefinition { AngleDeg = NormalizeAngle(crossAngle), OffsetMm = crossSpacing });
        return grids;
    }

    private static List<FillGridDefinition>? ReadGrids(JToken token, string where, List<string> errors)
    {
        if (token is not JArray array || array.Count == 0) { errors.Add($"{where} must be a non-empty array of {{angle, origin, offset, shift, segments}}."); return null; }
        if (array.Count > MaxGrids) { errors.Add($"{where} has {array.Count} grids; at most {MaxGrids}."); return null; }
        var grids = new List<FillGridDefinition>();
        for (var i = 0; i < array.Count; i++)
        {
            var at = $"{where}[{i}]";
            if (array[i] is not JObject grid) { errors.Add($"{at} must be an object."); return null; }

            var angle = Given(grid["angle"]) ? Num(grid["angle"]) : 0;
            if (angle == null) { errors.Add($"{at}.angle must be a number of degrees."); return null; }

            double ox = 0, oy = 0;
            var origin = grid["origin"];
            if (Given(origin))
            {
                double? x = null, y = null;
                if (origin is JArray o && o.Count == 2) { x = Num(o[0]); y = Num(o[1]); }
                else if (origin is JObject p) { x = Num(p["x"]); y = Num(p["y"]); }
                if (x == null || y == null) { errors.Add($"{at}.origin must be [x, y] in mm."); return null; }
                ox = x.Value; oy = y.Value;
            }

            var offset = Num(grid["offset"] ?? grid["spacing"]);
            if (offset == null) { errors.Add($"{at}.offset must be the distance between the lines in mm."); return null; }
            var offsetError = CheckLength(Math.Abs(offset.Value), at + ".offset");
            if (offsetError != null) { errors.Add(offsetError); return null; }

            var shift = Given(grid["shift"]) ? Num(grid["shift"]) : 0;
            if (shift == null || Math.Abs(shift.Value) > MaxLengthMm) { errors.Add($"{at}.shift must be a number of mm (at most {MaxLengthMm})."); return null; }

            var segments = new List<PatternSegment>();
            if (Given(grid["segments"]))
            {
                var read = ReadSegments(grid["segments"], at + ".segments", errors);
                if (read == null) return null;
                var problem = CheckSegments(read, at + ".segments", lineOrSolid: true, MaxGridSegments);
                if (problem != null) { errors.Add(problem); return null; }
                segments = read;
            }

            grids.Add(new FillGridDefinition
            {
                AngleDeg = NormalizeAngle(angle.Value),
                OriginXMm = ox,
                OriginYMm = oy,
                OffsetMm = offset.Value,
                ShiftMm = shift.Value,
                Segments = segments
            });
        }
        return grids;
    }

    // ── Segments ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads [{type: dash|space|dot, length}] or numbers in the .pat way (positive a dash, negative a space, 0 a dot).
    /// Null after adding an error.
    /// </summary>
    public static List<PatternSegment>? ReadSegments(JToken? token, string where, List<string> errors)
    {
        if (token is not JArray array) { errors.Add($"{where} must be an array of {{type: dash|space|dot, length (mm)}}."); return null; }
        var result = new List<PatternSegment>();
        for (var i = 0; i < array.Count; i++)
        {
            var at = $"{where}[{i}]";
            var item = array[i];
            if (item.Type is JTokenType.Integer or JTokenType.Float)
            {
                var value = (double)item;
                result.Add(value > 0 ? new PatternSegment(PatternSegment.Dash, value)
                    : value < 0 ? new PatternSegment(PatternSegment.Space, -value)
                    : new PatternSegment(PatternSegment.Dot, 0));
                continue;
            }
            if (item is not JObject obj) { errors.Add($"{at} must be {{type: dash|space|dot, length}} or a number."); return null; }
            var typeText = Text(obj["type"]).ToLowerInvariant();
            var type = typeText switch
            {
                "dash" or "line" => PatternSegment.Dash,
                "space" or "gap" => PatternSegment.Space,
                "dot" => PatternSegment.Dot,
                _ => null
            };
            if (type == null) { errors.Add($"{at}.type '{typeText}' is not dash, space or dot."); return null; }
            var lengthToken = obj["length"] ?? obj["lengthMm"];
            if (type == PatternSegment.Dot)
            {
                if (Given(lengthToken) && Num(lengthToken) is not (null or 0d))
                {
                    errors.Add($"{at}: a dot has no length; leave length out, or use a short dash.");
                    return null;
                }
                result.Add(new PatternSegment(PatternSegment.Dot, 0));
                continue;
            }
            var length = Num(lengthToken);
            if (length == null) { errors.Add($"{at} ({type}) needs a length in mm."); return null; }
            result.Add(new PatternSegment(type, length.Value));
        }
        return result;
    }

    /// <summary>
    /// What Revit needs of a segment list: a dash or dot first, then alternating with spaces, an even count, dashes and
    /// spaces within the length limits. <paramref name="lineOrSolid"/> allows an empty list (a solid grid line).
    /// </summary>
    public static string? CheckSegments(IReadOnlyList<PatternSegment> segments, string where, bool lineOrSolid, int max)
    {
        if (segments.Count == 0)
            return lineOrSolid ? null : $"{where} needs at least a dash or dot and a space.";
        if (segments.Count > max) return $"{where} has {segments.Count} segments; at most {max}.";
        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            if (i % 2 == 0 && !s.IsVisible)
                return $"{where}[{i}] is a space where a dash or dot belongs: a pattern starts with a dash or dot and alternates with spaces.";
            if (i % 2 == 1 && s.IsVisible)
                return $"{where}[{i}] is a {s.Type} where a space belongs: put a space between every two dashes or dots.";
            if (s.Type != PatternSegment.Dot)
            {
                var error = CheckLength(s.LengthMm, $"{where}[{i}] ({s.Type})");
                if (error != null) return error;
            }
        }
        if (segments.Count % 2 == 1) return $"{where} ends with a {segments[segments.Count - 1].Type}: end it with a space (an even number of segments).";
        return null;
    }

    /// <summary>
    /// Segments from a fill grid's stored lengths in mm. With a negative value they are read the .pat way (negative a
    /// space, 0 a dot); otherwise as alternating visible and space lengths starting with a visible one (0 a dot).
    /// </summary>
    public static List<PatternSegment> SegmentsFromGridLengths(IReadOnlyList<double> lengthsMm)
    {
        var signed = lengthsMm.Any(v => v < 0);
        var result = new List<PatternSegment>();
        for (var i = 0; i < lengthsMm.Count; i++)
        {
            var v = lengthsMm[i];
            if (signed)
                result.Add(v > 0 ? new PatternSegment(PatternSegment.Dash, v) : v < 0 ? new PatternSegment(PatternSegment.Space, -v) : new PatternSegment(PatternSegment.Dot, 0));
            else if (i % 2 == 1)
                result.Add(new PatternSegment(PatternSegment.Space, v));
            else
                result.Add(Math.Abs(v) < 1e-9 ? new PatternSegment(PatternSegment.Dot, 0) : new PatternSegment(PatternSegment.Dash, v));
        }
        return result;
    }

    /// <summary>The lengths a fill grid stores: positive and alternating, a dot as 0, in mm.</summary>
    public static List<double> ToGridLengths(IEnumerable<PatternSegment> segments) =>
        segments.Select(s => s.Type == PatternSegment.Dot ? 0 : Math.Abs(s.LengthMm)).ToList();

    public static double NormalizeAngle(double degrees)
    {
        var a = degrees % 360;
        if (a < 0) a += 360;
        return Math.Abs(a - 360) < AngleToleranceDeg ? 0 : a;
    }

    // ── Comparison ───────────────────────────────────────────────────────────

    public static bool SameSegments(IReadOnlyList<PatternSegment> a, IReadOnlyList<PatternSegment> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Type != b[i].Type) return false;
            if (Math.Abs(a[i].LengthMm - b[i].LengthMm) > LengthToleranceMm) return false;
        }
        return true;
    }

    public static bool SameGrids(IReadOnlyList<FillGridDefinition> a, IReadOnlyList<FillGridDefinition> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            var x = a[i];
            var y = b[i];
            var angle = Math.Abs(NormalizeAngle(x.AngleDeg) - NormalizeAngle(y.AngleDeg));
            if (Math.Min(angle, 360 - angle) > AngleToleranceDeg) return false;
            if (Math.Abs(x.OriginXMm - y.OriginXMm) > LengthToleranceMm || Math.Abs(x.OriginYMm - y.OriginYMm) > LengthToleranceMm) return false;
            if (Math.Abs(x.OffsetMm - y.OffsetMm) > LengthToleranceMm) return false;
            if (x.Segments.Count > 0 && Math.Abs(x.ShiftMm - y.ShiftMm) > LengthToleranceMm) return false;
            if (!SameSegments(x.Segments, y.Segments)) return false;
        }
        return true;
    }

    // ── Output ───────────────────────────────────────────────────────────────

    public static object DescribeSegments(IEnumerable<PatternSegment> segments) =>
        segments.Select(s => s.Type == PatternSegment.Dot
            ? (object)new { type = s.Type }
            : new { type = s.Type, lengthMm = Round(s.LengthMm) }).ToList();

    public static object DescribeGrid(FillGridDefinition grid) => new
    {
        angle = Round(grid.AngleDeg),
        origin = new[] { Round(grid.OriginXMm), Round(grid.OriginYMm) },
        offset = Round(grid.OffsetMm),
        shift = Round(grid.ShiftMm),
        segments = grid.Segments.Count > 0 ? DescribeSegments(grid.Segments) : null
    };

    /// <summary>A short text such as "dash 3, space 1.5, dot, space 1.5".</summary>
    public static string SegmentText(IEnumerable<PatternSegment> segments) =>
        string.Join(", ", segments.Select(s => s.Type == PatternSegment.Dot ? "dot" : $"{s.Type} {Round(s.LengthMm).ToString(CultureInfo.InvariantCulture)}"));

    public static string LineSummary(IReadOnlyList<LinePatternDefinition> definitions, int invalid, string document)
    {
        var list = string.Join("; ", definitions.Take(8).Select(d => $"'{d.Name}' ({SegmentText(d.Segments)} mm)"));
        return $"Create or replace {definitions.Count} line pattern(s) in {(document.Length > 0 ? $"'{document}'" : "the active document")}: {list}" +
               $"{(definitions.Count > 8 ? ", …" : string.Empty)}. Replacing a pattern changes every line style that uses it." +
               $"{(invalid > 0 ? $" The request has {invalid} invalid item(s)." : string.Empty)}";
    }

    public static string FillSummary(IReadOnlyList<FillPatternDefinition> definitions, int invalid, string document)
    {
        var list = string.Join("; ", definitions.Take(8).Select(d =>
            $"'{d.Name}' ({d.Target}, " + (d.Simple
                ? $"{(d.Grids.Count > 1 ? "crosshatch" : "hatch")} {Round(d.Grids[0].AngleDeg).ToString(CultureInfo.InvariantCulture)}° every {Round(d.Grids[0].OffsetMm).ToString(CultureInfo.InvariantCulture)} mm"
                : $"{d.Grids.Count} grid(s)") + ")"));
        return $"Create or replace {definitions.Count} fill pattern(s) in {(document.Length > 0 ? $"'{document}'" : "the active document")}: {list}" +
               $"{(definitions.Count > 8 ? ", …" : string.Empty)}. Replacing a pattern changes every region, material and filter that uses it." +
               $"{(invalid > 0 ? $" The request has {invalid} invalid item(s)." : string.Empty)}";
    }

    public static double Round(double value) => Math.Round(value, 4);

    // ── Helpers ──────────────────────────────────────────────────────────────

    public static string? CheckName(string name, string what)
    {
        if (name != name.Trim()) return $"The {what} name '{name}' starts or ends with a space.";
        var bad = name.Where(c => ForbiddenNameCharacters.IndexOf(c) >= 0 || char.IsControl(c)).Distinct().ToList();
        if (bad.Count > 0) return $"The {what} name '{name}' contains characters Revit refuses: {string.Join(" ", bad)}.";
        if (name.Length > 200) return $"The {what} name is longer than 200 characters.";
        return null;
    }

    private static string? CheckLength(double mm, string where)
    {
        if (double.IsNaN(mm) || double.IsInfinity(mm) || mm < MinLengthMm)
            return $"{where} must be at least {MinLengthMm.ToString(CultureInfo.InvariantCulture)} mm.";
        if (mm > MaxLengthMm) return $"{where} must be at most {MaxLengthMm.ToString(CultureInfo.InvariantCulture)} mm.";
        return null;
    }

    private static IEnumerable<JObject?> Items(IReadOnlyDictionary<string, object?> args, string key, List<string> errors)
    {
        if (!args.TryGetValue(key, out var raw) || raw == null) return Array.Empty<JObject?>();
        JToken? token;
        try
        {
            token = raw switch
            {
                JToken t => t,
                string s when s.Trim().Length == 0 => null,
                string s => JToken.Parse(s),
                _ => JToken.FromObject(raw)
            };
        }
        catch (Exception)
        {
            errors.Add($"{key} is not valid JSON.");
            return Array.Empty<JObject?>();
        }
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
            else { errors.Add($"{key}[{index}] must be an object."); list.Add(null); }
            index++;
        }
        return list;
    }

    private static bool Given(JToken? token) => token != null && token.Type != JTokenType.Null;

    private static string Text(JToken? token) =>
        token == null || token.Type == JTokenType.Null ? string.Empty : (token.Type == JTokenType.String ? (string?)token ?? string.Empty : token.ToString()).Trim();

    private static double? Num(JToken? token)
    {
        if (token == null) return null;
        double? value = null;
        if (token.Type is JTokenType.Integer or JTokenType.Float) value = (double)token;
        else if (token.Type == JTokenType.String
                 && double.TryParse((string?)token, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) value = v;
        return value is { } d && !double.IsNaN(d) && !double.IsInfinity(d) ? d : null;
    }
}
