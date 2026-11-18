using System.Globalization;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.ModelFamilies;

/// <summary>One flex case: a family type and the values set on it before Revit regenerates.</summary>
public sealed class ModelFamilyFlexCase
{
    public string Name { get; set; } = string.Empty;
    /// <summary>The type's name; empty for a family without types, whose current state is flexed.</summary>
    public string Type { get; set; } = string.Empty;
    /// <summary><c>type</c> (the type as it is), <c>range</c>, <c>extremes</c> or <c>case</c>.</summary>
    public string Kind { get; set; } = "type";
    public List<KeyValuePair<string, JToken>> Values { get; } = new();
    public string Source { get; set; } = string.Empty;

    public JObject Inputs()
    {
        var inputs = new JObject();
        foreach (var pair in Values) inputs[pair.Key] = pair.Value.DeepClone();
        return inputs;
    }

    /// <summary>The type and values, independent of order and case, for finding duplicates.</summary>
    public string Key() =>
        Type.ToLowerInvariant() + "|" + string.Join(";", Values
            .OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase)
            .Select(v => v.Key.ToLowerInvariant() + "=" + Canonical(v.Value)));

    private static string Canonical(JToken value)
    {
        var number = value.Type is JTokenType.Integer or JTokenType.Float ? ModelFamilyJson.AsNumber(value) : null;
        return number != null ? number.Value.ToString("0.######", CultureInfo.InvariantCulture) : value.ToString(Newtonsoft.Json.Formatting.None).ToLowerInvariant();
    }
}

/// <summary>The cases a flex runs, with what was dropped as a duplicate.</summary>
public sealed class ModelFamilyFlexPlan
{
    public List<ModelFamilyFlexCase> Cases { get; } = new();
    /// <summary>The types the flex exercises, in the family's order.</summary>
    public List<string> Types { get; } = new();
    public int Requested { get; set; }
    public int Duplicates { get; set; }
}

/// <summary>
/// Generates the cases of a flex (#176): every selected type as it is; each range value alone on each type it applies to
/// (<c>oneAtATime</c>), plus all-min and all-max per type (<c>extremes</c>); then the explicit cases. Duplicates are
/// dropped, names made unique, and more cases than <c>maxCases</c> is a problem. Pure.
/// </summary>
public static class ModelFamilyFlexCases
{
    public const string CurrentStateName = "(current state)";

    /// <param name="knownTypes">The family's type names, in its order.</param>
    /// <param name="currentType">The current type's name, or null when the family has none.</param>
    public static ModelFamilyFlexPlan Build(ModelFamilyFlexSpec spec, IReadOnlyList<string> knownTypes, string? currentType, List<string> problems)
    {
        var plan = new ModelFamilyFlexPlan();
        string? Known(string name) => knownTypes.FirstOrDefault(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));
        bool Exists(string name, string source)
        {
            if (Known(name) != null) return true;
            if (knownTypes.Count == 0)
            {
                problems.Add($"{source}: the family has no types, so '{name}' cannot be flexed; leave types out to flex its current state, or add types with revit_edit_model_family_parameters.");
                return false;
            }
            var close = ModelFamilyJson.Suggest(name, knownTypes);
            problems.Add($"{source}: the family has no type '{name}'" +
                         (close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(n => $"'{n}'"))}?" : $". Types: {string.Join(", ", knownTypes.Select(n => $"'{n}'"))}."));
            return false;
        }

        // Types.
        if (spec.Types != null)
        {
            for (var i = 0; i < spec.Types.Count; i++)
                if (Exists(spec.Types[i], $"types[{i}]")) plan.Types.Add(Known(spec.Types[i])!);
            // The family's order, so results read the same whatever order the caller gave.
            plan.Types.Sort((a, b) => IndexOf(knownTypes, a).CompareTo(IndexOf(knownTypes, b)));
        }
        else if (knownTypes.Count > 0) plan.Types.AddRange(knownTypes);
        else plan.Types.Add(currentType ?? string.Empty);

        foreach (var range in spec.Ranges)
            if (range.Types != null)
                for (var i = 0; i < range.Types.Count; i++)
                    if (Exists(range.Types[i], $"{range.Source}.types[{i}]")) range.Types[i] = Known(range.Types[i])!;

        var all = new List<ModelFamilyFlexCase>();
        foreach (var type in plan.Types)
        {
            var label = type.Length == 0 ? CurrentStateName : type;
            all.Add(new ModelFamilyFlexCase { Name = label, Type = type, Kind = "type", Source = "types" });
            var applicable = spec.Ranges.Where(r => r.Types == null || r.Types.Contains(type, StringComparer.OrdinalIgnoreCase)).ToList();
            foreach (var range in applicable)
                foreach (var value in range.Values())
                {
                    var item = new ModelFamilyFlexCase { Name = $"{label}: {range.Parameter} = {ModelFamilyParameterValues.Describe(value)}", Type = type, Kind = "range", Source = range.Source };
                    item.Values.Add(new KeyValuePair<string, JToken>(range.Parameter, value));
                    all.Add(item);
                }
            if (spec.Combine == ModelFamilyFlexArgs.Extremes && applicable.Count > 0)
            {
                var low = new ModelFamilyFlexCase { Name = $"{label}: all minimum", Type = type, Kind = "extremes", Source = "combine" };
                var high = new ModelFamilyFlexCase { Name = $"{label}: all maximum", Type = type, Kind = "extremes", Source = "combine" };
                foreach (var range in applicable.GroupBy(r => r.Parameter, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
                {
                    var values = range.Values();
                    low.Values.Add(new KeyValuePair<string, JToken>(range.Parameter, values[0]));
                    high.Values.Add(new KeyValuePair<string, JToken>(range.Parameter, values[values.Count - 1]));
                }
                all.Add(low);
                all.Add(high);
            }
        }

        foreach (var explicitCase in spec.Cases)
        {
            string type;
            if (explicitCase.Type != null)
            {
                if (!Exists(explicitCase.Type, $"{explicitCase.Source}.type")) continue;
                type = Known(explicitCase.Type)!;
            }
            else type = currentType ?? string.Empty;
            var item = new ModelFamilyFlexCase
            {
                Name = explicitCase.Name ?? $"{(type.Length == 0 ? CurrentStateName : type)}: {string.Join(", ", explicitCase.Values.Select(v => $"{v.Key} = {ModelFamilyParameterValues.Describe(v.Value)}"))}",
                Type = type,
                Kind = "case",
                Source = explicitCase.Source
            };
            item.Values.AddRange(explicitCase.Values);
            all.Add(item);
        }

        plan.Requested = all.Count;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in all)
        {
            if (!seen.Add(item.Key()))
            {
                plan.Duplicates++;
                continue;
            }
            if (names.TryGetValue(item.Name, out var count))
            {
                names[item.Name] = count + 1;
                item.Name = $"{item.Name} ({count + 1})";
            }
            else names[item.Name] = 1;
            plan.Cases.Add(item);
        }

        if (plan.Cases.Count > spec.MaxCases)
            problems.Add($"maxCases: {plan.Cases.Count} cases were requested ({plan.Types.Count} type(s), {spec.Ranges.Count} range(s), {spec.Cases.Count} explicit case(s)), more than maxCases = {spec.MaxCases}. " +
                         $"Narrow types or a range's types, lower steps, use combine=\"{ModelFamilyFlexArgs.OneAtATime}\", or raise maxCases (up to {ModelFamilyFlexArgs.MaxMaxCases}).");
        return plan;
    }

    private static int IndexOf(IReadOnlyList<string> list, string name)
    {
        for (var i = 0; i < list.Count; i++)
            if (string.Equals(list[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return int.MaxValue;
    }

    /// <summary>The parameters the cases set, each with the first source that sets it (for problems).</summary>
    public static List<(string Parameter, string Source, JToken Value)> Inputs(ModelFamilyFlexPlan plan) =>
        plan.Cases.SelectMany(c => c.Values.Select(v => (v.Key, c.Kind == "range" || c.Kind == "extremes" ? c.Source : $"{c.Source}.values.{v.Key}", v.Value))).ToList();
}
