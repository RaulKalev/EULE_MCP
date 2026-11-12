using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Families;

/// <summary>One value a family type gets: a parameter name and the value as given (same units as parameters[].value).</summary>
public sealed class DetailTypeValue
{
    public string Source { get; set; } = string.Empty;
    public string Parameter { get; set; } = string.Empty;
    public JToken Value { get; set; } = JValue.CreateNull();
}

/// <summary>A family type revit_create_detail_family / revit_edit_detail_family creates (or updates) with its values.</summary>
public sealed class DetailFamilyTypeSpec
{
    /// <summary>Where it came from in the request, for messages: types[0], …</summary>
    public string Source { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<DetailTypeValue> Values { get; } = new();
}

/// <summary>
/// A parameter the family already has, as an edit sees it: read from the family document on the Revit side, checked
/// here. Pure: no Revit types.
/// </summary>
public sealed class KnownFamilyParameter
{
    public string Name { get; set; } = string.Empty;
    /// <summary>text, length, yesNo, … when the map knows the data type; null otherwise (its values cannot be set here).</summary>
    public string? Friendly { get; set; }
    /// <summary>Revit's label for the data type, for messages.</summary>
    public string DataTypeLabel { get; set; } = string.Empty;
    public bool IsInstance { get; set; }
    public bool IsShared { get; set; }
    /// <summary>A built-in parameter of the family (Type Comments, Description, …): never removed.</summary>
    public bool IsBuiltIn { get; set; }
    public string Formula { get; set; } = string.Empty;
    /// <summary>Elements whose parameters are linked to this one (visibility, nested labels, dimension labels).</summary>
    public List<long> AssociatedElementIds { get; } = new();
}

/// <summary>Family types of revit_create_detail_family and revit_edit_detail_family. Pure: no Revit types, unit tested.</summary>
public static partial class DetailFamilySpecParser
{
    public const int MaxTypes = 100;
    public const int MaxTypeValues = 200;

    private static void ReadTypes(IReadOnlyDictionary<string, object?> args, string key, DetailFamilySpec spec, List<string> errors)
    {
        var index = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Items(args, key, errors))
        {
            var source = $"{key}[{index++}]";
            if (item == null) continue;
            var type = new DetailFamilyTypeSpec { Source = source, Name = Text(item["name"]) };
            if (type.Name.Length == 0) { errors.Add($"{source} needs a name."); continue; }
            var nameError = CheckFamilyName(type.Name);
            if (nameError != null) { errors.Add($"{source}: {nameError.Replace("family name", "type name")}"); continue; }
            if (!seen.Add(type.Name)) { errors.Add($"{source}: the type '{type.Name}' is listed twice."); continue; }

            var values = item["values"];
            if (values != null && values.Type != JTokenType.Null)
            {
                if (values is not JObject valueObject)
                {
                    errors.Add($"{source}.values must be an object: {{\"<parameter name>\": value, …}}.");
                    continue;
                }
                foreach (var property in valueObject.Properties())
                {
                    var valueSource = $"{source}.values['{property.Name}']";
                    if (property.Name.Trim().Length == 0) { errors.Add($"{source}.values has an empty parameter name."); continue; }
                    if (property.Value.Type == JTokenType.Null)
                    {
                        errors.Add($"{valueSource} is null: give a value, or leave the parameter out.");
                        continue;
                    }
                    type.Values.Add(new DetailTypeValue { Source = valueSource, Parameter = property.Name.Trim(), Value = property.Value });
                }
                if (type.Values.Count > MaxTypeValues)
                    errors.Add($"{source} sets {type.Values.Count} values; at most {MaxTypeValues} per type.");
            }
            spec.Types.Add(type);
        }
        if (spec.Types.Count > MaxTypes)
            errors.Add($"{spec.Types.Count} types were requested; at most {MaxTypes} per family.");
    }

    /// <summary>Checks the types' values against <c>parameters</c> (a new family).</summary>
    public static void CheckTypeValues(DetailFamilySpec spec, List<string> errors) => CheckTypeValues(spec, errors, null, null);

    /// <summary>
    /// Each value of <c>types</c> must name a parameter of <c>parameters</c> or, when editing, one the family already
    /// has (and the edit does not remove); it must not have a formula, and must convert like parameters[].value. A
    /// shared parameter given without dataType takes its type from the file, so its values are checked on the Revit side.
    /// </summary>
    public static void CheckTypeValues(DetailFamilySpec spec, List<string> errors,
        IReadOnlyDictionary<string, KnownFamilyParameter>? existing, ISet<string>? removed)
    {
        if (spec.Types.Count == 0) return;
        var byName = spec.Parameters.Where(p => p.Name.Length > 0)
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var where = existing == null ? "parameters" : "parameters or the family";

        foreach (var type in spec.Types)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in type.Values)
            {
                if (!seen.Add(value.Parameter))
                {
                    errors.Add($"{type.Source}: '{value.Parameter}' is given twice.");
                    continue;
                }
                string? friendly;
                if (byName.TryGetValue(value.Parameter, out var parameter))
                {
                    if (parameter.Formula.Length > 0)
                    {
                        errors.Add($"{value.Source}: '{value.Parameter}' has a formula, which sets its value in every type; give no value for it.");
                        continue;
                    }
                    friendly = parameter.Spec?.Friendly;
                    if (friendly == null) continue; // shared, type from the file: checked on the Revit side
                }
                else if (existing != null && existing.TryGetValue(value.Parameter, out var known) && removed?.Contains(value.Parameter) != true)
                {
                    if (known.Formula.Length > 0)
                    {
                        errors.Add($"{value.Source}: the family's parameter '{known.Name}' has the formula '{known.Formula}', which sets its value in every type; give no value for it.");
                        continue;
                    }
                    friendly = known.Friendly;
                    if (friendly == null)
                    {
                        errors.Add($"{value.Source}: values of type {(known.DataTypeLabel.Length > 0 ? known.DataTypeLabel : "unknown")} cannot be set here.");
                        continue;
                    }
                }
                else
                {
                    var names = byName.Keys.Concat(existing?.Keys.Where(k => removed?.Contains(k) != true) ?? Enumerable.Empty<string>());
                    var close = SuggestNames(value.Parameter, names);
                    errors.Add($"{value.Source}: '{value.Parameter}' is not in {where}{RemovedNote(value.Parameter, removed)}" +
                               $"{(close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(n => $"'{n}'"))}?" : ": add it to parameters first.")}");
                    continue;
                }

                var (_, valueError) = ConvertValue(friendly, value.Value);
                if (valueError != null) errors.Add($"{value.Source}: {valueError}");
            }
        }
    }

    /// <summary>" (the same edit removes it)" when <paramref name="name"/> is among the removed parameters.</summary>
    private static string RemovedNote(string name, ISet<string>? removed) =>
        removed?.Contains(name) == true ? " (remove.parameters removes it; list it in parameters to add it again)" : string.Empty;

    /// <summary>Close names for a name that was not found: containment either way, or a shared prefix of four characters.</summary>
    public static List<string> SuggestNames(string requested, IEnumerable<string> names, int max = 5)
    {
        var needle = requested.Trim();
        if (needle.Length == 0) return new List<string>();
        static int Prefix(string a, string b)
        {
            var i = 0;
            while (i < a.Length && i < b.Length && char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[i])) i++;
            return i;
        }
        return names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => n.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 || needle.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0
                        || Prefix(n, needle) >= Math.Min(4, needle.Length))
            .OrderByDescending(n => Prefix(n, needle))
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
    }
}
