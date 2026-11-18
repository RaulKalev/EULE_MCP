using System.Globalization;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.ModelFamilies;

/// <summary>
/// Reads the JSON arguments of the model-family tools (#168). Pure. Lengths are millimetres and angles degrees on the
/// wire; <see cref="MmPerFoot"/> converts to Revit's feet. Every reader adds a problem naming <c>source</c> (for
/// example <c>extrusions[0].depth</c>) instead of throwing.
/// </summary>
public static class ModelFamilyJson
{
    public const double MmPerFoot = 304.8;

    public static double ToFeet(double mm) => mm / MmPerFoot;
    public static double ToMm(double feet) => Math.Round(feet * MmPerFoot, 4);

    /// <summary>An argument as a token: a JToken as it is, a JSON string parsed, anything else converted.</summary>
    public static JToken? Token(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var raw) || raw == null) return null;
        switch (raw)
        {
            case JToken { Type: JTokenType.Null }:
                return null;
            case JValue { Type: JTokenType.String } v:
                return ParseOrKeep((string?)v ?? string.Empty);
            case JToken t:
                return t;
            case string s:
                return ParseOrKeep(s);
            default:
                try { return JToken.FromObject(raw); }
                catch { return null; }
        }
    }

    private static JToken? ParseOrKeep(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return null;
        if (trimmed[0] is '[' or '{')
        {
            try { return JToken.Parse(trimmed); }
            catch { /* a plain string that happens to start with a bracket */ }
        }
        return new JValue(text);
    }

    /// <summary>An array argument's objects. A single object counts as a list of one; anything else is a problem.</summary>
    public static List<JObject> Objects(IReadOnlyDictionary<string, object?> args, string key, List<string> problems)
    {
        var result = new List<JObject>();
        var token = Token(args, key);
        if (token == null) return result;
        if (token is JObject single)
        {
            result.Add(single);
            return result;
        }
        if (token is not JArray array)
        {
            problems.Add($"{key}: must be a list of objects.");
            return result;
        }
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is JObject item) result.Add(item);
            else problems.Add($"{key}[{i}]: must be an object.");
        }
        return result;
    }

    public static JToken? Get(JObject obj, string key)
    {
        var token = obj.GetValue(key, StringComparison.OrdinalIgnoreCase);
        return token == null || token.Type == JTokenType.Null ? null : token;
    }

    public static bool Has(JObject obj, string key) => Get(obj, key) != null;

    public static string Str(JObject obj, string key, string fallback = "")
    {
        var token = Get(obj, key);
        if (token == null) return fallback;
        return (token.Type == JTokenType.String ? (string?)token : token.ToString(Newtonsoft.Json.Formatting.None))?.Trim() ?? fallback;
    }

    public static bool? Bool(JObject obj, string key, string source, List<string> problems)
    {
        var token = Get(obj, key);
        if (token == null) return null;
        if (token.Type == JTokenType.Boolean) return (bool)token;
        if (token.Type == JTokenType.String && bool.TryParse((string?)token, out var parsed)) return parsed;
        problems.Add($"{source}.{key}: must be true or false.");
        return null;
    }

    public static double? Number(JObject obj, string key, string source, List<string> problems)
    {
        var token = Get(obj, key);
        if (token == null) return null;
        var value = AsNumber(token);
        if (value == null) problems.Add($"{source}.{key}: must be a number.");
        return value;
    }

    public static double? AsNumber(JToken? token)
    {
        if (token == null) return null;
        if (token.Type is JTokenType.Integer or JTokenType.Float)
        {
            var value = token.Value<double>();
            return double.IsNaN(value) || double.IsInfinity(value) ? null : value;
        }
        if (token.Type == JTokenType.String &&
            double.TryParse(((string?)token)?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
            !double.IsNaN(parsed) && !double.IsInfinity(parsed))
            return parsed;
        return null;
    }

    public static long? AsId(JToken? token)
    {
        if (token == null) return null;
        if (token.Type == JTokenType.Integer) return token.Value<long>();
        if (token.Type == JTokenType.String &&
            long.TryParse(((string?)token)?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return null;
    }

    /// <summary>A point <c>[x, y]</c> or <c>[x, y, z]</c> in millimetres (z defaults to 0), returned in millimetres.</summary>
    public static double[]? Point(JToken? token, string source, List<string> problems, bool allow2D = true)
    {
        if (token is not JArray array || array.Count < (allow2D ? 2 : 3) || array.Count > 3)
        {
            problems.Add($"{source}: must be {(allow2D ? "[x, y] or [x, y, z]" : "[x, y, z]")} in millimetres.");
            return null;
        }
        var result = new double[3];
        for (var i = 0; i < array.Count; i++)
        {
            var value = AsNumber(array[i]);
            if (value == null)
            {
                problems.Add($"{source}[{i}]: must be a number.");
                return null;
            }
            result[i] = value.Value;
        }
        return result;
    }

    public static double[]? Point(JObject obj, string key, string source, List<string> problems, bool required = true)
    {
        var token = Get(obj, key);
        if (token == null)
        {
            if (required) problems.Add($"{source}.{key} is required: [x, y, z] in millimetres.");
            return null;
        }
        return Point(token, $"{source}.{key}", problems);
    }

    public static JArray PointMm(double xFeet, double yFeet, double zFeet) => new(ToMm(xFeet), ToMm(yFeet), ToMm(zFeet));

    /// <summary>Unknown keys of an operation object, as problems: a misspelt key must not be dropped silently.</summary>
    public static void CheckKeys(JObject obj, string source, List<string> problems, params string[] known)
    {
        foreach (var property in obj.Properties())
            if (!known.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                problems.Add($"{source}.{property.Name}: unknown key. Known: {string.Join(", ", known)}.");
    }

    /// <summary>Close names for a "did you mean" hint.</summary>
    public static List<string> Suggest(string wanted, IEnumerable<string> known, int max = 5)
    {
        var w = wanted.Trim();
        return known.Where(k => k.Length > 0)
            .Select(k => (Name: k, Score: Score(w, k)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Take(max).Select(x => x.Name).ToList();
    }

    private static readonly string[] MeaninglessPrefixes = { "OST_", "RBS_" };

    private static int SharedPrefixLength(string wanted, string known)
    {
        foreach (var prefix in MeaninglessPrefixes)
            if (wanted.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && known.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return prefix.Length;
        return 0;
    }

    private static int Score(string wanted, string known)
    {
        if (string.Equals(wanted, known, StringComparison.OrdinalIgnoreCase)) return 100;
        if (known.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0 || wanted.IndexOf(known, StringComparison.OrdinalIgnoreCase) >= 0) return 50;
        // A shared built-in prefix (OST_, RBS_) says nothing: every such name would look close to every other.
        var skip = SharedPrefixLength(wanted, known);
        var common = skip;
        while (common < wanted.Length && common < known.Length &&
               char.ToLowerInvariant(wanted[common]) == char.ToLowerInvariant(known[common])) common++;
        return common - skip >= 3 ? common - skip : 0;
    }
}
