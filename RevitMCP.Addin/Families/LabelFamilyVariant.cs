using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Families;

/// <summary>The text appearance of a label: the fields of its text type. A null field is not given.</summary>
public sealed class LabelTextAppearance
{
    /// <summary>Text size on paper, mm.</summary>
    public double? SizeMm { get; set; }
    public string? Font { get; set; }
    public bool? Bold { get; set; }
    public bool? Italic { get; set; }
    public bool? Underline { get; set; }
    public double? WidthFactor { get; set; }
    /// <summary>#RRGGBB.</summary>
    public string? Color { get; set; }

    public bool HasValues => SizeMm != null || Font != null || Bold != null || Italic != null || Underline != null
                             || WidthFactor != null || Color != null;

    /// <summary>"size 2.5 mm, font Arial, bold": the fields given, for messages.</summary>
    public string Describe() => string.Join(", ", new[]
    {
        SizeMm != null ? $"size {SizeMm.Value.ToString("0.###", CultureInfo.InvariantCulture)} mm" : null,
        Font != null ? $"font {Font}" : null,
        Bold != null ? (Bold.Value ? "bold" : "not bold") : null,
        Italic != null ? (Italic.Value ? "italic" : "not italic") : null,
        Underline != null ? (Underline.Value ? "underlined" : "not underlined") : null,
        WidthFactor != null ? $"width factor {WidthFactor.Value.ToString("0.###", CultureInfo.InvariantCulture)}" : null,
        Color != null ? $"colour {Color}" : null
    }.Where(s => s != null));
}

/// <summary>A family type the variant has, with the label appearance asked for in it.</summary>
public sealed class LabelFamilyTypeSpec
{
    public string Source { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public LabelTextAppearance Appearance { get; set; } = new();
}

/// <summary>What revit_create_label_family makes: a copy of a seed label family with other types, text and parameter.</summary>
public sealed class LabelFamilyVariantSpec
{
    /// <summary>The seed: the name of a family loaded in the document, or a .rfa path.</summary>
    public string Seed { get; set; } = string.Empty;
    public bool SeedIsPath => Seed.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase);
    public string SeedName => SeedIsPath ? Path.GetFileNameWithoutExtension(Seed) : Seed;

    public string SavePath { get; set; } = string.Empty;
    public string FamilyName { get; set; } = string.Empty;
    public bool Overwrite { get; set; }

    /// <summary>The variant's types; empty = the seed's types stay as they are.</summary>
    public List<LabelFamilyTypeSpec> Types { get; set; } = new();
    /// <summary>The label text appearance, merged over every type (the family has one label text type).</summary>
    public LabelTextAppearance Appearance { get; set; } = new();

    /// <summary>The label family's parameter the changes apply to; empty = renameParameter.from, else the only one.</summary>
    public string Parameter { get; set; } = string.Empty;
    public string RenameTo { get; set; } = string.Empty;
    /// <summary>true = make it an instance parameter, false = a type parameter, null = leave it.</summary>
    public bool? MakeInstance { get; set; }
    /// <summary>The parameter's value in every type; null = leave it.</summary>
    public string? SampleValue { get; set; }

    public bool LoadIntoProject { get; set; }
    public string IfExists { get; set; } = "skip";

    /// <summary>Whether any change needs the label's parameter.</summary>
    public bool ChangesParameter => RenameTo.Length > 0 || MakeInstance != null || SampleValue != null;
}

/// <summary>A user-defined family parameter of the seed, as the planner sees it.</summary>
public sealed class LabelFamilyParameterInfo
{
    public string Name { get; set; } = string.Empty;
    public bool IsInstance { get; set; }
    public bool IsShared { get; set; }
    /// <summary>text, integer, length, … or Revit's label for the data type.</summary>
    public string DataType { get; set; } = string.Empty;
    public string? Formula { get; set; }
    public bool IsReadOnly { get; set; }

    public bool Editable => !IsReadOnly && string.IsNullOrEmpty(Formula);
}

/// <summary>What happens to one family type of the variant.</summary>
public sealed class LabelFamilyTypeAction
{
    public string Name { get; set; } = string.Empty;
    /// <summary>keep | rename (same name, other case) | create | delete.</summary>
    public string Action { get; set; } = string.Empty;
    /// <summary>The seed's name of a type that is kept or renamed.</summary>
    public string? SeedName { get; set; }
}

/// <summary>
/// Arguments of revit_list_label_families and revit_(preview_)create_label_family, and the plans that need no Revit.
/// Pure: no Revit types, unit tested (LabelFamilyVariantTests).
/// </summary>
public static class LabelFamilyVariantParser
{
    public const int MaxTypes = 50;
    public const int DefaultInspectLimit = 5;
    public const int MaxInspectLimit = 25;
    public const double MaxTextSizeMm = 100;
    public const double MinWidthFactor = 0.01;
    public const double MaxWidthFactor = 10;

    /// <summary>Characters Revit refuses in type and parameter names.</summary>
    private const string ForbiddenNameCharacters = "\\:{}[]|;<>?`~";

    // ── revit_create_label_family ───────────────────────────────────────────

    public static LabelFamilyVariantSpec Parse(IReadOnlyDictionary<string, object?> args, List<string> errors)
    {
        var spec = new LabelFamilyVariantSpec
        {
            Seed = Str(args, "seed"),
            FamilyName = Str(args, "familyName"),
            Overwrite = Bool(args, "overwrite", false),
            LoadIntoProject = Bool(args, "loadIntoProject", false),
            IfExists = Str(args, "ifExists", "skip").ToLowerInvariant(),
            Parameter = Str(args, "parameter")
        };

        if (spec.Seed.Length == 0)
            errors.Add("seed is required: the label family to copy, a Generic Annotation family loaded in the document (by name) or a .rfa path. revit_list_label_families lists them.");
        else if (spec.SeedIsPath && !Path.IsPathRooted(spec.Seed))
            errors.Add($"seed '{spec.Seed}' must be an absolute .rfa path.");

        var (savePath, familyName, pathError) = DetailFamilySpecParser.ResolveSavePath(Str(args, "savePath"), spec.FamilyName);
        if (pathError != null) errors.Add(pathError);
        spec.SavePath = savePath ?? string.Empty;
        spec.FamilyName = familyName ?? spec.FamilyName;
        if (spec.Seed.Length > 0 && spec.FamilyName.Length > 0 && string.Equals(spec.SeedName, spec.FamilyName, StringComparison.OrdinalIgnoreCase))
            errors.Add($"The variant needs a name of its own: familyName '{spec.FamilyName}' is the seed's name. The seed is never changed.");
        if (spec.SeedIsPath && spec.SavePath.Length > 0 && SamePath(spec.Seed, spec.SavePath))
            errors.Add("savePath is the seed's own file; the seed is never changed.");

        if (spec.IfExists != "skip" && spec.IfExists != "overwrite")
            errors.Add($"ifExists '{spec.IfExists}' is not supported: skip | overwrite.");

        ReadTypes(args, spec, errors);
        ReadRename(args, spec, errors);

        if (args.TryGetValue("makeInstance", out var rawInstance) && rawInstance != null)
        {
            var flag = Flag(ToToken(rawInstance));
            if (flag == null) errors.Add("makeInstance must be true (instance parameter) or false (type parameter).");
            spec.MakeInstance = flag;
        }

        if (args.TryGetValue("sampleValue", out var rawSample) && rawSample != null)
        {
            var token = ToToken(rawSample);
            if (token != null && token.Type != JTokenType.Null)
                spec.SampleValue = token.Type == JTokenType.String ? (string?)token ?? string.Empty : token.ToString(Newtonsoft.Json.Formatting.None);
        }
        return spec;
    }

    private static void ReadTypes(IReadOnlyDictionary<string, object?> args, LabelFamilyVariantSpec spec, List<string> errors)
    {
        if (!args.TryGetValue("types", out var raw) || raw == null) return;
        var token = ToToken(raw);
        if (token == null || token.Type == JTokenType.Null) return;
        if (token is not JArray array) { errors.Add("types must be an array of {name, textSize?, font?, bold?, italic?, underline?, widthFactor?, color?}."); return; }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < array.Count; i++)
        {
            var source = $"types[{i}]";
            if (array[i] is not JObject item)
            {
                // A plain string is a type name with nothing else.
                if (array[i].Type == JTokenType.String) item = new JObject { ["name"] = array[i] };
                else { errors.Add($"{source} must be an object."); continue; }
            }
            var name = Text(item["name"]);
            if (name.Length == 0) { errors.Add($"{source} needs a name."); continue; }
            var nameError = CheckName(name, "type");
            if (nameError != null) { errors.Add($"{source}: {nameError}"); continue; }
            if (!seen.Add(name)) { errors.Add($"{source}: the type '{name}' is listed twice."); continue; }

            var appearance = new LabelTextAppearance();
            var ok = true;
            var size = item["textSize"] ?? item["size"];
            if (size != null && size.Type != JTokenType.Null)
            {
                var number = Num(size);
                if (number is > 0 and <= MaxTextSizeMm) appearance.SizeMm = number;
                else { errors.Add($"{source}.textSize must be a size on paper in mm, above 0 and at most {MaxTextSizeMm} (2.5 is common)."); ok = false; }
            }
            var font = item["font"];
            if (font != null && font.Type != JTokenType.Null)
            {
                var text = Text(font);
                if (text.Length > 0) appearance.Font = text;
                else { errors.Add($"{source}.font is empty."); ok = false; }
            }
            appearance.Bold = ReadFlag(item["bold"], source + ".bold", errors, ref ok);
            appearance.Italic = ReadFlag(item["italic"], source + ".italic", errors, ref ok);
            appearance.Underline = ReadFlag(item["underline"], source + ".underline", errors, ref ok);
            var width = item["widthFactor"];
            if (width != null && width.Type != JTokenType.Null)
            {
                var number = Num(width);
                if (number is >= MinWidthFactor and <= MaxWidthFactor) appearance.WidthFactor = number;
                else { errors.Add($"{source}.widthFactor must be a number from {MinWidthFactor} to {MaxWidthFactor} (1 = normal width)."); ok = false; }
            }
            appearance.Color = Colour(item["color"] ?? item["colour"], source + ".color", errors, ref ok);

            if (ok) spec.Types.Add(new LabelFamilyTypeSpec { Source = source, Name = name, Appearance = appearance });
        }
        if (spec.Types.Count > MaxTypes) errors.Add($"{spec.Types.Count} types were given; at most {MaxTypes}.");
        spec.Appearance = MergeAppearance(spec.Types, errors);
    }

    /// <summary>
    /// The one text appearance of the variant. A label family holds one label with one text type, so every type shows
    /// the same text: a field given differently in two types is refused.
    /// </summary>
    public static LabelTextAppearance MergeAppearance(IReadOnlyList<LabelFamilyTypeSpec> types, List<string> errors)
    {
        object? Pick(string field, Func<LabelTextAppearance, object?> get)
        {
            string Show(object value) => value is double d ? d.ToString("0.######", CultureInfo.InvariantCulture)
                : value is bool b ? (b ? "true" : "false") : value.ToString() ?? string.Empty;
            var given = types.Select(t => (t.Name, Value: get(t.Appearance))).Where(t => t.Value != null).ToList();
            if (given.Select(t => Show(t.Value!)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            {
                errors.Add($"types give different {field} values ({string.Join(", ", given.Select(t => $"{t.Name}: {Show(t.Value!)}"))}). " +
                           "The family's label has one text type for all its types, so its text looks the same in every type: " +
                           "make one variant per text appearance instead.");
                return null;
            }
            return given.Count > 0 ? given[0].Value : null;
        }

        return new LabelTextAppearance
        {
            SizeMm = (double?)Pick("textSize", a => a.SizeMm),
            Font = (string?)Pick("font", a => a.Font),
            Bold = (bool?)Pick("bold", a => a.Bold),
            Italic = (bool?)Pick("italic", a => a.Italic),
            Underline = (bool?)Pick("underline", a => a.Underline),
            WidthFactor = (double?)Pick("widthFactor", a => a.WidthFactor),
            Color = (string?)Pick("color", a => a.Color)
        };
    }

    private static void ReadRename(IReadOnlyDictionary<string, object?> args, LabelFamilyVariantSpec spec, List<string> errors)
    {
        if (!args.TryGetValue("renameParameter", out var raw) || raw == null) return;
        var token = ToToken(raw);
        if (token == null || token.Type == JTokenType.Null) return;
        string from, to;
        if (token is JObject obj) { from = Text(obj["from"]); to = Text(obj["to"]); }
        else if (token.Type == JTokenType.String) { from = string.Empty; to = Text(token); }
        else { errors.Add("renameParameter must be {from?, to}."); return; }
        if (to.Length == 0) { errors.Add("renameParameter needs 'to', the parameter's new name."); return; }
        var nameError = CheckName(to, "parameter");
        if (nameError != null) { errors.Add($"renameParameter.to: {nameError}"); return; }

        if (from.Length > 0)
        {
            if (spec.Parameter.Length > 0 && !string.Equals(spec.Parameter, from, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"parameter '{spec.Parameter}' and renameParameter.from '{from}' name different parameters; the changes apply to one parameter, the label's.");
                return;
            }
            spec.Parameter = from;
        }
        spec.RenameTo = to;
    }

    /// <summary>
    /// The seed's parameter the changes apply to: the named one, else the only editable user-defined parameter.
    /// Returns null without an error when no change needs it.
    /// </summary>
    public static LabelFamilyParameterInfo? ResolveParameter(LabelFamilyVariantSpec spec, IReadOnlyList<LabelFamilyParameterInfo> parameters, List<string> errors)
    {
        if (!spec.ChangesParameter && spec.Parameter.Length == 0) return null;

        LabelFamilyParameterInfo? found;
        if (spec.Parameter.Length > 0)
        {
            found = parameters.FirstOrDefault(p => string.Equals(p.Name, spec.Parameter, StringComparison.OrdinalIgnoreCase));
            if (found == null)
            {
                var close = Suggest(spec.Parameter, parameters.Select(p => p.Name));
                errors.Add($"The seed has no family parameter '{spec.Parameter}'" +
                           (close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(c => $"'{c}'"))}?"
                               : parameters.Count > 0 ? $" (its parameters: {string.Join(", ", parameters.Select(p => p.Name).Take(15))})." : "; it has no user-defined parameters."));
                return null;
            }
        }
        else
        {
            var editable = parameters.Where(p => p.Editable).ToList();
            if (editable.Count != 1)
            {
                errors.Add(editable.Count == 0
                    ? "The seed has no editable user-defined parameter for the label to show."
                    : $"The seed has several parameters ({string.Join(", ", editable.Select(p => p.Name))}): pass parameter, the one its label shows.");
                return null;
            }
            found = editable[0];
        }

        if (spec.RenameTo.Length > 0)
        {
            if (found.IsShared)
                errors.Add($"'{found.Name}' is a shared parameter: Revit does not rename shared parameters. Use a family parameter in the seed's label.");
            else if (!string.Equals(found.Name, spec.RenameTo, StringComparison.Ordinal)
                     && parameters.Any(p => p != found && string.Equals(p.Name, spec.RenameTo, StringComparison.OrdinalIgnoreCase)))
                errors.Add($"The seed already has a parameter '{spec.RenameTo}'; renameParameter.to must be a free name.");
        }
        if (spec.SampleValue != null && !found.Editable)
            errors.Add($"'{found.Name}' is {(string.IsNullOrEmpty(found.Formula) ? "read-only" : $"set by the formula '{found.Formula}'")}, so sampleValue cannot be set.");
        return found;
    }

    /// <summary>
    /// The type changes: each requested type is kept (same name), renamed (same name in another case) or created; the
    /// seed's types that are not requested are deleted. No requested types = every seed type is kept.
    /// </summary>
    public static List<LabelFamilyTypeAction> PlanTypes(IReadOnlyList<string> seedTypes, IReadOnlyList<LabelFamilyTypeSpec> requested)
    {
        var result = new List<LabelFamilyTypeAction>();
        if (requested.Count == 0)
            return seedTypes.Select(t => new LabelFamilyTypeAction { Name = t, Action = "keep", SeedName = t }).ToList();

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in requested)
        {
            var exact = seedTypes.FirstOrDefault(t => string.Equals(t, type.Name, StringComparison.Ordinal) && !used.Contains(t));
            var other = exact ?? seedTypes.FirstOrDefault(t => string.Equals(t, type.Name, StringComparison.OrdinalIgnoreCase) && !used.Contains(t));
            if (other != null) used.Add(other);
            result.Add(new LabelFamilyTypeAction
            {
                Name = type.Name,
                Action = exact != null ? "keep" : other != null ? "rename" : "create",
                SeedName = other
            });
        }
        result.AddRange(seedTypes.Where(t => !used.Contains(t)).Select(t => new LabelFamilyTypeAction { Name = t, Action = "delete", SeedName = t }));
        return result;
    }

    // ── revit_list_label_families ───────────────────────────────────────────

    /// <summary>
    /// Which loaded Generic Annotation families to open for their parameters: those named in <paramref name="filter"/>
    /// (all when empty), by name, at most <paramref name="limit"/>. Names in the filter that are not loaded go to
    /// <paramref name="errors"/> with close names.
    /// </summary>
    public static (List<string> Listed, List<string> Inspect) PlanListing(IReadOnlyCollection<string> loaded, IReadOnlyList<string> filter,
        int limit, bool inspect, List<string> errors)
    {
        var listed = new List<string>();
        if (filter.Count == 0) listed.AddRange(loaded);
        else
            foreach (var name in filter.Select(f => f.Trim()).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var match = loaded.FirstOrDefault(l => string.Equals(l, name, StringComparison.OrdinalIgnoreCase));
                if (match != null) { listed.Add(match); continue; }
                var close = Suggest(name, loaded);
                errors.Add($"'{name}' is not a Generic Annotation family loaded in the document" +
                           (close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(c => $"'{c}'"))}?" : "."));
            }
        listed = listed.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var cap = Math.Max(0, Math.Min(limit <= 0 ? DefaultInspectLimit : limit, MaxInspectLimit));
        return (listed, inspect ? listed.Take(cap).ToList() : new List<string>());
    }

    // ── Shared helpers ──────────────────────────────────────────────────────

    /// <summary>Revit's LINE_COLOR integer (red + green·256 + blue·65536) of "#RRGGBB".</summary>
    public static int ColorToRevitInt(string hex)
    {
        var r = Convert.ToInt32(hex.Substring(1, 2), 16);
        var g = Convert.ToInt32(hex.Substring(3, 2), 16);
        var b = Convert.ToInt32(hex.Substring(5, 2), 16);
        return r + (g << 8) + (b << 16);
    }

    /// <summary>"#RRGGBB" of Revit's LINE_COLOR integer.</summary>
    public static string RevitIntToColor(int value) => $"#{value & 0xFF:X2}{(value >> 8) & 0xFF:X2}{(value >> 16) & 0xFF:X2}";

    public static string? CheckName(string name, string what)
    {
        if (name != name.Trim()) return $"The {what} name '{name}' starts or ends with a space.";
        var bad = name.Where(c => ForbiddenNameCharacters.IndexOf(c) >= 0 || char.IsControl(c)).Distinct().ToList();
        if (bad.Count > 0) return $"The {what} name '{name}' contains characters Revit refuses: {string.Join(" ", bad)}.";
        if (name.Length > 200) return $"The {what} name is longer than 200 characters.";
        return null;
    }

    /// <summary>Up to five names close to <paramref name="requested"/>: containing it, contained in it, or sharing a word.</summary>
    public static List<string> Suggest(string requested, IEnumerable<string> names) =>
        names.Where(n => n.IndexOf(requested, StringComparison.OrdinalIgnoreCase) >= 0
                         || requested.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0
                         || n.Split(' ', '_', '-').Any(w => w.Length > 2 && requested.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    private static bool? ReadFlag(JToken? token, string where, List<string> errors, ref bool ok)
    {
        if (token == null || token.Type == JTokenType.Null) return null;
        var flag = Flag(token);
        if (flag == null) { errors.Add($"{where} must be true or false."); ok = false; }
        return flag;
    }

    private static bool? Flag(JToken? token)
    {
        if (token == null || token.Type == JTokenType.Null) return null;
        if (token.Type == JTokenType.Boolean) return (bool)token;
        if (token.Type == JTokenType.Integer) return (long)token != 0;
        return ((string?)token ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "1" => true,
            "false" or "no" or "0" => false,
            _ => null
        };
    }

    private static string? Colour(JToken? token, string where, List<string> errors, ref bool ok)
    {
        if (token == null || token.Type == JTokenType.Null) return null;
        double? r = null, g = null, b = null;
        if (token is JArray array && array.Count == 3) { r = Num(array[0]); g = Num(array[1]); b = Num(array[2]); }
        else if (token is JObject obj) { r = Num(obj["r"] ?? obj["red"]); g = Num(obj["g"] ?? obj["green"]); b = Num(obj["b"] ?? obj["blue"]); }
        else
        {
            var text = Text(token);
            var hex = text.StartsWith("#", StringComparison.Ordinal) ? text.Substring(1) : text;
            if (hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                return "#" + value.ToString("X6", CultureInfo.InvariantCulture);
            var parts = text.Split(',');
            if (parts.Length == 3) { r = Num(new JValue(parts[0].Trim())); g = Num(new JValue(parts[1].Trim())); b = Num(new JValue(parts[2].Trim())); }
        }
        static bool Channel(double? v) => v is >= 0 and <= 255 && Math.Abs(v.Value - Math.Round(v.Value)) < 1e-9;
        if (Channel(r) && Channel(g) && Channel(b)) return $"#{(int)r!.Value:X2}{(int)g!.Value:X2}{(int)b!.Value:X2}";
        errors.Add($"{where} is not a colour: use \"#RRGGBB\", \"r,g,b\" or [r, g, b].");
        ok = false;
        return null;
    }

    private static double? Num(JToken? token)
    {
        if (token == null) return null;
        if (token.Type is JTokenType.Integer or JTokenType.Float) return (double)token;
        if (token.Type == JTokenType.String
            && double.TryParse((string?)token, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return v;
        return null;
    }

    private static string Text(JToken? token) =>
        token == null || token.Type == JTokenType.Null ? string.Empty
        : token.Type == JTokenType.String ? ((string?)token ?? string.Empty).Trim()
        : token.ToString(Newtonsoft.Json.Formatting.None).Trim();

    private static JToken? ToToken(object raw)
    {
        try
        {
            return raw switch
            {
                JToken t => t,
                string s when s.Trim().StartsWith("{", StringComparison.Ordinal) || s.Trim().StartsWith("[", StringComparison.Ordinal) => JToken.Parse(s),
                string s => new JValue(s),
                bool b => new JValue(b),
                _ => JToken.FromObject(raw)
            };
        }
        catch { return null; }
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
}
