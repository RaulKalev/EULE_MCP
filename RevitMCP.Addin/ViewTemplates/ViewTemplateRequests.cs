using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.ViewTemplates;

/// <summary>
/// What to change on one view template: revit_set_view_templates, and the edits of revit_create_view_templates applied
/// to the new template. A null field or an empty list is not given.
/// </summary>
public sealed class ViewTemplateEdit
{
    /// <summary>Where it came from in the request, for messages: templates[0], …</summary>
    public string Source { get; set; } = string.Empty;
    /// <summary>The template's name or id; for a new template, its name.</summary>
    public string Template { get; set; } = string.Empty;
    public string? NewName { get; set; }
    /// <summary>The scale denominator: 50 for 1:50.</summary>
    public int? Scale { get; set; }
    /// <summary>Coarse, Medium or Fine.</summary>
    public string? DetailLevel { get; set; }
    /// <summary>A ViewDiscipline name: Architectural, Structural, Mechanical, Electrical, Plumbing, Coordination.</summary>
    public string? Discipline { get; set; }
    /// <summary>A DisplayStyle name: Wireframe, HLR, Shading, ShadingWithEdges, Realistic, RealisticWithEdges, FlatColors, Textures, Rendering.</summary>
    public string? DisplayStyle { get; set; }
    /// <summary>Template parameters the template should control.</summary>
    public List<string> Include { get; } = new();
    /// <summary>Template parameters the template should not control.</summary>
    public List<string> Exclude { get; } = new();
    public List<string> HideCategories { get; } = new();
    public List<string> ShowCategories { get; } = new();
    /// <summary>Other parameters of the template: name → value as text.</summary>
    public List<KeyValuePair<string, string>> Parameters { get; } = new();

    public bool HasChanges =>
        NewName != null || Scale != null || DetailLevel != null || Discipline != null || DisplayStyle != null ||
        Include.Count > 0 || Exclude.Count > 0 || HideCategories.Count > 0 || ShowCategories.Count > 0 || Parameters.Count > 0;

    /// <summary>"scale 1:50, detail level Fine, controls +View Scale, …", for approval texts.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (NewName != null) parts.Add($"rename to '{NewName}'");
        if (Scale != null) parts.Add($"scale 1:{Scale.Value.ToString(CultureInfo.InvariantCulture)}");
        if (DetailLevel != null) parts.Add($"detail level {DetailLevel}");
        if (Discipline != null) parts.Add($"discipline {Discipline}");
        if (DisplayStyle != null) parts.Add($"display style {DisplayStyle}");
        if (Include.Count > 0) parts.Add($"controls {string.Join(", ", Include.Take(4).Select(n => $"+{n}"))}{(Include.Count > 4 ? ", …" : string.Empty)}");
        if (Exclude.Count > 0) parts.Add($"stops controlling {string.Join(", ", Exclude.Take(4))}{(Exclude.Count > 4 ? ", …" : string.Empty)}");
        if (HideCategories.Count > 0) parts.Add($"hides {HideCategories.Count} categor{(HideCategories.Count == 1 ? "y" : "ies")}");
        if (ShowCategories.Count > 0) parts.Add($"shows {ShowCategories.Count} categor{(ShowCategories.Count == 1 ? "y" : "ies")}");
        if (Parameters.Count > 0) parts.Add($"{Parameters.Count} parameter(s): {string.Join(", ", Parameters.Take(3).Select(p => $"{p.Key} = '{p.Value}'"))}{(Parameters.Count > 3 ? ", …" : string.Empty)}");
        return string.Join(", ", parts);
    }
}

/// <summary>A view template to create: from a view (View.CreateViewTemplate) or as a copy of a template, then edited.</summary>
public sealed class ViewTemplateCreateSpec
{
    public string Source { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>A view name or id: the new template takes its settings.</summary>
    public string? FromView { get; set; }
    /// <summary>A view template name or id to copy.</summary>
    public string? DuplicateFrom { get; set; }
    /// <summary>Edits applied to the new template; Template is the new name.</summary>
    public ViewTemplateEdit Edit { get; set; } = new();

    public string Mode => FromView != null ? "fromView" : "duplicate";
}

/// <summary>Arguments of the view template tools. Pure: no Revit types, unit tested.</summary>
public static class ViewTemplateRequestParser
{
    public const int MaxTemplates = 50;
    public const int MinScale = 1;
    public const int MaxScale = 24000;

    /// <summary>Characters Revit refuses in view names.</summary>
    private const string ForbiddenNameCharacters = "\\:{}[]|;<>?`~";

    public static readonly IReadOnlyList<string> DetailLevels = new[] { "Coarse", "Medium", "Fine" };

    public static readonly IReadOnlyList<string> Disciplines =
        new[] { "Architectural", "Structural", "Mechanical", "Electrical", "Plumbing", "Coordination" };

    /// <summary>Accepted words (lower case, no spaces) → the DisplayStyle enum name.</summary>
    private static readonly Dictionary<string, string> DisplayStyleWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wireframe"] = "Wireframe",
        ["hlr"] = "HLR",
        ["hiddenline"] = "HLR",
        ["shading"] = "Shading",
        ["shaded"] = "Shading",
        ["shadingwithedges"] = "ShadingWithEdges",
        ["shadedwithedges"] = "ShadingWithEdges",
        ["consistentcolors"] = "FlatColors",
        ["consistentcolours"] = "FlatColors",
        ["flatcolors"] = "FlatColors",
        ["realistic"] = "Realistic",
        ["realisticwithedges"] = "RealisticWithEdges",
        ["textures"] = "Textures",
        ["rendering"] = "Rendering"
    };

    public static IReadOnlyCollection<string> DisplayStyles => DisplayStyleWords.Values.Distinct().ToList();

    /// <summary>Reads revit_set_view_templates' templates [{template, newName?, scale?, …}].</summary>
    public static List<ViewTemplateEdit> ReadEdits(IReadOnlyDictionary<string, object?> args, string key, List<string> errors)
    {
        var result = new List<ViewTemplateEdit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in Items(args, key, errors))
        {
            var source = $"{key}[{index++}]";
            if (item == null) continue;
            var template = Text(item["template"] ?? item["id"]);
            if (template.Length == 0) { errors.Add($"{source} needs template: a view template name or id."); continue; }
            if (!seen.Add(template)) { errors.Add($"{source}: the template '{template}' is listed twice; put all its changes in one item."); continue; }

            var edit = new ViewTemplateEdit { Source = source, Template = template };
            if (!ReadEditFields(item, edit, source, errors, allowRename: true)) continue;
            if (!edit.HasChanges)
            {
                errors.Add($"{source} ('{template}') gives nothing to change: newName, scale, detailLevel, discipline, displayStyle, " +
                           "include, exclude, hideCategories, showCategories or parameters.");
                continue;
            }
            result.Add(edit);
        }
        if (result.Count > MaxTemplates) errors.Add($"{result.Count} templates were given; at most {MaxTemplates} per call.");
        return result;
    }

    /// <summary>Reads revit_create_view_templates' templates [{name, fromView? | duplicateFrom?, edits…}].</summary>
    public static List<ViewTemplateCreateSpec> ReadCreates(IReadOnlyDictionary<string, object?> args, string key, List<string> errors)
    {
        var result = new List<ViewTemplateCreateSpec>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in Items(args, key, errors))
        {
            var source = $"{key}[{index++}]";
            if (item == null) continue;
            var name = Text(item["name"]);
            if (name.Length == 0) { errors.Add($"{source} needs a name for the new template."); continue; }
            var nameError = CheckName(name);
            if (nameError != null) { errors.Add($"{source}: {nameError}"); continue; }
            if (!seen.Add(name)) { errors.Add($"{source}: the name '{name}' is given twice."); continue; }

            var fromView = OptionalText(item["fromView"]);
            var duplicateFrom = OptionalText(item["duplicateFrom"]);
            if (fromView != null && duplicateFrom != null)
            {
                errors.Add($"{source} ('{name}'): give fromView or duplicateFrom, not both.");
                continue;
            }
            if (fromView == null && duplicateFrom == null)
            {
                errors.Add($"{source} ('{name}') needs fromView (a view to take the settings from) or duplicateFrom (a template to copy).");
                continue;
            }
            if (item["newName"] != null && item["newName"]!.Type != JTokenType.Null)
            {
                errors.Add($"{source} ('{name}'): newName does not apply to a new template; name is its name.");
                continue;
            }

            var spec = new ViewTemplateCreateSpec
            {
                Source = source,
                Name = name,
                FromView = fromView,
                DuplicateFrom = duplicateFrom,
                Edit = new ViewTemplateEdit { Source = source, Template = name }
            };
            if (!ReadEditFields(item, spec.Edit, source, errors, allowRename: false)) continue;
            result.Add(spec);
        }
        if (result.Count > MaxTemplates) errors.Add($"{result.Count} templates were given; at most {MaxTemplates} per call.");
        return result;
    }

    public static string? CheckName(string name)
    {
        if (name.Trim().Length == 0) return "The template name is empty.";
        if (name != name.Trim()) return $"The template name '{name}' starts or ends with a space.";
        var bad = name.Where(c => ForbiddenNameCharacters.IndexOf(c) >= 0 || char.IsControl(c)).Distinct().ToList();
        if (bad.Count > 0) return $"The template name '{name}' contains characters Revit refuses: {string.Join(" ", bad)}.";
        if (name.Length > 250) return "The template name is longer than 250 characters.";
        return null;
    }

    /// <summary>50, 50.0, "50" or "1:50" → 50. Null when it is not a scale.</summary>
    public static int? ParseScale(JToken? token)
    {
        if (token == null || token.Type == JTokenType.Null) return null;
        double? number = null;
        if (token.Type is JTokenType.Integer or JTokenType.Float) number = token.Value<double>();
        else if (token.Type == JTokenType.String)
        {
            var text = (token.Value<string>() ?? string.Empty).Trim().Replace(" ", string.Empty);
            var colon = text.IndexOf(':');
            if (colon >= 0)
            {
                if (text.Substring(0, colon) != "1") return null;
                text = text.Substring(colon + 1);
            }
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) number = parsed;
        }
        if (number == null || Math.Abs(number.Value - Math.Round(number.Value)) > 1e-9) return null;
        var scale = Math.Round(number.Value);
        return scale is >= MinScale and <= MaxScale ? (int)scale : null;
    }

    public static string? NormalizeDetailLevel(string text) => Pick(text, DetailLevels);

    public static string? NormalizeDiscipline(string text) => Pick(text, Disciplines);

    public static string? NormalizeDisplayStyle(string text) =>
        DisplayStyleWords.TryGetValue(Squeeze(text), out var style) ? style : null;

    private static bool ReadEditFields(JObject item, ViewTemplateEdit edit, string source, List<string> errors, bool allowRename)
    {
        var before = errors.Count;

        if (allowRename && item["newName"] is { } nameToken && nameToken.Type != JTokenType.Null)
        {
            var newName = Text(nameToken);
            var nameError = CheckName(newName);
            if (nameError != null) errors.Add($"{source}.newName: {nameError}");
            else edit.NewName = newName;
        }

        if (item["scale"] is { } scaleToken && scaleToken.Type != JTokenType.Null)
        {
            edit.Scale = ParseScale(scaleToken);
            if (edit.Scale == null)
                errors.Add($"{source}.scale must be a scale such as 50 or \"1:50\" (a whole number from {MinScale} to {MaxScale}).");
        }

        edit.DetailLevel = Choice(item["detailLevel"], source + ".detailLevel", NormalizeDetailLevel, DetailLevels, errors);
        edit.Discipline = Choice(item["discipline"], source + ".discipline", NormalizeDiscipline, Disciplines, errors);
        edit.DisplayStyle = Choice(item["displayStyle"], source + ".displayStyle", NormalizeDisplayStyle,
            new[] { "Wireframe", "Hidden Line", "Shaded", "Shaded with edges", "Consistent Colors", "Realistic", "Realistic with edges", "Textures" }, errors);

        edit.Include.AddRange(Strings(item["include"], source + ".include", errors));
        edit.Exclude.AddRange(Strings(item["exclude"], source + ".exclude", errors));
        foreach (var both in edit.Include.Intersect(edit.Exclude, StringComparer.OrdinalIgnoreCase))
            errors.Add($"{source}: '{both}' is in both include and exclude.");

        edit.HideCategories.AddRange(Strings(item["hideCategories"], source + ".hideCategories", errors));
        edit.ShowCategories.AddRange(Strings(item["showCategories"], source + ".showCategories", errors));
        foreach (var both in edit.HideCategories.Intersect(edit.ShowCategories, StringComparer.OrdinalIgnoreCase))
            errors.Add($"{source}: '{both}' is in both hideCategories and showCategories.");

        if (item["parameters"] is { } parameters && parameters.Type != JTokenType.Null)
        {
            if (parameters is not JObject map) errors.Add($"{source}.parameters must be an object {{\"parameter name\": value}}.");
            else
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in map.Properties())
                {
                    var name = property.Name.Trim();
                    if (name.Length == 0) { errors.Add($"{source}.parameters has an empty parameter name."); continue; }
                    if (!seen.Add(name)) { errors.Add($"{source}.parameters names '{name}' twice."); continue; }
                    var value = ValueText(property.Value);
                    if (value == null) { errors.Add($"{source}.parameters['{name}'] must be text, a number, true/false or null."); continue; }
                    edit.Parameters.Add(new KeyValuePair<string, string>(name, value));
                }
            }
        }

        return errors.Count == before;
    }

    /// <summary>A parameter value as text: numbers in invariant culture, true/false as 1/0 (Yes/No parameters), null as empty.</summary>
    public static string? ValueText(JToken? token)
    {
        if (token == null) return string.Empty;
        return token.Type switch
        {
            JTokenType.Null => string.Empty,
            JTokenType.String => token.Value<string>() ?? string.Empty,
            JTokenType.Boolean => token.Value<bool>() ? "1" : "0",
            JTokenType.Integer => token.Value<long>().ToString(CultureInfo.InvariantCulture),
            JTokenType.Float => token.Value<double>().ToString("R", CultureInfo.InvariantCulture),
            _ => null
        };
    }

    private static string? Choice(JToken? token, string path, Func<string, string?> normalize, IEnumerable<string> allowed, List<string> errors)
    {
        if (token == null || token.Type == JTokenType.Null) return null;
        var text = Text(token);
        var value = normalize(text);
        if (value == null) errors.Add($"{path} '{text}' is not one of: {string.Join(", ", allowed)}.");
        return value;
    }

    private static string? Pick(string text, IEnumerable<string> values)
    {
        var squeezed = Squeeze(text);
        return values.FirstOrDefault(v => string.Equals(Squeeze(v), squeezed, StringComparison.OrdinalIgnoreCase));
    }

    private static string Squeeze(string text) => new((text ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c != '_' && c != '-').ToArray());

    private static List<string> Strings(JToken? token, string path, List<string> errors)
    {
        var result = new List<string>();
        if (token == null || token.Type == JTokenType.Null) return result;
        if (token.Type == JTokenType.String) token = new JArray(token);
        if (token is not JArray array) { errors.Add($"{path} must be a list of names."); return result; }
        foreach (var entry in array)
        {
            var text = entry.Type is JTokenType.String or JTokenType.Integer ? Text(entry) : string.Empty;
            if (text.Length == 0) { errors.Add($"{path} has an empty or non-text entry."); continue; }
            if (!result.Contains(text, StringComparer.OrdinalIgnoreCase)) result.Add(text);
        }
        return result;
    }

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
        if (token is JObject single) token = new JArray(single);
        if (token is not JArray array)
        {
            errors.Add($"{key} must be an array of objects.");
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

    private static JToken? TryParse(string s)
    {
        try { return JToken.Parse(s); }
        catch (JsonException) { return null; }
    }

    private static JToken? TryFrom(object raw)
    {
        try { return JToken.FromObject(raw); }
        catch (Exception) { return null; }
    }

    private static string Text(JToken? token) =>
        token == null || token.Type == JTokenType.Null ? string.Empty : (token.Type == JTokenType.String ? token.Value<string>() ?? string.Empty : token.ToString()).Trim();

    private static string? OptionalText(JToken? token)
    {
        var text = Text(token);
        return text.Length == 0 ? null : text;
    }
}
