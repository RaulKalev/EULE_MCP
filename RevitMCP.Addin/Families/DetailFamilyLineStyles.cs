using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Families;

/// <summary>
/// A line style to create, or to edit when the name exists: used by revit_set_line_styles (in a project or a family
/// document) and by revit_create_detail_family's lineStyles (in the family being created). A null field is not given.
/// </summary>
public sealed class LineStyleDefinition
{
    /// <summary>Where it came from in the request, for messages: styles[0], lineStyles[1], …</summary>
    public string Source { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>Projection line weight, 1 to 16.</summary>
    public int? Weight { get; set; }
    /// <summary>#RRGGBB.</summary>
    public string? Color { get; set; }
    /// <summary>A line pattern name; empty = solid; null = not given.</summary>
    public string? Pattern { get; set; }

    public bool HasValues => Weight != null || Color != null || Pattern != null;
}

/// <summary>Line style definitions of revit_set_line_styles and revit_create_detail_family. Pure: no Revit types, unit tested.</summary>
public static partial class DetailFamilySpecParser
{
    public const int MaxLineStyles = 100;
    /// <summary>What a new line style gets for a field that is not given.</summary>
    public const int DefaultLineWeight = 1;
    public const string DefaultLineColor = "#000000";

    /// <summary>Characters Revit refuses in subcategory names.</summary>
    private const string ForbiddenStyleCharacters = "\\:{}[]|;<>?`~";

    /// <summary>
    /// Reads [{name, weight?, color?, pattern?}]. weight is the projection line weight (1-16; lineWeight and size are
    /// accepted too), color "#RRGGBB", "r,g,b" or [r,g,b], pattern a line pattern name or "solid".
    /// </summary>
    public static List<LineStyleDefinition> ReadLineStyleDefinitions(IReadOnlyDictionary<string, object?> args, string key, List<string> errors)
    {
        var result = new List<LineStyleDefinition>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in Items(args, key, errors))
        {
            var source = $"{key}[{index++}]";
            if (item == null) continue;
            var name = Text(item["name"]);
            if (name.Length == 0) { errors.Add($"{source} needs a name."); continue; }
            var nameError = CheckLineStyleName(name);
            if (nameError != null) { errors.Add($"{source}: {nameError}"); continue; }
            if (!seen.Add(name)) { errors.Add($"{source}: the line style '{name}' is listed twice."); continue; }

            var definition = new LineStyleDefinition { Source = source, Name = name };
            var ok = true;

            var weightToken = item["weight"] ?? item["lineWeight"] ?? item["size"];
            if (weightToken != null && weightToken.Type != JTokenType.Null)
            {
                var number = Num(weightToken);
                if (number is >= 1 and <= 16 && Math.Abs(number.Value - Math.Round(number.Value)) < 1e-9)
                    definition.Weight = (int)Math.Round(number.Value);
                else
                {
                    errors.Add($"{source}.weight must be a whole number from 1 to 16: Revit's line weight number, not millimetres " +
                               "(the width of each number is set in Manage > Additional Settings > Line Weights).");
                    ok = false;
                }
            }

            definition.Color = Colour(item["color"] ?? item["colour"], source + ".color", errors, ref ok);

            var patternToken = item["pattern"] ?? item["linePattern"];
            if (patternToken != null && patternToken.Type != JTokenType.Null)
            {
                var pattern = Text(patternToken);
                definition.Pattern = pattern.Length == 0 || pattern.Equals("solid", StringComparison.OrdinalIgnoreCase)
                                     || pattern.Equals("none", StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : pattern;
            }

            if (ok) result.Add(definition);
        }
        if (result.Count > MaxLineStyles)
            errors.Add($"{result.Count} line styles were given; at most {MaxLineStyles} per call.");
        return result;
    }

    public static string? CheckLineStyleName(string name)
    {
        if (name != name.Trim()) return $"The line style name '{name}' starts or ends with a space.";
        var bad = name.Where(c => ForbiddenStyleCharacters.IndexOf(c) >= 0 || char.IsControl(c)).Distinct().ToList();
        if (bad.Count > 0) return $"The line style name '{name}' contains characters Revit refuses: {string.Join(" ", bad)}.";
        if (name.Length > 200) return "The line style name is longer than 200 characters.";
        return null;
    }

    /// <summary>
    /// What a defined style looks like: the definition's values over those of the existing style of that name
    /// (<paramref name="existing"/>, null when new), and the defaults for a new style's fields that are not given.
    /// </summary>
    public static ProjectLineStyleInfo MergeLineStyle(LineStyleDefinition definition, ProjectLineStyleInfo? existing) => new()
    {
        Name = existing?.Name ?? definition.Name,
        Weight = definition.Weight ?? existing?.Weight ?? DefaultLineWeight,
        Color = definition.Color ?? (existing != null && existing.Color.Length > 0 ? existing.Color : DefaultLineColor),
        Pattern = definition.Pattern ?? existing?.Pattern ?? string.Empty
    };
}
