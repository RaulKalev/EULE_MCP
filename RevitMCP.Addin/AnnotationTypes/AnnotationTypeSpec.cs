using System.Globalization;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.AnnotationTypes;

/// <summary>How a field of a text or dimension type is given and compared.</summary>
public enum AnnotationFieldKind
{
    /// <summary>A string, such as a font name.</summary>
    Text,
    /// <summary>Paper millimetres; Revit stores feet.</summary>
    Length,
    /// <summary>A plain ratio, such as the width factor.</summary>
    Factor,
    /// <summary>true / false; Revit stores 1 / 0.</summary>
    Bool,
    /// <summary>#RRGGBB; Revit stores red + green * 256 + blue * 65536.</summary>
    Colour,
    /// <summary>Revit's line weight number, 1 to 16.</summary>
    LineWeight,
    /// <summary>An arrowhead type name; empty = none.</summary>
    Arrowhead,
    /// <summary>opaque | transparent; Revit stores 0 / 1.</summary>
    Background,
    /// <summary>{useProjectSettings?, unit?, accuracy?}: the format of a dimension type's value.</summary>
    UnitsFormat
}

/// <summary>One field a text or dimension type spec can give.</summary>
public sealed record AnnotationTypeField(string Key, AnnotationFieldKind Kind, string[] Aliases, bool AllowZero = false);

/// <summary>The format of a dimension type's value. Unit is a canonical unit name (millimeters, degrees, …).</summary>
public sealed record UnitsFormatValue(bool UseProjectSettings, string? Unit, double? Accuracy)
{
    public override string ToString() => UseProjectSettings
        ? "project settings"
        : $"{Unit ?? "?"}{(Accuracy != null ? $", accuracy {Accuracy.Value.ToString("0.########", CultureInfo.InvariantCulture)}" : string.Empty)}";
}

/// <summary>unitsFormat as given: a null field is not given.</summary>
public sealed class UnitsFormatSpec
{
    public bool? UseProjectSettings { get; set; }
    public string? Unit { get; set; }
    public double? Accuracy { get; set; }
}

/// <summary>A text or dimension type to create (a name the document lacks) or edit (a name it has).</summary>
public sealed class AnnotationTypeSpec
{
    /// <summary>Where it came from in the request, for messages: types[0], …</summary>
    public string Source { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? DuplicateFrom { get; set; }
    public string? NewName { get; set; }
    /// <summary>The dimension style (linear, angular, …) as given; null = any.</summary>
    public string? Style { get; set; }
    /// <summary>Field key to plain value: string, double, bool, int or <see cref="UnitsFormatSpec"/>.</summary>
    public Dictionary<string, object> Values { get; } = new(StringComparer.Ordinal);

    public bool HasValues => Values.Count > 0;
}

/// <summary>A field that changes: from (the type's current value, or the source's for a new type) and to.</summary>
public sealed record AnnotationFieldChange(string Field, object? From, object? To);

/// <summary>
/// Specs of revit_set_text_types and revit_set_dimension_types, value conversions and the from/to plan.
/// Pure: no Revit types, unit tested (AnnotationTypeSpecTests).
/// </summary>
public static class AnnotationTypeSpecParser
{
    public const int MaxTypes = 100;
    public const double MmPerFoot = 304.8;
    private const double Tolerance = 1e-6;

    /// <summary>Characters Revit refuses in element names.</summary>
    private const string ForbiddenNameCharacters = "\\:{}[]|;<>?`~";

    public static readonly IReadOnlyList<AnnotationTypeField> TextTypeFields = new[]
    {
        new AnnotationTypeField("font", AnnotationFieldKind.Text, new[] { "textFont" }),
        new AnnotationTypeField("size", AnnotationFieldKind.Length, new[] { "textSize" }),
        new AnnotationTypeField("widthFactor", AnnotationFieldKind.Factor, new[] { "textWidthFactor" }),
        new AnnotationTypeField("bold", AnnotationFieldKind.Bool, Array.Empty<string>()),
        new AnnotationTypeField("italic", AnnotationFieldKind.Bool, Array.Empty<string>()),
        new AnnotationTypeField("underline", AnnotationFieldKind.Bool, Array.Empty<string>()),
        new AnnotationTypeField("color", AnnotationFieldKind.Colour, new[] { "colour" }),
        new AnnotationTypeField("background", AnnotationFieldKind.Background, new[] { "textBackground" }),
        new AnnotationTypeField("showBorder", AnnotationFieldKind.Bool, Array.Empty<string>()),
        new AnnotationTypeField("leaderBorderOffset", AnnotationFieldKind.Length, new[] { "leaderOffset" }, AllowZero: true),
        new AnnotationTypeField("tabSize", AnnotationFieldKind.Length, Array.Empty<string>()),
        new AnnotationTypeField("lineWeight", AnnotationFieldKind.LineWeight, Array.Empty<string>()),
        new AnnotationTypeField("leaderArrowhead", AnnotationFieldKind.Arrowhead, new[] { "arrowhead" })
    };

    public static readonly IReadOnlyList<AnnotationTypeField> DimensionTypeFields = new[]
    {
        new AnnotationTypeField("textSize", AnnotationFieldKind.Length, new[] { "size" }),
        new AnnotationTypeField("textFont", AnnotationFieldKind.Text, new[] { "font" }),
        new AnnotationTypeField("textWidthFactor", AnnotationFieldKind.Factor, new[] { "widthFactor" }),
        new AnnotationTypeField("bold", AnnotationFieldKind.Bool, Array.Empty<string>()),
        new AnnotationTypeField("italic", AnnotationFieldKind.Bool, Array.Empty<string>()),
        new AnnotationTypeField("underline", AnnotationFieldKind.Bool, Array.Empty<string>()),
        new AnnotationTypeField("textOffset", AnnotationFieldKind.Length, Array.Empty<string>(), AllowZero: true),
        new AnnotationTypeField("textBackground", AnnotationFieldKind.Background, new[] { "background" }),
        new AnnotationTypeField("color", AnnotationFieldKind.Colour, new[] { "colour" }),
        new AnnotationTypeField("lineWeight", AnnotationFieldKind.LineWeight, Array.Empty<string>()),
        new AnnotationTypeField("tickMark", AnnotationFieldKind.Arrowhead, Array.Empty<string>()),
        new AnnotationTypeField("tickMarkLineWeight", AnnotationFieldKind.LineWeight, Array.Empty<string>()),
        new AnnotationTypeField("witnessLineExtension", AnnotationFieldKind.Length, Array.Empty<string>(), AllowZero: true),
        new AnnotationTypeField("witnessLineGap", AnnotationFieldKind.Length, new[] { "witnessLineGapToElement" }, AllowZero: true),
        new AnnotationTypeField("dimensionLineExtension", AnnotationFieldKind.Length, Array.Empty<string>(), AllowZero: true),
        new AnnotationTypeField("unitsFormat", AnnotationFieldKind.UnitsFormat, Array.Empty<string>())
    };

    /// <summary>Unit names unitsFormat.unit accepts, to the canonical name.</summary>
    private static readonly Dictionary<string, string> UnitNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mm"] = "millimeters", ["millimeter"] = "millimeters", ["millimeters"] = "millimeters", ["millimetre"] = "millimeters", ["millimetres"] = "millimeters",
        ["cm"] = "centimeters", ["centimeter"] = "centimeters", ["centimeters"] = "centimeters", ["centimetre"] = "centimeters", ["centimetres"] = "centimeters",
        ["dm"] = "decimeters", ["decimeter"] = "decimeters", ["decimeters"] = "decimeters", ["decimetre"] = "decimeters", ["decimetres"] = "decimeters",
        ["m"] = "meters", ["meter"] = "meters", ["meters"] = "meters", ["metre"] = "meters", ["metres"] = "meters",
        ["ft"] = "feet", ["foot"] = "feet", ["feet"] = "feet",
        ["in"] = "inches", ["inch"] = "inches", ["inches"] = "inches",
        ["deg"] = "degrees", ["degree"] = "degrees", ["degrees"] = "degrees", ["°"] = "degrees"
    };

    public static IReadOnlyCollection<string> CanonicalUnits => UnitNames.Values.Distinct().ToList();

    /// <summary>
    /// Reads [{name, duplicateFrom?, newName?, style?, …fields}] against <paramref name="fields"/>.
    /// <paramref name="allowStyle"/> is true for dimension types.
    /// </summary>
    public static List<AnnotationTypeSpec> Read(IReadOnlyDictionary<string, object?> args, string key,
        IReadOnlyList<AnnotationTypeField> fields, bool allowStyle, List<string> errors)
    {
        var result = new List<AnnotationTypeSpec>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in Items(args, key, errors))
        {
            var source = $"{key}[{index++}]";
            if (item == null) continue;
            var name = Text(item["name"]);
            if (name.Length == 0) { errors.Add($"{source} needs a name."); continue; }
            var nameError = CheckTypeName(name);
            if (nameError != null) { errors.Add($"{source}: {nameError}"); continue; }

            var spec = new AnnotationTypeSpec { Source = source, Name = name };
            var ok = true;

            var duplicateFrom = OptionalText(item["duplicateFrom"]);
            if (duplicateFrom is { Length: > 0 }) spec.DuplicateFrom = duplicateFrom;

            var newName = OptionalText(item["newName"]);
            if (newName is { Length: > 0 })
            {
                var newNameError = CheckTypeName(newName);
                if (newNameError != null) { errors.Add($"{source}.newName: {newNameError}"); ok = false; }
                else spec.NewName = newName;
            }

            var style = OptionalText(item["style"]);
            if (style is { Length: > 0 })
            {
                if (allowStyle) spec.Style = style;
                else { errors.Add($"{source}.style applies to dimension types only."); ok = false; }
            }

            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "name", "duplicateFrom", "newName", "style" };
            foreach (var field in fields)
            {
                known.Add(field.Key);
                foreach (var alias in field.Aliases) known.Add(alias);
                var token = item[field.Key] ?? field.Aliases.Select(a => item[a]).FirstOrDefault(t => t != null);
                if (token == null || token.Type == JTokenType.Null) continue;
                var value = ReadValue(field, token, $"{source}.{field.Key}", errors);
                if (value == null) ok = false;
                else spec.Values[field.Key] = value;
            }

            var unknown = item.Properties().Select(p => p.Name).Where(n => !known.Contains(n)).ToList();
            if (unknown.Count > 0)
            {
                errors.Add($"{source}: unknown field(s) {string.Join(", ", unknown.Select(u => $"'{u}'"))}. " +
                           $"Fields: {string.Join(", ", fields.Select(f => f.Key))}.");
                ok = false;
            }

            var identity = (spec.Style != null ? NormalizeStyle(spec.Style) + "|" : string.Empty) + name;
            if (!seen.Add(identity)) { errors.Add($"{source}: '{name}' is listed twice."); continue; }
            if (ok) result.Add(spec);
        }
        if (result.Count > MaxTypes)
            errors.Add($"{result.Count} types were given; at most {MaxTypes} per call.");
        return result;
    }

    private static object? ReadValue(AnnotationTypeField field, JToken token, string where, List<string> errors)
    {
        switch (field.Kind)
        {
            case AnnotationFieldKind.Text:
            {
                var text = Text(token);
                if (text.Length > 0) return text;
                errors.Add($"{where} must not be empty.");
                return null;
            }
            case AnnotationFieldKind.Length:
            {
                var number = Num(token);
                if (number != null && (field.AllowZero ? number >= 0 : number > 0) && number <= 1000) return number.Value;
                errors.Add($"{where} must be {(field.AllowZero ? "zero or more" : "more than zero")} and at most 1000: paper millimetres.");
                return null;
            }
            case AnnotationFieldKind.Factor:
            {
                var number = Num(token);
                if (number is >= 0.01 and <= 100) return number.Value;
                errors.Add($"{where} must be a number from 0.01 to 100 (1 = normal width).");
                return null;
            }
            case AnnotationFieldKind.Bool:
            {
                var flag = Bool(token);
                if (flag != null) return flag.Value;
                errors.Add($"{where} must be true or false.");
                return null;
            }
            case AnnotationFieldKind.Colour:
                return Colour(token, where, errors);
            case AnnotationFieldKind.LineWeight:
            {
                var number = Num(token);
                if (number is >= 1 and <= 16 && Math.Abs(number.Value - Math.Round(number.Value)) < 1e-9) return (int)Math.Round(number.Value);
                errors.Add($"{where} must be a whole number from 1 to 16: Revit's line weight number, not millimetres.");
                return null;
            }
            case AnnotationFieldKind.Arrowhead:
            {
                var text = Text(token);
                return text.Length == 0 || text.Equals("none", StringComparison.OrdinalIgnoreCase) ? string.Empty : text;
            }
            case AnnotationFieldKind.Background:
            {
                var text = Text(token).ToLowerInvariant();
                if (text is "opaque" or "transparent") return text;
                errors.Add($"{where} must be opaque or transparent.");
                return null;
            }
            case AnnotationFieldKind.UnitsFormat:
                return UnitsFormat(token, where, errors);
            default:
                return null;
        }
    }

    private static UnitsFormatSpec? UnitsFormat(JToken token, string where, List<string> errors)
    {
        if (token.Type == JTokenType.String)
        {
            var text = Text(token);
            if (text.Equals("project", StringComparison.OrdinalIgnoreCase) || text.Equals("default", StringComparison.OrdinalIgnoreCase))
                return new UnitsFormatSpec { UseProjectSettings = true };
        }
        if (token is not JObject obj)
        {
            errors.Add($"{where} must be {{useProjectSettings?, unit?, accuracy?}} or \"project\".");
            return null;
        }

        var spec = new UnitsFormatSpec();
        var ok = true;
        var useToken = obj["useProjectSettings"] ?? obj["useDefault"];
        if (useToken != null && useToken.Type != JTokenType.Null)
        {
            spec.UseProjectSettings = Bool(useToken);
            if (spec.UseProjectSettings == null) { errors.Add($"{where}.useProjectSettings must be true or false."); ok = false; }
        }
        var unitToken = obj["unit"];
        if (unitToken != null && unitToken.Type != JTokenType.Null)
        {
            var unit = NormalizeUnit(Text(unitToken));
            if (unit == null)
            {
                errors.Add($"{where}.unit '{Text(unitToken)}' is not a unit here: use {string.Join(", ", CanonicalUnits)}.");
                ok = false;
            }
            spec.Unit = unit;
        }
        var accuracyToken = obj["accuracy"] ?? obj["rounding"];
        if (accuracyToken != null && accuracyToken.Type != JTokenType.Null)
        {
            var accuracy = Num(accuracyToken);
            if (accuracy is > 0 and <= 1000) spec.Accuracy = accuracy;
            else { errors.Add($"{where}.accuracy must be a positive number, such as 1, 0.5 or 0.01 (rounding in the unit)."); ok = false; }
        }
        if (ok && spec.UseProjectSettings == true && (spec.Unit != null || spec.Accuracy != null))
        {
            errors.Add($"{where}: useProjectSettings=true takes no unit or accuracy.");
            ok = false;
        }
        if (ok && spec.UseProjectSettings == null && spec.Unit == null && spec.Accuracy == null)
        {
            errors.Add($"{where} gives nothing: use useProjectSettings, unit or accuracy.");
            ok = false;
        }
        return ok ? spec : null;
    }

    /// <summary>The canonical unit name (millimeters, degrees, …) or null.</summary>
    public static string? NormalizeUnit(string text) => UnitNames.TryGetValue(text.Trim(), out var unit) ? unit : null;

    /// <summary>The unit name of a Revit unit type id such as "autodesk.unit.unit:millimeters-1.0.1".</summary>
    public static string UnitNameFromTypeId(string typeId)
    {
        var start = typeId.LastIndexOf(':');
        var name = start >= 0 ? typeId.Substring(start + 1) : typeId;
        var dash = name.IndexOf('-');
        return dash > 0 ? name.Substring(0, dash) : name;
    }

    /// <summary>A dimension style name compared without case, spaces, hyphens or underscores: "Arc Length" = "arcLength".</summary>
    public static string NormalizeStyle(string style) =>
        new string(style.Where(c => !char.IsWhiteSpace(c) && c != '-' && c != '_').ToArray()).ToLowerInvariant();

    public static bool SameStyle(string a, string b) => NormalizeStyle(a) == NormalizeStyle(b);

    public static string? CheckTypeName(string name)
    {
        if (name != name.Trim()) return $"The type name '{name}' starts or ends with a space.";
        var bad = name.Where(c => ForbiddenNameCharacters.IndexOf(c) >= 0 || char.IsControl(c)).Distinct().ToList();
        if (bad.Count > 0) return $"The type name '{name}' contains characters Revit refuses: {string.Join(" ", bad)}.";
        if (name.Length > 200) return "The type name is longer than 200 characters.";
        return null;
    }

    // ── Conversions ──────────────────────────────────────────────────────────

    /// <summary>Revit's colour integer (red + green * 256 + blue * 65536) as #RRGGBB.</summary>
    public static string ColourFromRevit(int value) =>
        $"#{value & 0xFF:X2}{(value >> 8) & 0xFF:X2}{(value >> 16) & 0xFF:X2}";

    /// <summary>#RRGGBB as Revit's colour integer.</summary>
    public static int ColourToRevit(string hex)
    {
        var text = hex.TrimStart('#');
        var r = Convert.ToInt32(text.Substring(0, 2), 16);
        var g = Convert.ToInt32(text.Substring(2, 2), 16);
        var b = Convert.ToInt32(text.Substring(4, 2), 16);
        return r + (g << 8) + (b << 16);
    }

    public static double FeetToMm(double feet) => Math.Round(feet * MmPerFoot, 6);
    public static double MmToFeet(double mm) => mm / MmPerFoot;

    public static string BackgroundFromRevit(int value) => value == 0 ? "opaque" : "transparent";
    public static int BackgroundToRevit(string value) => value == "opaque" ? 0 : 1;

    // ── Planning ─────────────────────────────────────────────────────────────

    /// <summary>
    /// What the spec changes, field by field: <paramref name="current"/> holds the plain values of the type being edited
    /// (or of the type a new one is duplicated from); a field the spec gives is a change when it differs.
    /// </summary>
    public static List<AnnotationFieldChange> Changes(AnnotationTypeSpec spec, IReadOnlyList<AnnotationTypeField> fields,
        IReadOnlyDictionary<string, object?> current)
    {
        var changes = new List<AnnotationFieldChange>();
        foreach (var field in fields)
        {
            if (!spec.Values.TryGetValue(field.Key, out var to)) continue;
            current.TryGetValue(field.Key, out var from);
            if (field.Kind == AnnotationFieldKind.UnitsFormat)
            {
                var merged = MergeUnits((UnitsFormatSpec)to, from as UnitsFormatValue);
                if (!Equals(merged, from as UnitsFormatValue)) changes.Add(new AnnotationFieldChange(field.Key, from?.ToString(), merged.ToString()));
                continue;
            }
            if (!SameValue(field.Kind, from, to)) changes.Add(new AnnotationFieldChange(field.Key, Display(field.Kind, from), Display(field.Kind, to)));
        }
        return changes;
    }

    /// <summary>
    /// The format a type gets: useProjectSettings=true is the project's format; a unit or accuracy turns project settings
    /// off and keeps the other from <paramref name="current"/>.
    /// </summary>
    public static UnitsFormatValue MergeUnits(UnitsFormatSpec spec, UnitsFormatValue? current)
    {
        if (spec.UseProjectSettings == true) return new UnitsFormatValue(true, null, null);
        return new UnitsFormatValue(false, spec.Unit ?? current?.Unit, spec.Accuracy ?? current?.Accuracy);
    }

    public static bool SameValue(AnnotationFieldKind kind, object? a, object? b)
    {
        if (a == null || b == null) return a == null && b == null;
        return kind switch
        {
            AnnotationFieldKind.Length or AnnotationFieldKind.Factor => Math.Abs(Convert.ToDouble(a, CultureInfo.InvariantCulture) - Convert.ToDouble(b, CultureInfo.InvariantCulture)) < Tolerance,
            AnnotationFieldKind.Text or AnnotationFieldKind.Colour or AnnotationFieldKind.Arrowhead or AnnotationFieldKind.Background =>
                string.Equals(Convert.ToString(a, CultureInfo.InvariantCulture), Convert.ToString(b, CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase),
            _ => Equals(a, b)
        };
    }

    /// <summary>A plain value as it is shown: an arrowhead "" is none, lengths rounded.</summary>
    public static object? Display(AnnotationFieldKind kind, object? value) => value switch
    {
        null => null,
        string s when kind == AnnotationFieldKind.Arrowhead && s.Length == 0 => "none",
        double d => Math.Round(d, 4),
        UnitsFormatValue u => u.ToString(),
        _ => value
    };

    /// <summary>Close names for a type that was not found.</summary>
    public static List<string> Suggest(string requested, IEnumerable<string> names, int max = 5)
    {
        var needle = requested.Trim();
        return names
            .Where(n => n.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 || needle.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0
                        || SharedPrefix(n, needle) >= Math.Min(3, needle.Length)
                        || n.Split(' ', '-', '_').Any(w => w.Length > 2 && needle.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(n => SharedPrefix(n, needle))
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
    }

    public static string DidYouMean(string requested, IEnumerable<string> names)
    {
        var close = Suggest(requested, names);
        return close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(n => $"'{n}'"))}?" : ".";
    }

    private static int SharedPrefix(string a, string b)
    {
        var i = 0;
        while (i < a.Length && i < b.Length && char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[i])) i++;
        return i;
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

    private static string Text(JToken? token) =>
        token == null || token.Type == JTokenType.Null ? string.Empty
        : token.Type == JTokenType.String ? ((string?)token ?? string.Empty).Trim()
        : token.ToString(Newtonsoft.Json.Formatting.None).Trim();

    private static string? OptionalText(JToken? token) =>
        token == null || token.Type == JTokenType.Null ? null : Text(token);

    private static double? Num(JToken? token)
    {
        if (token == null) return null;
        if (token.Type is JTokenType.Integer or JTokenType.Float) return (double)token;
        if (token.Type == JTokenType.String
            && double.TryParse((string?)token, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return v;
        return null;
    }

    private static bool? Bool(JToken token)
    {
        if (token.Type == JTokenType.Boolean) return (bool)token;
        if (token.Type == JTokenType.Integer) return (long)token switch { 0 => false, 1 => true, _ => null };
        var text = Text(token).ToLowerInvariant();
        return text switch { "true" or "yes" or "1" => true, "false" or "no" or "0" => false, _ => null };
    }

    /// <summary>"#RRGGBB", "r,g,b", [r,g,b] or {r,g,b} as #RRGGBB; null (with an error) when it is not a colour.</summary>
    private static string? Colour(JToken token, string where, List<string> errors)
    {
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
        return null;
    }

    private static bool IsChannel(double? value) => value is >= 0 and <= 255 && Math.Abs(value.Value - Math.Round(value.Value)) < 1e-9;

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
