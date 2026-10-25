using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Jobs;

/// <summary>
/// The JSON path subset a job step binds results with (#100), the same forms RevitMCP.Benchmark scenarios use plus an
/// equality filter: <c>$.data.items[*].id</c>, <c>$.data.moved[:10]</c>, <c>$.data.items[0].id</c>,
/// <c>$.data.moves[?(@.status=='Moved')].elementId</c>. Property names match case-insensitively. A path through
/// <c>[*]</c>, <c>[:n]</c> or a filter yields an array. Pure.
/// </summary>
public static class JobJsonPath
{
    private static readonly Regex Segment = new(
        @"\G(?:\.(?<prop>[A-Za-z_][A-Za-z0-9_\-]*)|\[(?<idx>\d+)\]|\[(?<all>\*)\]|\[:(?<take>\d+)\]|\[\?\(@\.(?<fkey>[A-Za-z_][A-Za-z0-9_]*)\s*==\s*(?:'(?<fstr>[^']*)'|(?<fnum>-?\d+(?:\.\d+)?)|(?<fbool>true|false))\)\])",
        RegexOptions.Compiled);

    private static readonly Regex Placeholder = new(@"\{\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}\}", RegexOptions.Compiled);
    private static readonly Regex WholePlaceholder = new(@"^\{\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}\}$", RegexOptions.Compiled);

    private enum Kind { Property, Index, All, Take, Filter }

    private sealed class Step
    {
        public Kind Kind;
        public string Name = string.Empty;
        public int Number;
        public JToken? Equals_;
    }

    /// <summary>True when <paramref name="path"/> is in the supported subset.</summary>
    public static bool TryParse(string? path, out string? error) => Parse(path, out error) != null;

    /// <summary>The value at <paramref name="path"/>, or null when it does not resolve (or a collection is empty).</summary>
    public static JToken? Select(JToken? root, string path)
    {
        var steps = Parse(path, out _);
        if (root == null || steps == null) return null;

        IEnumerable<JToken> current = new[] { root };
        var collecting = false;
        foreach (var step in steps)
        {
            var next = new List<JToken>();
            foreach (var node in current)
            {
                switch (step.Kind)
                {
                    case Kind.Property when node is JObject obj:
                        var prop = obj.Properties().FirstOrDefault(p => string.Equals(p.Name, step.Name, StringComparison.OrdinalIgnoreCase));
                        if (prop != null) next.Add(prop.Value);
                        break;
                    case Kind.Index when node is JArray arr && step.Number < arr.Count:
                        next.Add(arr[step.Number]);
                        break;
                    case Kind.All when node is JArray arr:
                        next.AddRange(arr);
                        collecting = true;
                        break;
                    case Kind.Take when node is JArray arr:
                        next.AddRange(arr.Take(step.Number));
                        collecting = true;
                        break;
                    case Kind.Filter when node is JArray arr:
                        next.AddRange(arr.Where(item => item is JObject o && Matches(o, step)));
                        collecting = true;
                        break;
                }
            }
            current = next;
        }

        var results = current.Where(t => t.Type != JTokenType.Null).ToList();
        if (results.Count == 0) return null;
        return collecting ? new JArray(results.Select(r => r.DeepClone())) : results[0].DeepClone();
    }

    /// <summary>Placeholder names used anywhere in <paramref name="token"/>.</summary>
    public static HashSet<string> Placeholders(JToken token)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var values = token is JContainer container ? container.DescendantsAndSelf().OfType<JValue>() : new[] { token }.OfType<JValue>();
        foreach (var value in values)
        {
            if (value.Type != JTokenType.String) continue;
            foreach (Match m in Placeholder.Matches((string)value.Value!)) names.Add(m.Groups["name"].Value);
        }
        return names;
    }

    /// <summary>
    /// Substitutes bound variables: a string that is exactly <c>"{{name}}"</c> becomes the bound token (an array
    /// stays an array); inside a longer string the value is written as text. Null with the first missing name
    /// when a placeholder is unbound.
    /// </summary>
    public static JObject? Substitute(JObject arguments, IReadOnlyDictionary<string, JToken> variables, out string? missing)
    {
        string? firstMissing = null;
        var copy = (JObject)arguments.DeepClone();
        foreach (var value in copy.Descendants().OfType<JValue>().Where(v => v.Type == JTokenType.String).ToList())
        {
            var text = (string)value.Value!;
            var whole = WholePlaceholder.Match(text);
            if (whole.Success)
            {
                if (variables.TryGetValue(whole.Groups["name"].Value, out var bound)) value.Replace(bound.DeepClone());
                else firstMissing ??= whole.Groups["name"].Value;
                continue;
            }
            if (!Placeholder.IsMatch(text)) continue;
            var replaced = Placeholder.Replace(text, m =>
            {
                if (variables.TryGetValue(m.Groups["name"].Value, out var bound))
                    return bound is JValue v ? Convert.ToString(v.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
                                             : bound.ToString(Newtonsoft.Json.Formatting.None);
                firstMissing ??= m.Groups["name"].Value;
                return m.Value;
            });
            value.Value = replaced;
        }
        missing = firstMissing;
        return firstMissing == null ? copy : null;
    }

    /// <summary>String ids ("123") in an array become numbers, so a binding fits <c>long[] elementIds</c>.</summary>
    public static JToken AsIds(JToken token)
    {
        if (token is not JArray arr) return token;
        return new JArray(arr.Select(item =>
            item is JValue { Type: JTokenType.String } v && long.TryParse((string)v.Value!, out var l) ? new JValue(l) : item.DeepClone()));
    }

    private static List<Step>? Parse(string? path, out string? error)
    {
        error = null;
        var p = (path ?? string.Empty).Trim();
        if (!p.StartsWith("$", StringComparison.Ordinal))
        {
            error = $"'{path}' must start with $";
            return null;
        }
        p = p.Substring(1);
        var steps = new List<Step>();
        var at = 0;
        while (at < p.Length)
        {
            var m = Segment.Match(p, at);
            if (!m.Success || m.Index != at)
            {
                error = $"'{path}': unsupported at '{p.Substring(at)}' (supported: .name, [n], [*], [:n], [?(@.key=='value')])";
                return null;
            }
            if (m.Groups["prop"].Success) steps.Add(new Step { Kind = Kind.Property, Name = m.Groups["prop"].Value });
            else if (m.Groups["idx"].Success) steps.Add(new Step { Kind = Kind.Index, Number = int.Parse(m.Groups["idx"].Value) });
            else if (m.Groups["all"].Success) steps.Add(new Step { Kind = Kind.All });
            else if (m.Groups["take"].Success) steps.Add(new Step { Kind = Kind.Take, Number = int.Parse(m.Groups["take"].Value) });
            else
            {
                JToken value = m.Groups["fstr"].Success ? new JValue(m.Groups["fstr"].Value)
                    : m.Groups["fbool"].Success ? new JValue(m.Groups["fbool"].Value == "true")
                    : new JValue(double.Parse(m.Groups["fnum"].Value, System.Globalization.CultureInfo.InvariantCulture));
                steps.Add(new Step { Kind = Kind.Filter, Name = m.Groups["fkey"].Value, Equals_ = value });
            }
            at += m.Length;
        }
        return steps;
    }

    private static bool Matches(JObject item, Step filter)
    {
        var prop = item.Properties().FirstOrDefault(p => string.Equals(p.Name, filter.Name, StringComparison.OrdinalIgnoreCase));
        if (prop?.Value is not JValue actual || filter.Equals_ is not JValue expected) return false;
        if (expected.Type == JTokenType.Float && actual.Type is JTokenType.Integer or JTokenType.Float)
            return Math.Abs(Convert.ToDouble(actual.Value) - Convert.ToDouble(expected.Value)) < 1e-9;
        if (expected.Type == JTokenType.Boolean) return actual.Type == JTokenType.Boolean && (bool)actual.Value! == (bool)expected.Value!;
        return string.Equals(Convert.ToString(actual.Value, System.Globalization.CultureInfo.InvariantCulture), (string)expected.Value!, StringComparison.Ordinal);
    }
}
