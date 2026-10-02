using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RevitMCP.Benchmark;

/// <summary>
/// Pure helpers for the benchmark: token estimates, live-element counting, scenario templating
/// and JSON path bindings. No MCP or Revit dependency — unit tested in RevitMCP.Tests.
/// </summary>
public static class BenchmarkMath
{
    /// <summary>
    /// Characters per token used for estimates. JSON tool schemas and results tokenize at roughly
    /// 3–4 characters per token with current Claude/GPT tokenizers; 4 keeps estimates conservative
    /// (low) and stable across runs. Raw byte counts are always reported next to the estimate.
    /// </summary>
    public const double CharsPerToken = 4.0;

    public static int EstimateTokens(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(text!.Length / CharsPerToken);

    public static int Utf8Bytes(string? text) => string.IsNullOrEmpty(text) ? 0 : Encoding.UTF8.GetByteCount(text);

    /// <summary>Graph tools return routing data, not live Revit elements.</summary>
    public static bool IsGraphTool(string tool) =>
        tool.StartsWith("revit_graph_", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Number of live Revit element records in a tool result: JSON objects carrying an
    /// <c>ElementId</c>/<c>elementId</c> key. Graph results (routing data) count as zero.
    /// Non-JSON results count as zero.
    /// </summary>
    public static int CountLiveElements(string tool, string? resultText)
    {
        if (IsGraphTool(tool) || string.IsNullOrWhiteSpace(resultText)) return 0;
        JsonNode? root;
        try { root = JsonNode.Parse(resultText!); }
        catch (JsonException) { return 0; }
        return CountElementObjects(root);
    }

    private static int CountElementObjects(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var count = obj.Any(p => p.Key is "ElementId" or "elementId") ? 1 : 0;
                foreach (var p in obj) count += CountElementObjects(p.Value);
                return count;
            }
            case JsonArray arr:
                return arr.Sum(CountElementObjects);
            default:
                return 0;
        }
    }

    // ── Bindings and templating ──────────────────────────────────────────────

    private static readonly Regex Placeholder = new(@"""\{\{(?<name>[A-Za-z0-9_]+)\}\}""|\{\{(?<inline>[A-Za-z0-9_]+)\}\}", RegexOptions.Compiled);

    /// <summary>
    /// Replaces <c>{{name}}</c> placeholders in an argument JSON template. A placeholder that is the
    /// whole JSON string value (<c>"{{ids}}"</c>) is replaced by the bound JSON value as-is, so arrays
    /// and numbers keep their type; a placeholder inside a longer string is replaced by the value's text.
    /// Returns null and the missing name when a placeholder is unbound.
    /// </summary>
    public static string? Fill(string template, IReadOnlyDictionary<string, JsonNode?> bindings, out string? missing)
    {
        string? firstMissing = null;
        var result = Placeholder.Replace(template, m =>
        {
            var whole = m.Groups["name"].Success;
            var name = whole ? m.Groups["name"].Value : m.Groups["inline"].Value;
            if (!bindings.TryGetValue(name, out var value) || value == null)
            {
                firstMissing ??= name;
                return m.Value;
            }
            if (whole) return value.ToJsonString();
            return value is JsonValue v && v.TryGetValue<string>(out var s) ? JsonEncodedText.Encode(s).ToString() : value.ToJsonString();
        });
        missing = firstMissing;
        return firstMissing == null ? result : null;
    }

    /// <summary>
    /// Minimal JSON path: <c>$.a.b[0].c</c>, with <c>[*]</c> collecting every array element and
    /// <c>[:N]</c> keeping the first N. Property names are matched case-insensitively. Returns null
    /// when the path does not resolve (or a collection is empty).
    /// </summary>
    public static JsonNode? Select(JsonNode? root, string path)
    {
        if (root == null) return null;
        var tokens = Tokenize(path);
        IEnumerable<JsonNode?> current = new[] { root };
        var collecting = false;

        foreach (var token in tokens)
        {
            var next = new List<JsonNode?>();
            foreach (var node in current)
            {
                if (node == null) continue;
                if (token.Kind == TokenKind.Property)
                {
                    if (node is JsonObject obj)
                    {
                        var hit = obj.FirstOrDefault(p => string.Equals(p.Key, token.Name, StringComparison.OrdinalIgnoreCase));
                        if (hit.Key != null) next.Add(hit.Value);
                    }
                }
                else if (node is JsonArray arr)
                {
                    switch (token.Kind)
                    {
                        case TokenKind.Index when token.Index < arr.Count:
                            next.Add(arr[token.Index]);
                            break;
                        case TokenKind.All:
                            next.AddRange(arr);
                            collecting = true;
                            break;
                        case TokenKind.Take:
                            next.AddRange(arr.Take(token.Index));
                            collecting = true;
                            break;
                    }
                }
            }
            current = next;
        }

        var results = current.Where(n => n != null).ToList();
        if (collecting)
            return results.Count == 0 ? null : new JsonArray(results.Select(n => n!.DeepClone()).ToArray());
        return results.Count == 0 ? null : results[0]!.DeepClone();
    }

    private enum TokenKind { Property, Index, All, Take }

    private readonly struct Token
    {
        public Token(TokenKind kind, string name = "", int index = 0) { Kind = kind; Name = name; Index = index; }
        public TokenKind Kind { get; }
        public string Name { get; }
        public int Index { get; }
    }

    private static List<Token> Tokenize(string path)
    {
        var tokens = new List<Token>();
        var p = path.Trim();
        if (p.StartsWith("$")) p = p.Substring(1);
        foreach (Match m in Regex.Matches(p, @"\.(?<prop>[^.\[]+)|\[(?<idx>\d+)\]|\[(?<all>\*)\]|\[:(?<take>\d+)\]"))
        {
            if (m.Groups["prop"].Success) tokens.Add(new Token(TokenKind.Property, m.Groups["prop"].Value));
            else if (m.Groups["idx"].Success) tokens.Add(new Token(TokenKind.Index, index: int.Parse(m.Groups["idx"].Value)));
            else if (m.Groups["all"].Success) tokens.Add(new Token(TokenKind.All));
            else if (m.Groups["take"].Success) tokens.Add(new Token(TokenKind.Take, index: int.Parse(m.Groups["take"].Value)));
        }
        return tokens;
    }

    /// <summary>
    /// Converts a bound value for use as Revit ids: string ids ("123") become numbers so the result
    /// fits <c>long[] elementIds</c> parameters. Other values pass through unchanged.
    /// </summary>
    public static JsonNode? AsIdArray(JsonNode? value)
    {
        if (value is not JsonArray arr) return value;
        var converted = new JsonArray();
        foreach (var item in arr)
        {
            if (item is JsonValue v && v.TryGetValue<string>(out var s) && long.TryParse(s, out var l))
                converted.Add(l);
            else
                converted.Add(item?.DeepClone());
        }
        return converted;
    }
}
