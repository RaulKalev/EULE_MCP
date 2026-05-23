using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Skills;

/// <summary>
/// Type-safe helpers for reading values from a <c>Dictionary&lt;string, object?&gt;</c>
/// that has been round-tripped through Newtonsoft.Json (values may be JValue/JObject).
/// </summary>
internal static class SkillSettings
{
    public static bool GetBool(Dictionary<string, object?> settings, string key, bool defaultValue = false)
    {
        if (!settings.TryGetValue(key, out var raw) || raw is null) return defaultValue;
        return raw switch
        {
            bool b => b,
            JValue jv => jv.Value<bool>(),
            _ when bool.TryParse(raw.ToString(), out var parsed) => parsed,
            _ => defaultValue
        };
    }

    public static double GetDouble(Dictionary<string, object?> settings, string key, double defaultValue = 0.0)
    {
        if (!settings.TryGetValue(key, out var raw) || raw is null) return defaultValue;
        return raw switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            JValue jv => jv.Value<double>(),
            _ when double.TryParse(raw.ToString(), out var parsed) => parsed,
            _ => defaultValue
        };
    }

    public static int GetInt(Dictionary<string, object?> settings, string key, int defaultValue = 0)
    {
        if (!settings.TryGetValue(key, out var raw) || raw is null) return defaultValue;
        return raw switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            JValue jv => jv.Value<int>(),
            _ when int.TryParse(raw.ToString(), out var parsed) => parsed,
            _ => defaultValue
        };
    }

    public static string GetString(Dictionary<string, object?> settings, string key, string defaultValue = "")
    {
        if (!settings.TryGetValue(key, out var raw) || raw is null) return defaultValue;
        return raw switch
        {
            string s => s,
            JValue jv => jv.Value<string>() ?? defaultValue,
            _ => raw.ToString() ?? defaultValue
        };
    }

    public static string[] GetStringArray(Dictionary<string, object?> settings, string key, string[] defaultValue)
    {
        if (!settings.TryGetValue(key, out var raw) || raw is null) return defaultValue;
        if (raw is JArray ja)
            return ja.Values<string>().Where(s => s is not null).Select(s => s!).ToArray();
        if (raw is string[] sa)
            return sa;
        if (raw is IEnumerable<string> es)
            return es.ToArray();
        if (raw is IEnumerable<object> eo)
            return eo.Select(o => o?.ToString() ?? "").Where(s => s.Length > 0).ToArray();
        return defaultValue;
    }
}
