using System.Text.Json;

namespace RevitMCP.Bridge;

/// <summary>
/// Makes tool arguments match the tool's advertised input schema where a client sent a scalar of the wrong JSON kind
/// for a string parameter. Clients send a numeric-looking id or name (<c>id=2206191</c>, <c>nameContains=1.1</c>,
/// <c>panel=101</c>) as a JSON number although the schema says string; the SDK's binder then fails with "The JSON
/// value could not be converted to System.String" before the tool runs. A number or boolean given for a property
/// whose schema allows a string but not that kind becomes a string of its exact JSON text ("1.10" stays "1.10");
/// arrays of strings get the same treatment per item. Nothing else is touched. Pure: no MCP SDK types, unit tested.
/// </summary>
public static class ToolArgumentCoercion
{
    /// <summary>
    /// The arguments with mismatched scalars turned into strings, or <paramref name="arguments"/> itself when nothing
    /// needed changing (also when it or the schema is missing). <paramref name="changed"/> receives the property names.
    /// </summary>
    public static IDictionary<string, JsonElement>? Coerce(
        IDictionary<string, JsonElement>? arguments, JsonElement inputSchema, ICollection<string>? changed = null)
    {
        if (arguments == null || arguments.Count == 0) return arguments;
        if (inputSchema.ValueKind != JsonValueKind.Object ||
            !inputSchema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            return arguments;

        Dictionary<string, JsonElement>? result = null;
        foreach (var pair in arguments)
        {
            if (!properties.TryGetProperty(pair.Key, out var property)) continue;
            if (!TryCoerce(pair.Value, property, out var coerced)) continue;
            result ??= new Dictionary<string, JsonElement>(arguments, StringComparer.Ordinal);
            result[pair.Key] = coerced;
            changed?.Add(pair.Key);
        }
        return result ?? arguments;
    }

    /// <summary>
    /// Argument names the schema does not declare (case-insensitive, like the binder). The binder drops them without a
    /// word, so <c>revit_tools_call</c> reports them (M1). Empty when the schema lists no properties.
    /// </summary>
    public static List<string> UnknownNames(IEnumerable<string> names, JsonElement inputSchema)
    {
        if (inputSchema.ValueKind != JsonValueKind.Object ||
            !inputSchema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            return new List<string>();
        var known = new HashSet<string>(properties.EnumerateObject().Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        return names.Where(n => !known.Contains(n)).ToList();
    }

    private static bool TryCoerce(JsonElement value, JsonElement schema, out JsonElement coerced)
    {
        coerced = value;
        var types = Types(schema);
        if (IsScalar(value))
        {
            if (!types.Contains("string") || types.Contains(KindName(value)) ||
                (value.ValueKind == JsonValueKind.Number && types.Contains("number"))) return false;
            coerced = StringElement(value.GetRawText());
            return true;
        }

        if (value.ValueKind != JsonValueKind.Array || !types.Contains("array")) return false;
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("items", out var items)) return false;
        var itemTypes = Types(items);
        if (!itemTypes.Contains("string")) return false;

        var any = false;
        var raw = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (IsScalar(item) && !itemTypes.Contains(KindName(item)) &&
                !(item.ValueKind == JsonValueKind.Number && itemTypes.Contains("number")))
            {
                raw.Add(JsonSerializer.Serialize(item.GetRawText()));
                any = true;
            }
            else
            {
                raw.Add(item.GetRawText());
            }
        }
        if (!any) return false;
        using var doc = JsonDocument.Parse("[" + string.Join(",", raw) + "]");
        coerced = doc.RootElement.Clone();
        return true;
    }

    private static bool IsScalar(JsonElement value) =>
        value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False;

    /// <summary>The JSON Schema type name of a scalar value: integer for whole numbers, number otherwise, boolean.</summary>
    private static string KindName(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.TryGetInt64(out _) ? "integer" : "number",
        _ => "boolean"
    };

    /// <summary>Every type a schema allows: "type" as a string or an array, and the types of anyOf / oneOf branches.</summary>
    private static HashSet<string> Types(JsonElement schema)
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        if (schema.ValueKind != JsonValueKind.Object) return types;
        if (schema.TryGetProperty("type", out var type))
        {
            if (type.ValueKind == JsonValueKind.String) types.Add(type.GetString()!);
            else if (type.ValueKind == JsonValueKind.Array)
                foreach (var t in type.EnumerateArray())
                    if (t.ValueKind == JsonValueKind.String) types.Add(t.GetString()!);
        }
        foreach (var key in new[] { "anyOf", "oneOf" })
            if (schema.TryGetProperty(key, out var branches) && branches.ValueKind == JsonValueKind.Array)
                foreach (var branch in branches.EnumerateArray())
                    types.UnionWith(Types(branch));
        return types;
    }

    private static JsonElement StringElement(string text)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(text));
        return doc.RootElement.Clone();
    }
}
