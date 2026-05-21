using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Query;

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

    public static int GetInt(Dictionary<string, object?> args, string key, int defaultValue = 0)
    {
        if (!args.TryGetValue(key, out var val)) return defaultValue;
        return val switch
        {
            int i => i,
            long l => (int)l,
            JValue jv => jv.Value<int>(),
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

    public static string[] GetStringArray(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var val)) return [];
        return val switch
        {
            JArray ja => ja.Select(t => t.Value<string>() ?? string.Empty).ToArray(),
            string[] sa => sa,
            _ => []
        };
    }

    public static List<ParameterFilterDto> GetFilters(Dictionary<string, object?> args, string key = "filters")
    {
        if (!args.TryGetValue(key, out var val) || val is not JArray ja) return new();
        return ja.Select(token => new ParameterFilterDto
        {
            ParameterName = token["parameterName"]?.Value<string>() ?? string.Empty,
            MatchMode = token["matchMode"]?.Value<string>() ?? "Contains",
            Operator = token["operator"]?.Value<string>() ?? "equals",
            Value = token["value"]?.Value<string>() ?? string.Empty,
            Scope = token["scope"]?.Value<string>() ?? "InstanceAndType"
        }).Where(f => !string.IsNullOrEmpty(f.ParameterName)).ToList();
    }

    public static List<GroupKeyOptions> GetGroupByKeys(Dictionary<string, object?> args, string key = "groupBy")
    {
        if (!args.TryGetValue(key, out var val) || val is not JArray ja) return new();
        return ja.Select(token => new GroupKeyOptions
        {
            Type = token["type"]?.Value<string>() ?? "Parameter",
            ParameterName = token["parameterName"]?.Value<string>() ?? string.Empty,
            ParameterMatchMode = token["matchMode"]?.Value<string>() ?? "Contains",
            Scope = token["scope"]?.Value<string>() ?? "InstanceAndType"
        }).ToList();
    }
}
