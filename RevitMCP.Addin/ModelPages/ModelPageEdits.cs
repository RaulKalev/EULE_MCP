using System.Globalization;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.ModelPages;

/// <summary>One value a page asks to write: an element, a parameter name and the text the user typed.</summary>
public sealed class ModelPageChange
{
    public long Id { get; set; }
    public string Parameter { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;

    /// <summary>What the page showed before the edit; null when the page does not say. Guards against overwriting a newer value.</summary>
    public string? Expected { get; set; }
}

/// <summary>
/// The pure half of editing from a page: reading the requested changes, the text that describes them in
/// the approval queue, and the value rules that do not need Revit. Unit tested in RevitMCP.Tests.
/// </summary>
public static class ModelPageEdits
{
    public const int MaxChanges = 500;
    public const int MaxValueLength = 2000;
    public const int MaxParameterLength = 200;

    /// <summary>
    /// Reads <c>[{id, parameter, value, expected?}]</c>. Returns null with <paramref name="problem"/> when the list
    /// is unusable: a change that cannot be read is an error, not something to drop silently before a write.
    /// </summary>
    public static List<ModelPageChange>? Parse(JToken? token, out string? problem)
    {
        problem = null;
        if (token is not JArray array || array.Count == 0)
        {
            problem = "Name the values to change.";
            return null;
        }
        if (array.Count > MaxChanges)
        {
            problem = $"At most {MaxChanges} values can be changed in one request.";
            return null;
        }

        var changes = new List<ModelPageChange>(array.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array)
        {
            if (item is not JObject o || !TryId(o["id"], out var id))
            {
                problem = "Every change needs an element id.";
                return null;
            }
            var parameter = o["parameter"] is JValue pv && pv.Type == JTokenType.String ? pv.Value<string>()!.Trim() : string.Empty;
            if (parameter.Length == 0 || parameter.Length > MaxParameterLength)
            {
                problem = "Every change needs a parameter name.";
                return null;
            }
            if (!TryText(o["value"], out var value) || value == null || value.Length > MaxValueLength)
            {
                problem = $"The value for '{parameter}' is missing or longer than {MaxValueLength} characters.";
                return null;
            }
            TryText(o["expected"], out var expected);
            if (!seen.Add(id.ToString(CultureInfo.InvariantCulture) + "\n" + parameter))
            {
                problem = $"'{parameter}' of element {id} is listed twice.";
                return null;
            }
            changes.Add(new ModelPageChange { Id = id, Parameter = parameter, Value = value, Expected = expected });
        }
        return changes;
    }

    /// <summary>The line shown in the approval queue and the activity log.</summary>
    public static string Summary(string pageTitle, IReadOnlyList<ModelPageChange> changes)
    {
        var page = string.IsNullOrWhiteSpace(pageTitle) ? "Model page" : $"Page '{Shorten(pageTitle, 40)}'";
        if (changes.Count == 0) return page + ": nothing to change.";
        if (changes.Count == 1)
            return $"{page}: set '{changes[0].Parameter}' to \"{Shorten(changes[0].Value, 40)}\" on element {changes[0].Id}.";

        var parameters = changes.Select(c => c.Parameter).Distinct(StringComparer.Ordinal).ToList();
        var elements = changes.Select(c => c.Id).Distinct().Count();
        var values = changes.Select(c => c.Value).Distinct(StringComparer.Ordinal).ToList();
        var names = string.Join(", ", parameters.Take(3).Select(p => $"'{p}'")) + (parameters.Count > 3 ? $" and {parameters.Count - 3} more" : string.Empty);
        var to = values.Count == 1 ? $" to \"{Shorten(values[0], 40)}\"" : string.Empty;
        return $"{page}: set {names}{to} on {elements} element{(elements == 1 ? "" : "s")} ({changes.Count} values).";
    }

    /// <summary>Yes/no as people type it, in English and Estonian.</summary>
    public static bool TryParseYesNo(string? text, out bool value)
    {
        value = false;
        switch ((text ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "yes": case "y": case "true": case "1": case "jah": case "j": case "on":
                value = true;
                return true;
            case "no": case "n": case "false": case "0": case "ei": case "e": case "off": case "":
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Whether the value in the model is still the one the page showed. Compared as trimmed text, with empty
    /// and missing treated alike; an unstated expectation always passes.
    /// </summary>
    public static bool StillExpected(string? expected, string? current) =>
        expected == null || string.Equals(expected.Trim(), (current ?? string.Empty).Trim(), StringComparison.Ordinal);

    private static bool TryId(JToken? token, out long id)
    {
        id = 0;
        if (token == null) return false;
        if (token.Type == JTokenType.Integer) id = token.Value<long>();
        else if (token.Type != JTokenType.String ||
                 !long.TryParse(token.Value<string>(), NumberStyles.None, CultureInfo.InvariantCulture, out id)) return false;
        return id > 0;
    }

    /// <summary>Text, number or boolean as text; false for anything else. A JSON null gives a null text.</summary>
    private static bool TryText(JToken? token, out string? text)
    {
        text = null;
        if (token == null || token.Type == JTokenType.Null) return true;
        switch (token.Type)
        {
            case JTokenType.String: text = token.Value<string>(); return true;
            case JTokenType.Integer: text = token.Value<long>().ToString(CultureInfo.InvariantCulture); return true;
            case JTokenType.Float: text = token.Value<double>().ToString("R", CultureInfo.InvariantCulture); return true;
            case JTokenType.Boolean: text = token.Value<bool>() ? "Yes" : "No"; return true;
            default: return false;
        }
    }

    private static string Shorten(string text, int max) => text.Length > max ? text.Substring(0, max - 1) + "…" : text;
}
