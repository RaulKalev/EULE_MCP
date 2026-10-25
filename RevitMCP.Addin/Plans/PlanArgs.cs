using System.Globalization;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Plans;

/// <summary>
/// Reads plan arguments that arrive either as Newtonsoft tokens (after the pipe round trip) or as CLR values
/// (tests, internal requests). Pure.
/// </summary>
internal static class PlanArgs
{
    public static JToken? Token(object? value)
    {
        try
        {
            return value switch
            {
                null => null,
                JToken t => t,
                string s when LooksLikeJson(s) => JToken.Parse(s),
                _ => JToken.FromObject(value)
            };
        }
        catch
        {
            return value is string str ? new JValue(str) : null;
        }
    }

    public static JObject? Object(IDictionary<string, object?> args, string key)
        => args.TryGetValue(key, out var v) ? Token(v) as JObject : null;

    public static string String(JToken? token, string defaultValue = "")
        => token is JValue { Value: not null } v ? Convert.ToString(v.Value, CultureInfo.InvariantCulture) ?? defaultValue : defaultValue;

    public static bool Bool(JToken? token, bool defaultValue)
    {
        if (token is not JValue v || v.Value == null) return defaultValue;
        if (v.Value is bool b) return b;
        return bool.TryParse(Convert.ToString(v.Value, CultureInfo.InvariantCulture), out var parsed) ? parsed : defaultValue;
    }

    public static int? Int(JToken? token)
    {
        if (token is not JValue v || v.Value == null) return null;
        return long.TryParse(Convert.ToString(v.Value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
            ? (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, l))
            : null;
    }

    /// <summary>An integer id, also from a numeric string; null for anything else.</summary>
    public static long? Long(JToken? token)
    {
        if (token is not JValue v || v.Value == null) return null;
        if (v.Value is long l) return l;
        if (v.Value is int i) return i;
        if (v.Value is double or float or decimal) return null;   // coordinates, not ids
        return long.TryParse(Convert.ToString(v.Value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    public static List<string> Strings(JToken? token)
    {
        var result = new List<string>();
        if (token is JArray array)
        {
            foreach (var item in array)
                if (String(item) is { Length: > 0 } s) result.Add(s.Trim());
        }
        else if (String(token) is { Length: > 0 } single)
        {
            result.Add(single.Trim());
        }
        return result;
    }

    public static List<long> Longs(JToken? token)
    {
        var result = new List<long>();
        if (token is JArray array)
        {
            foreach (var item in array)
                if (Long(item) is long id) result.Add(id);
        }
        else if (Long(token) is long single)
        {
            result.Add(single);
        }
        return result;
    }

    private static bool LooksLikeJson(string s)
    {
        var t = s.TrimStart();
        return t.StartsWith("{", StringComparison.Ordinal) || t.StartsWith("[", StringComparison.Ordinal);
    }
}
