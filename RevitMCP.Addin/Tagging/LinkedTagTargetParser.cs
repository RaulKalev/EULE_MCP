using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Tagging;

/// <summary>One element of a linked model to tag: the link instance in the host and the element id in the linked document.</summary>
public readonly struct LinkedTagTargetId : IEquatable<LinkedTagTargetId>
{
    public LinkedTagTargetId(long linkInstanceId, long elementId)
    {
        LinkInstanceId = linkInstanceId;
        ElementId = elementId;
    }

    public long LinkInstanceId { get; }
    public long ElementId { get; }

    /// <summary>The id the model graph uses for linked nodes.</summary>
    public string Key => $"link:{LinkInstanceId}:{ElementId}";

    public bool Equals(LinkedTagTargetId other) => LinkInstanceId == other.LinkInstanceId && ElementId == other.ElementId;
    public override bool Equals(object? obj) => obj is LinkedTagTargetId other && Equals(other);
    public override int GetHashCode() => unchecked((LinkInstanceId.GetHashCode() * 397) ^ ElementId.GetHashCode());
    public override string ToString() => Key;
}

/// <summary>
/// Reads the linked targets of revit_place_tags (#151): linkInstanceId + linkedElementIds (ids in the linked document,
/// as revit_query_linked_elements returns them), and/or linkedElements — graph ids "link:&lt;instance&gt;:&lt;id&gt;" or
/// {linkInstanceId, elementId} objects. Pure, unit tested.
/// </summary>
public static class LinkedTagTargetParser
{
    public const int MaxLinkedTargets = 2000;

    public static List<LinkedTagTargetId> Parse(IReadOnlyDictionary<string, object?> args, List<string> errors)
    {
        var result = new List<LinkedTagTargetId>();
        var linkInstanceId = Long(Get(args, "linkInstanceId")) ?? 0;

        var ids = Array(Get(args, "linkedElementIds"), "linkedElementIds", errors).ToList();
        if (ids.Count > 0 && linkInstanceId <= 0)
            errors.Add("linkedElementIds needs linkInstanceId: the link instance in the host (revit_list_open_documents or revit_list_clashable_links).");
        foreach (var token in ids)
        {
            var id = Long(token);
            if (id == null || id <= 0) errors.Add($"linkedElementIds: '{token}' is not an element id.");
            else if (linkInstanceId > 0) result.Add(new LinkedTagTargetId(linkInstanceId, id.Value));
        }

        var index = 0;
        foreach (var token in Array(Get(args, "linkedElements"), "linkedElements", errors))
        {
            var where = $"linkedElements[{index++}]";
            if (token.Type == JTokenType.String)
            {
                if (TryParseKey((string?)token ?? string.Empty, out var parsed)) result.Add(parsed);
                else errors.Add($"{where}: '{token}' is not a linked id \"link:<linkInstanceId>:<elementId>\".");
                continue;
            }
            if (token is JObject obj)
            {
                var link = Long(obj["linkInstanceId"]) ?? linkInstanceId;
                var element = Long(obj["elementId"]);
                if (link <= 0 || element == null || element <= 0)
                    errors.Add($"{where} needs linkInstanceId and elementId.");
                else
                    result.Add(new LinkedTagTargetId(link, element.Value));
                continue;
            }
            errors.Add($"{where} must be \"link:<linkInstanceId>:<elementId>\" or {{linkInstanceId, elementId}}.");
        }

        var distinct = result.Distinct().ToList();
        if (distinct.Count > MaxLinkedTargets)
            errors.Add($"{distinct.Count} linked elements were given; at most {MaxLinkedTargets} per call.");
        return distinct;
    }

    /// <summary>"link:12345:678" → (12345, 678).</summary>
    public static bool TryParseKey(string text, out LinkedTagTargetId id)
    {
        id = default;
        var parts = text.Trim().Split(':');
        if (parts.Length != 3 || !string.Equals(parts[0], "link", StringComparison.OrdinalIgnoreCase)) return false;
        if (!long.TryParse(parts[1], out var link) || !long.TryParse(parts[2], out var element) || link <= 0 || element <= 0) return false;
        id = new LinkedTagTargetId(link, element);
        return true;
    }

    private static object? Get(IReadOnlyDictionary<string, object?> args, string key) => args.TryGetValue(key, out var v) ? v : null;

    private static JToken? Token(object? raw) => raw switch
    {
        null => null,
        JToken t => t,
        string s when s.TrimStart().StartsWith("[", StringComparison.Ordinal) => TryParse(s),
        string s => new JValue(s),
        _ => TryFrom(raw)
    };

    private static IEnumerable<JToken> Array(object? raw, string name, List<string> errors)
    {
        var token = Token(raw);
        if (token == null || token.Type == JTokenType.Null) return System.Array.Empty<JToken>();
        if (token is JArray array) return array;
        if (token.Type == JTokenType.String)
        {
            var text = ((string?)token ?? string.Empty).Trim();
            if (text.Length == 0) return System.Array.Empty<JToken>();
            return text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(s => (JToken)new JValue(s));
        }
        if (token.Type == JTokenType.Integer) return new[] { token };
        errors.Add($"{name} must be an array.");
        return System.Array.Empty<JToken>();
    }

    private static long? Long(object? raw)
    {
        var token = Token(raw);
        if (token == null || token.Type == JTokenType.Null) return null;
        if (token.Type == JTokenType.Integer) return (long)token;
        if (token.Type == JTokenType.Float) return (long)(double)token;
        return long.TryParse(token is JValue v ? v.Value?.ToString() : token.ToString(), out var parsed) ? parsed : null;
    }

    private static JToken? TryParse(string s)
    {
        try { return JToken.Parse(s); }
        catch { return null; }
    }

    private static JToken? TryFrom(object raw)
    {
        try { return JToken.FromObject(raw); }
        catch { return null; }
    }
}
