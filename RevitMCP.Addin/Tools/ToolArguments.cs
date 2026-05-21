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

    public static long GetLong(Dictionary<string, object?> args, string key, long defaultValue = 0L)
    {
        if (!args.TryGetValue(key, out var val)) return defaultValue;
        return val switch
        {
            long l => l,
            int i => i,
            JValue jv => jv.Value<long>(),
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
        return GetFiltersWithWarnings(args, key).Items;
    }

    public static ParsedArgumentList<ParameterFilterDto> GetFiltersWithWarnings(Dictionary<string, object?> args, string key = "filters")
    {
        var result = new ParsedArgumentList<ParameterFilterDto>();
        if (!args.TryGetValue(key, out var val) || val == null)
            return result;

        result.HadInput = true;
        var ja = ToJArray(val);
        if (ja == null)
        {
            result.ParseFailed = true;
            result.Warnings.Add($"'{key}' argument was provided but could not be parsed as a JSON array. Query ran without filters.");
            return result;
        }

        result.Items = ja.Select(token => new ParameterFilterDto
        {
            ParameterName = token["parameterName"]?.Value<string>() ?? string.Empty,
            MatchMode = token["matchMode"]?.Value<string>() ?? "ContainsNormalized",
            Operator = token["operator"]?.Value<string>() ?? "equals",
            Value = token["value"]?.Value<string>() ?? string.Empty,
            Scope = token["scope"]?.Value<string>() ?? "InstanceAndType"
        }).Where(f => !string.IsNullOrEmpty(f.ParameterName)).ToList();
        return result;
    }

    public static List<GroupKeyOptions> GetGroupByKeys(Dictionary<string, object?> args, string key = "groupBy")
    {
        return GetGroupByKeysWithWarnings(args, key).Items;
    }

    public static ParsedArgumentList<GroupKeyOptions> GetGroupByKeysWithWarnings(Dictionary<string, object?> args, string key = "groupBy")
    {
        var result = new ParsedArgumentList<GroupKeyOptions>();
        if (!args.TryGetValue(key, out var val) || val == null)
            return result;

        result.HadInput = true;
        var ja = ToJArray(val);
        if (ja == null)
        {
            result.ParseFailed = true;
            result.Warnings.Add($"'{key}' argument was provided but could not be parsed as a JSON array. Query ran without grouping.");
            return result;
        }

        result.Items = ja.Select(token => new GroupKeyOptions
        {
            Type = token["type"]?.Value<string>() ?? "Parameter",
            ParameterName = token["parameterName"]?.Value<string>() ?? string.Empty,
            ParameterMatchMode = token["matchMode"]?.Value<string>() ?? "ContainsNormalized",
            Scope = token["scope"]?.Value<string>() ?? "InstanceAndType"
        }).ToList();
        return result;
    }

    private static JArray? ToJArray(object? value)
    {
        if (value == null)
            return null;

        if (value is JArray ja)
            return ja;

        if (value is string s)
        {
            try
            {
                var parsed = JToken.Parse(s);
                return parsed as JArray;
            }
            catch
            {
                return null;
            }
        }

        if (value is IEnumerable<object> enumerable)
        {
            try { return JArray.FromObject(enumerable); }
            catch { return null; }
        }

        try { return JArray.FromObject(value); }
        catch { return null; }
    }
}

public class ParsedArgumentList<T>
{
    public List<T> Items { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public bool HadInput { get; set; }
    public bool ParseFailed { get; set; }
}
