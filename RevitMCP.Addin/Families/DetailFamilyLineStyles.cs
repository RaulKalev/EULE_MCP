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
    /// <summary>The style's new name (revit_set_line_styles only); null = keep the name.</summary>
    public string? NewName { get; set; }

    public bool HasValues => Weight != null || Color != null || Pattern != null;
}

/// <summary>
/// An object style to edit (a category or one of its subcategories), or a subcategory to create: revit_set_object_styles.
/// A null field is not given.
/// </summary>
public sealed class ObjectStyleDefinition
{
    public string Source { get; set; } = string.Empty;
    /// <summary>The category: a name, a built-in name (OST_Walls) or an id.</summary>
    public string Category { get; set; } = string.Empty;
    /// <summary>The subcategory to edit or create; null = the category itself.</summary>
    public string? Subcategory { get; set; }
    public int? ProjectionWeight { get; set; }
    public int? CutWeight { get; set; }
    /// <summary>#RRGGBB.</summary>
    public string? Color { get; set; }
    /// <summary>A line pattern name; empty = solid; null = not given.</summary>
    public string? Pattern { get; set; }
    /// <summary>A material name; empty = no material; null = not given.</summary>
    public string? Material { get; set; }

    public bool HasValues => ProjectionWeight != null || CutWeight != null || Color != null || Pattern != null || Material != null;

    /// <summary>"Walls" or "Walls / Hidden Lines", for messages.</summary>
    public string Label => Subcategory == null ? Category : $"{Category} / {Subcategory}";
}

/// <summary>Line style definitions of revit_set_line_styles and revit_create_detail_family. Pure: no Revit types, unit tested.</summary>
public static partial class DetailFamilySpecParser
{
    public const int MaxLineStyles = 100;
    public const int MaxObjectStyles = 200;
    /// <summary>What a new line style gets for a field that is not given.</summary>
    public const int DefaultLineWeight = 1;
    public const string DefaultLineColor = "#000000";

    /// <summary>Characters Revit refuses in subcategory names.</summary>
    private const string ForbiddenStyleCharacters = "\\:{}[]|;<>?`~";

    /// <summary>
    /// Reads [{name, weight?, color?, pattern?}]. weight is the projection line weight (1-16; lineWeight and size are
    /// accepted too), color "#RRGGBB", "r,g,b" or [r,g,b], pattern a line pattern name or "solid". With
    /// <paramref name="allowRename"/> (revit_set_line_styles) an item may also carry newName.
    /// </summary>
    public static List<LineStyleDefinition> ReadLineStyleDefinitions(
        IReadOnlyDictionary<string, object?> args, string key, List<string> errors, bool allowRename = false)
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

            definition.Weight = Weight(item["weight"] ?? item["lineWeight"] ?? item["size"], source + ".weight", errors, ref ok);
            definition.Color = Colour(item["color"] ?? item["colour"], source + ".color", errors, ref ok);
            definition.Pattern = PatternName(item["pattern"] ?? item["linePattern"]);

            if (allowRename && item["newName"] is { Type: not JTokenType.Null } newNameToken)
            {
                var newName = Text(newNameToken);
                var newNameError = newName.Length == 0 ? "newName is empty." : CheckLineStyleName(newName);
                if (newNameError != null)
                {
                    errors.Add($"{source}.newName: {newNameError}");
                    ok = false;
                }
                else if (!string.Equals(newName, name, StringComparison.Ordinal)) definition.NewName = newName;
            }

            if (ok) result.Add(definition);
        }

        if (allowRename)
        {
            // A new name may not be another item's name or new name: the outcome would depend on the order.
            var names = new HashSet<string>(result.Select(d => d.Name), StringComparer.OrdinalIgnoreCase);
            var newNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var definition in result.Where(d => d.NewName != null))
            {
                if (!newNames.Add(definition.NewName!))
                    errors.Add($"{definition.Source}.newName: '{definition.NewName}' is the new name of another style in this request.");
                else if (names.Contains(definition.NewName!) && !string.Equals(definition.NewName, definition.Name, StringComparison.OrdinalIgnoreCase))
                    errors.Add($"{definition.Source}.newName: '{definition.NewName}' is also listed as a style name in this request.");
            }
        }

        if (result.Count > MaxLineStyles)
            errors.Add($"{result.Count} line styles were given; at most {MaxLineStyles} per call.");
        return result;
    }

    /// <summary>
    /// Reads [{category, subcategory?, projectionWeight?, cutWeight?, color?, pattern?, material?}] of
    /// revit_set_object_styles. weight and lineWeight are accepted for projectionWeight; pattern "solid" and material
    /// "none" clear them.
    /// </summary>
    public static List<ObjectStyleDefinition> ReadObjectStyleDefinitions(IReadOnlyDictionary<string, object?> args, string key, List<string> errors)
    {
        var result = new List<ObjectStyleDefinition>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in Items(args, key, errors))
        {
            var source = $"{key}[{index++}]";
            if (item == null) continue;
            var category = Text(item["category"]);
            if (category.Length == 0) { errors.Add($"{source} needs a category."); continue; }

            string? subcategory = null;
            if (item["subcategory"] is { Type: not JTokenType.Null } subToken)
            {
                subcategory = Text(subToken);
                var nameError = subcategory.Length == 0 ? "subcategory is empty." : CheckStyleName(subcategory, "subcategory");
                if (nameError != null) { errors.Add($"{source}: {nameError}"); continue; }
            }

            var definition = new ObjectStyleDefinition { Source = source, Category = category, Subcategory = subcategory };
            if (!seen.Add(definition.Label)) { errors.Add($"{source}: '{definition.Label}' is listed twice."); continue; }

            var ok = true;
            definition.ProjectionWeight = Weight(item["projectionWeight"] ?? item["weight"] ?? item["lineWeight"], source + ".projectionWeight", errors, ref ok);
            definition.CutWeight = Weight(item["cutWeight"], source + ".cutWeight", errors, ref ok);
            definition.Color = Colour(item["color"] ?? item["colour"], source + ".color", errors, ref ok);
            definition.Pattern = PatternName(item["pattern"] ?? item["linePattern"]);

            if (item["material"] is { Type: not JTokenType.Null } materialToken)
            {
                var material = Text(materialToken);
                definition.Material = material.Equals("none", StringComparison.OrdinalIgnoreCase) || material.Equals("<By Category>", StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : material;
            }

            if (ok && subcategory == null && !definition.HasValues)
            {
                errors.Add($"{source}: nothing to change on '{category}'; give projectionWeight, cutWeight, color, pattern or material, or a subcategory to create.");
                ok = false;
            }
            if (ok) result.Add(definition);
        }
        if (result.Count > MaxObjectStyles)
            errors.Add($"{result.Count} object styles were given; at most {MaxObjectStyles} per call.");
        return result;
    }

    public static string? CheckLineStyleName(string name) => CheckStyleName(name, "line style");

    /// <summary>Why Revit would refuse <paramref name="name"/> as a subcategory name; null when it is fine.</summary>
    public static string? CheckStyleName(string name, string what)
    {
        if (name != name.Trim()) return $"The {what} name '{name}' starts or ends with a space.";
        var bad = name.Where(c => ForbiddenStyleCharacters.IndexOf(c) >= 0 || char.IsControl(c)).Distinct().ToList();
        if (bad.Count > 0) return $"The {what} name '{name}' contains characters Revit refuses: {string.Join(" ", bad)}.";
        if (name.Length > 200) return $"The {what} name is longer than 200 characters.";
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

    /// <summary>A line weight number 1-16; null when not given. A bad value adds an error and clears <paramref name="ok"/>.</summary>
    private static int? Weight(JToken? token, string where, List<string> errors, ref bool ok)
    {
        if (token == null || token.Type == JTokenType.Null) return null;
        var number = Num(token);
        if (number is >= 1 and <= 16 && Math.Abs(number.Value - Math.Round(number.Value)) < 1e-9)
            return (int)Math.Round(number.Value);
        errors.Add($"{where} must be a whole number from 1 to 16: Revit's line weight number, not millimetres " +
                   "(the width of each number is set in Manage > Additional Settings > Line Weights).");
        ok = false;
        return null;
    }

    /// <summary>A line pattern name; empty for solid or none; null when not given.</summary>
    private static string? PatternName(JToken? token)
    {
        if (token == null || token.Type == JTokenType.Null) return null;
        var pattern = Text(token);
        return pattern.Length == 0 || pattern.Equals("solid", StringComparison.OrdinalIgnoreCase)
                                   || pattern.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : pattern;
    }
}
