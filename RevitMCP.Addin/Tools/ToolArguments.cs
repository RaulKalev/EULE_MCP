using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Helpers for safely extracting typed values from the pipe request Arguments dictionary.
/// Values arrive as Newtonsoft.Json.Linq objects after JSON round-trip through the pipe.
/// </summary>
internal static class ToolArguments
{
    public static bool GetBool(Dictionary<string, object?> args, string key, bool defaultValue = false)
    {
        if (!args.TryGetValue(key, out var val)) return defaultValue;
        return val switch
        {
            bool b => b,
            JValue jv => jv.Value<bool>(),
            _ => defaultValue
        };
    }

    public static string GetString(Dictionary<string, object?> args, string key, string defaultValue = "")
    {
        if (!args.TryGetValue(key, out var val)) return defaultValue;
        return val switch
        {
            string s => s,
            JValue jv => jv.Value<string>() ?? defaultValue,
            _ => defaultValue
        };
    }

    public static long[] GetLongArray(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var val)) return [];
        return val switch
        {
            JArray ja => ja.Select(t => t.Value<long>()).ToArray(),
            long[] la => la,
            _ => []
        };
    }
}
