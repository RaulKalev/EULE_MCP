using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace RevitMCP.Addin.Graph;

/// <summary>One allowlisted routing parameter (#63).</summary>
public sealed class RoutingParameterSpec
{
    /// <summary>Parameter name (lookup and the name stored in the graph).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional shared-parameter GUID; preferred over the name for the lookup.</summary>
    public Guid? Guid { get; set; }

    /// <summary>Category names the parameter is read for; empty = every indexed element.</summary>
    public HashSet<string> Categories { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool AppliesTo(string? category) =>
        Categories.Count == 0 || (category != null && Categories.Contains(category));
}

/// <summary>
/// The bounded allowlist of parameters whose values the graph indexes as routing hints (#63), read
/// from <c>graph.routingParameters</c> in the project, user or company config (the first scope that
/// defines it wins). Entries are a parameter name, or <c>{ "name", "guid"?, "categories"? }</c>.
/// Pure — unit tested in RevitMCP.Tests.
/// </summary>
public static class GraphRoutingParameters
{
    public const string ConfigSection = "graph";
    public const string ConfigKey = "routingParameters";

    /// <summary>At most this many parameters are indexed; extra entries are ignored with a warning.</summary>
    public const int MaxParameters = 20;

    /// <summary>Longer values are truncated: the graph stores routing hints, not documents.</summary>
    public const int MaxValueLength = 200;

    /// <summary>Names that look like secrets are never indexed, even when allowlisted.</summary>
    private static readonly string[] SensitiveFragments =
        { "password", "passwd", "secret", "token", "apikey", "api key", "api_key", "credential" };

    public static (List<RoutingParameterSpec> Specs, List<string> Warnings) Parse(JsonNode? node)
    {
        var specs = new List<RoutingParameterSpec>();
        var warnings = new List<string>();
        if (node == null) return (specs, warnings);
        if (node is not JsonArray array)
        {
            warnings.Add($"{ConfigSection}.{ConfigKey} must be an array of parameter names or {{name, guid, categories}} objects; ignored.");
            return (specs, warnings);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in array)
        {
            var spec = ParseEntry(item, warnings);
            if (spec == null) continue;
            if (IsSensitive(spec.Name))
            {
                warnings.Add($"Routing parameter '{spec.Name}' looks like a secret and is never indexed.");
                continue;
            }
            if (!seen.Add(spec.Name)) continue;
            if (specs.Count >= MaxParameters)
            {
                warnings.Add($"Only the first {MaxParameters} routing parameters are indexed; '{spec.Name}' and later entries were ignored.");
                break;
            }
            specs.Add(spec);
        }
        return (specs, warnings);
    }

    private static RoutingParameterSpec? ParseEntry(JsonNode? item, List<string> warnings)
    {
        switch (item)
        {
            case JsonValue value when value.TryGetValue<string>(out var name):
                return string.IsNullOrWhiteSpace(name) ? null : new RoutingParameterSpec { Name = name.Trim() };

            case JsonObject obj:
            {
                var name = obj["name"]?.ToString()?.Trim();
                Guid? guid = null;
                var guidText = obj["guid"]?.ToString();
                if (!string.IsNullOrWhiteSpace(guidText))
                {
                    if (System.Guid.TryParse(guidText, out var g)) guid = g;
                    else warnings.Add($"Routing parameter guid '{guidText}' is not a GUID; ignored.");
                }
                if (string.IsNullOrEmpty(name))
                {
                    if (guid == null) return null;
                    name = guid.Value.ToString("D");
                }
                var spec = new RoutingParameterSpec { Name = name!, Guid = guid };
                if (obj["categories"] is JsonArray cats)
                    foreach (var c in cats)
                        if (c?.ToString() is { Length: > 0 } cat) spec.Categories.Add(cat.Trim());
                return spec;
            }

            default:
                warnings.Add($"Unrecognised {ConfigKey} entry '{item?.ToJsonString()}'; ignored.");
                return null;
        }
    }

    public static bool IsSensitive(string name)
    {
        var lower = name.ToLowerInvariant();
        return SensitiveFragments.Any(lower.Contains);
    }

    /// <summary>Stable text describing the allowlist; a change forces a full rebuild.</summary>
    public static string Signature(IEnumerable<RoutingParameterSpec> specs) =>
        string.Join("|", specs.Select(s =>
            s.Name + (s.Guid != null ? "#" + s.Guid.Value.ToString("N") : "") +
            (s.Categories.Count > 0 ? "[" + string.Join(",", s.Categories.OrderBy(c => c, StringComparer.OrdinalIgnoreCase)) + "]" : "")));

    /// <summary>The value as stored for display: trimmed, single-line, truncated. Null when blank.</summary>
    public static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var sb = new StringBuilder(value!.Length);
        var space = false;
        foreach (var ch in value.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!space) sb.Append(' ');
                space = true;
            }
            else
            {
                sb.Append(ch);
                space = false;
            }
        }
        var text = sb.ToString();
        return text.Length > MaxValueLength ? text.Substring(0, MaxValueLength) : text;
    }

    /// <summary>The value as matched: <see cref="Clean"/> then lower-cased (invariant), so filters are case-insensitive for any script.</summary>
    public static string? Normalize(string? value) => Clean(value)?.ToLower(CultureInfo.InvariantCulture);
}
