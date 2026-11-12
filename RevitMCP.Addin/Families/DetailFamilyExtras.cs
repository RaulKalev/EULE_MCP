using System.Globalization;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Parameters;

namespace RevitMCP.Addin.Families;

/// <summary>One parameter revit_create_detail_family adds to the family (#147, phase 2).</summary>
public sealed class FamilyParameterSpec
{
    /// <summary>Where it came from in the request, for messages: parameters[0], …</summary>
    public string Source { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>
    /// A shared parameter: an existing definition of the shared parameter file, found by name (and sharedGroup) or guid.
    /// The tool never creates a shared parameter definition.
    /// </summary>
    public bool Shared { get; set; }
    public string SharedGuid { get; set; } = string.Empty;
    public string SharedGroup { get; set; } = string.Empty;
    /// <summary>The data type as given (text, length, yesNo, …); empty for a shared parameter, which takes its definition's.</summary>
    public string DataType { get; set; } = string.Empty;
    /// <summary>The resolved data type; null for a shared parameter without dataType.</summary>
    public SpecResolution? Spec { get; set; }
    /// <summary>The group as given; empty = a default from the data type.</summary>
    public string Group { get; set; } = string.Empty;
    /// <summary>The GroupTypeId member name ("" = Other), or null when the map does not know it (the Revit side tries labels).</summary>
    public string? GroupKey { get; set; }
    public bool IsInstance { get; set; }
    /// <summary>The value for the family's type, as given (null = none).</summary>
    public JToken? Value { get; set; }
    public string Formula { get; set; } = string.Empty;
}

/// <summary>One text revit_create_detail_family places through a nested Generic Annotation family (#147, phase 3).</summary>
public sealed class DetailLabelSpec
{
    public string Source { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    /// <summary>The insertion point in the detail item, model mm on the reference level.</summary>
    public DetailPoint Position { get; set; }
    /// <summary>Text size on paper in mm; null = the text type's (textType) or the default.</summary>
    public double? SizeMm { get; set; }
    /// <summary>A text type of the project to copy (font, size, width factor, bold, italic); empty = the template's with SizeMm.</summary>
    public string TextType { get; set; } = string.Empty;
    /// <summary>left | center | right: where the insertion point sits on the text.</summary>
    public string Alignment { get; set; } = "left";
    /// <summary>Degrees counter-clockwise.</summary>
    public double RotationDeg { get; set; }
    /// <summary>
    /// The family parameter the label shows; empty = fixed <see cref="Text"/>. A parameter label is an instance of
    /// <see cref="LabelFamily"/>, whose own label parameter is linked to this one.
    /// </summary>
    public string Parameter { get; set; } = string.Empty;
    /// <summary>A Generic Annotation family that already holds a label: a family loaded in the project, or a .rfa path.</summary>
    public string LabelFamily { get; set; } = string.Empty;
    /// <summary>The type of the label family to place; empty = its first type.</summary>
    public string LabelType { get; set; } = string.Empty;
    /// <summary>The label family's parameter its label shows; empty = its only parameter of the matching data type.</summary>
    public string LabelParameter { get; set; } = string.Empty;
    /// <summary>A yes/no family parameter that switches the label on and off; empty = always visible.</summary>
    public string VisibleParameter { get; set; } = string.Empty;
}

/// <summary>One Generic Annotation family made for the labels: one per distinct text, size, text type and alignment.</summary>
public sealed class DetailLabelGroup
{
    public string FamilyName { get; set; } = string.Empty;
    public DetailLabelSpec Template { get; set; } = new();
    public List<DetailLabelSpec> Instances { get; } = new();
}

/// <summary>
/// Parameters and labels of revit_create_detail_family (#147, phases 2 and 3). Pure: no Revit types, unit tested.
/// </summary>
public static partial class DetailFamilySpecParser
{
    public const int MaxParameters = 100;
    public const int MaxLabels = 200;
    public const int MaxLabelFamilies = 50;
    public const double DefaultLabelSizeMm = 2.5;
    public const double MaxLabelSizeMm = 50;
    public const string DefaultAnnotationTemplateFileName = "Metric Generic Annotation.rft";

    /// <summary>Characters Revit refuses in parameter names.</summary>
    private const string ForbiddenParameterCharacters = "\\:{}[]|;<>?`~";

    private const double MmPerFoot = 304.8;
    private const double SquareFeetPerSquareMetre = 10.763910416709722;
    private const double CubicFeetPerCubicMetre = 35.31466672148859;

    private static void ReadParameters(IReadOnlyDictionary<string, object?> args, string key, DetailFamilySpec spec, List<string> errors)
    {
        var index = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Items(args, key, errors))
        {
            var source = $"{key}[{index++}]";
            if (item == null) continue;
            var parameter = new FamilyParameterSpec
            {
                Source = source,
                Name = Text(item["name"]),
                SharedGuid = Text(item["guid"]),
                SharedGroup = Text(item["sharedGroup"]),
                DataType = Text(item["dataType"] ?? item["type"]),
                Group = Text(item["group"] ?? item["parameterGroup"]),
                IsInstance = Flag(item["instance"]) ?? string.Equals(Text(item["binding"]), "instance", StringComparison.OrdinalIgnoreCase),
                Value = item["value"] is { Type: not JTokenType.Null } value ? value : null,
                Formula = Text(item["formula"])
            };
            parameter.Shared = (Flag(item["shared"]) ?? false) || parameter.SharedGuid.Length > 0;

            if (parameter.SharedGuid.Length > 0 && !Guid.TryParse(parameter.SharedGuid, out _))
                errors.Add($"{source}.guid '{parameter.SharedGuid}' is not a GUID.");
            if (parameter.Name.Length == 0 && parameter.SharedGuid.Length == 0)
            {
                errors.Add($"{source} needs a name{(parameter.Shared ? " or a guid" : string.Empty)}.");
                continue;
            }
            if (parameter.Name.Length > 0)
            {
                var nameError = CheckParameterName(parameter.Name);
                if (nameError != null) { errors.Add($"{source}: {nameError}"); continue; }
                if (!seen.Add(parameter.Name))
                {
                    errors.Add($"{source}: the parameter '{parameter.Name}' is listed twice.");
                    continue;
                }
            }

            if (parameter.DataType.Length > 0)
            {
                parameter.Spec = ParameterSpecMap.ResolveSpec(parameter.DataType);
                if (parameter.Spec == null)
                {
                    var suggestions = ParameterSpecMap.SuggestSpecs(parameter.DataType);
                    errors.Add($"{source}.dataType '{parameter.DataType}' is not known: use " +
                               $"{string.Join(", ", suggestions.Count > 0 ? suggestions : ParameterSpecMap.FriendlySpecNames.Take(10))}.");
                    continue;
                }
            }
            else if (!parameter.Shared)
            {
                errors.Add($"{source} ('{parameter.Name}') needs a dataType (text, integer, number, length, area, angle, yesNo, …).");
                continue;
            }

            if (parameter.Group.Length > 0)
                parameter.GroupKey = ParameterSpecMap.ResolveGroupKey(parameter.Group);
            else
                parameter.GroupKey = DefaultGroupKey(parameter.Spec?.Friendly);

            if (parameter.Formula.Length > 0 && parameter.Value != null)
                errors.Add($"{source} ('{parameter.Name}'): give a value or a formula, not both.");
            else if (parameter.Value != null && parameter.Spec?.Friendly != null)
            {
                var (_, valueError) = ConvertValue(parameter.Spec.Friendly, parameter.Value);
                if (valueError != null) errors.Add($"{source} ('{parameter.Name}'): {valueError}");
            }
            spec.Parameters.Add(parameter);
        }
        if (spec.Parameters.Count > MaxParameters)
            errors.Add($"{spec.Parameters.Count} parameters were requested; at most {MaxParameters} per family.");
    }

    private static void ReadLabels(IReadOnlyDictionary<string, object?> args, string key, DetailFamilySpec spec, string defaultLabelFamily, List<string> errors)
    {
        var index = 0;
        foreach (var item in Items(args, key, errors))
        {
            var source = $"{key}[{index++}]";
            if (item == null) continue;
            var text = item["text"] is JValue { Type: JTokenType.String } t ? ((string?)t ?? string.Empty) : Text(item["text"]);
            var parameterName = Text(item["parameter"]);
            if (parameterName.Length > 0 && text.Trim().Length > 0)
            {
                errors.Add($"{source} has both text and parameter: fixed text takes text, a label that shows a parameter takes parameter.");
                continue;
            }
            if (parameterName.Length == 0 && text.Trim().Length == 0) { errors.Add($"{source}.text is empty."); continue; }
            if (text.Length > 1000) { errors.Add($"{source}.text is longer than 1000 characters."); continue; }
            var position = Point(item["position"] ?? item["point"], source + ".position", errors);
            if (position == null) continue;

            var label = new DetailLabelSpec
            {
                Source = source,
                Text = text,
                Position = position.Value,
                TextType = Text(item["textType"]),
                Alignment = Text(item["alignment"]).ToLowerInvariant() switch
                {
                    "" or "left" => "left",
                    "center" or "centre" or "middle" => "center",
                    "right" => "right",
                    var other => other
                },
                RotationDeg = Num(item["rotation"] ?? item["rotationDeg"]) ?? 0,
                Parameter = parameterName,
                LabelType = Text(item["labelType"]),
                LabelParameter = Text(item["labelParameter"]),
                VisibleParameter = Visible(item)
            };
            if (parameterName.Length > 0)
            {
                var family = Text(item["labelFamily"]);
                label.LabelFamily = family.Length > 0 ? family : defaultLabelFamily;
            }
            if (label.Alignment is not ("left" or "center" or "right"))
            {
                errors.Add($"{source}.alignment '{label.Alignment}' is not supported: left | center | right.");
                continue;
            }
            var size = Num(item["size"] ?? item["sizeMm"]);
            if (size != null && (size <= 0 || size > MaxLabelSizeMm))
            {
                errors.Add($"{source}.size must be between 0 and {MaxLabelSizeMm} mm (text height on paper).");
                continue;
            }
            label.SizeMm = size;
            spec.Labels.Add(label);
        }
        if (spec.Labels.Count > MaxLabels)
            errors.Add($"{spec.Labels.Count} labels were requested; at most {MaxLabels} per family.");
    }

    public static string? CheckParameterName(string name)
    {
        if (name != name.Trim()) return $"The parameter name '{name}' starts or ends with a space.";
        var bad = name.Where(c => ForbiddenParameterCharacters.IndexOf(c) >= 0 || char.IsControl(c)).Distinct().ToList();
        if (bad.Count > 0) return $"The parameter name '{name}' contains characters Revit refuses: {string.Join(" ", bad)}.";
        if (name.Length > 200) return "The parameter name is longer than 200 characters.";
        return null;
    }

    /// <summary>Where a parameter goes when no group is given: text in Text, sizes in Dimensions, the rest in Data.</summary>
    public static string DefaultGroupKey(string? friendly) => friendly switch
    {
        "text" or "multilineText" or "url" => "Text",
        "length" or "area" or "volume" or "angle" or "slope" => "Geometry",
        "yesNo" => "Graphics",
        _ => "Data"
    };

    /// <summary>
    /// A value given for a parameter of type <paramref name="friendly"/>, in Revit's internal units: lengths in mm,
    /// areas in m², volumes in m³ and angles in degrees become feet, square feet, cubic feet and radians. Returns a
    /// string, int or double, or an error.
    /// </summary>
    public static (object? Value, string? Error) ConvertValue(string friendly, JToken value)
    {
        switch (friendly)
        {
            case "text":
            case "multilineText":
            case "url":
                return (value.Type == JTokenType.String ? (string?)value ?? string.Empty : value.ToString(Newtonsoft.Json.Formatting.None), null);
            case "yesNo":
            {
                var flag = Flag(value);
                return flag == null ? (null, "a yesNo value must be true or false.") : (flag.Value ? 1 : 0, null);
            }
            case "integer":
            case "numberOfPoles":
            {
                var number = Num(value);
                if (number == null || Math.Abs(number.Value - Math.Round(number.Value)) > 1e-9 || Math.Abs(number.Value) > int.MaxValue)
                    return (null, "an integer value must be a whole number.");
                return ((int)Math.Round(number.Value), null);
            }
            case "number":
            case "currency":
            case "length":
            case "area":
            case "volume":
            case "angle":
            {
                var number = Num(value);
                if (number == null || double.IsNaN(number.Value) || double.IsInfinity(number.Value))
                    return (null, $"a {friendly} value must be a number{UnitHint(friendly)}.");
                return (friendly switch
                {
                    "length" => number.Value / MmPerFoot,
                    "area" => number.Value * SquareFeetPerSquareMetre,
                    "volume" => number.Value * CubicFeetPerCubicMetre,
                    "angle" => number.Value * Math.PI / 180,
                    _ => number.Value
                }, null);
            }
            default:
                return (null, $"values of type {friendly} cannot be set here; leave the value out, or give a formula.");
        }
    }

    private static string UnitHint(string friendly) => friendly switch
    {
        "length" => " (mm)",
        "area" => " (m²)",
        "volume" => " (m³)",
        "angle" => " (degrees)",
        _ => string.Empty
    };

    /// <summary>
    /// Groups the labels into Generic Annotation families: one per distinct text, size, text type and alignment, named
    /// "{familyName}_Text{n}" in first-use order. Rotation and position belong to the instances.
    /// </summary>
    public static List<DetailLabelGroup> GroupLabels(DetailFamilySpec spec, List<string> errors)
    {
        var groups = new List<DetailLabelGroup>();
        var byKey = new Dictionary<string, DetailLabelGroup>(StringComparer.Ordinal);
        foreach (var label in spec.TextLabels)
        {
            var key = string.Join("\u001f", label.Text, label.TextType.ToLowerInvariant(),
                label.SizeMm?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty, label.Alignment);
            if (!byKey.TryGetValue(key, out var group))
            {
                group = new DetailLabelGroup { FamilyName = $"{spec.FamilyName}_Text{groups.Count + 1}", Template = label };
                byKey[key] = group;
                groups.Add(group);
            }
            group.Instances.Add(label);
        }
        if (groups.Count > MaxLabelFamilies)
            errors.Add($"The labels need {groups.Count} different texts; at most {MaxLabelFamilies} per family.");
        foreach (var group in groups)
        {
            var nameError = CheckFamilyName(group.FamilyName);
            if (nameError != null) { errors.Add($"Label family: {nameError}"); break; }
        }
        return groups;
    }

    /// <summary>
    /// The shared parameter file's definition a parameter names: by guid, else by name (and sharedGroup when several
    /// groups hold that name). Never proposes creating one: a missing definition is an error naming close matches.
    /// </summary>
    public static (SharedDefinitionInfo? Definition, string? Error) MatchSharedDefinition(FamilyParameterSpec parameter,
        IReadOnlyCollection<SharedDefinitionInfo> definitions, string fileLabel)
    {
        const string never = "This tool never creates shared parameters: add the definition to the file in Revit (Manage > Shared Parameters), or make it a family parameter (shared=false with a dataType).";
        if (parameter.SharedGuid.Length > 0)
        {
            var byGuid = definitions.FirstOrDefault(d => string.Equals(d.Guid, parameter.SharedGuid, StringComparison.OrdinalIgnoreCase));
            if (byGuid == null)
                return (null, $"{parameter.Source}: no definition with guid {parameter.SharedGuid} in {fileLabel}. {never}");
            if (parameter.Name.Length > 0 && !string.Equals(byGuid.Name, parameter.Name, StringComparison.Ordinal))
                return (null, $"{parameter.Source}: guid {parameter.SharedGuid} is '{byGuid.Name}' in {fileLabel}, not '{parameter.Name}'.");
            return (byGuid, null);
        }

        var byName = definitions.Where(d => string.Equals(d.Name, parameter.Name, StringComparison.Ordinal)).ToList();
        if (byName.Count == 0)
            byName = definitions.Where(d => string.Equals(d.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (parameter.SharedGroup.Length > 0)
            byName = byName.Where(d => string.Equals(d.Group, parameter.SharedGroup, StringComparison.OrdinalIgnoreCase)).ToList();

        if (byName.Count == 1) return (byName[0], null);
        if (byName.Count > 1)
            return (null, $"{parameter.Source}: '{parameter.Name}' is in several groups of {fileLabel} " +
                          $"({string.Join(", ", byName.Select(d => d.Group).Distinct())}); pass sharedGroup or guid.");

        var needle = ParameterSpecMap.Normalize(parameter.Name);
        var close = definitions
            .Where(d => needle.Length > 0 && (ParameterSpecMap.Normalize(d.Name).Contains(needle) || needle.Contains(ParameterSpecMap.Normalize(d.Name))))
            .Select(d => $"'{d.Name}' ({d.Group})")
            .Distinct()
            .Take(5)
            .ToList();
        return (null, $"{parameter.Source}: '{parameter.Name}'{(parameter.SharedGroup.Length > 0 ? $" (group {parameter.SharedGroup})" : string.Empty)} " +
                      $"is not in {fileLabel}{(close.Count > 0 ? $"; did you mean {string.Join(", ", close)}?" : ".")} {never}");
    }

    /// <summary>Distinct project text types the labels ask for.</summary>
    public static List<string> LabelTextTypeNames(DetailFamilySpec spec) => spec.TextLabels
        .Select(l => l.TextType)
        .Where(n => n.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static string Text(JToken? token) =>
        token == null || token.Type == JTokenType.Null ? string.Empty
        : token.Type == JTokenType.String ? ((string?)token ?? string.Empty).Trim()
        : token.ToString(Newtonsoft.Json.Formatting.None).Trim();

    private static bool? Flag(JToken? token)
    {
        if (token == null || token.Type == JTokenType.Null) return null;
        if (token.Type == JTokenType.Boolean) return (bool)token;
        if (token.Type == JTokenType.Integer) return (long)token != 0;
        return ((string?)token ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "1" or "jah" => true,
            "false" or "no" or "0" or "ei" => false,
            _ => null
        };
    }
}
