using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using RevitMCP.Bridge;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>Numbers and booleans sent for string parameters of bridge tools (H9).</summary>
public class ToolArgumentCoercionTests
{
    // Shaped like the bridge's tools: nullable string ids and names next to real numbers, booleans and string arrays.
    [Description("test tool")]
    public static string Query(
        string? id = null,
        string? nameContains = null,
        int pageSize = 50,
        double? toleranceMm = null,
        bool compact = false,
        string[]? names = null) =>
        JsonSerializer.Serialize(new { id, nameContains, pageSize, toleranceMm, compact, names });

    private static readonly MethodInfo QueryMethod = typeof(ToolArgumentCoercionTests).GetMethod(nameof(Query))!;

    /// <summary>The input schema the SDK advertises for <see cref="Query"/>, exactly as the bridge builds it.</summary>
    private static JsonElement Schema() => McpServerTool.Create(QueryMethod, target: null).ProtocolTool.InputSchema;

    private static Dictionary<string, JsonElement> Args(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    /// <summary>Binds the arguments as the SDK does (AIFunctionFactory with the MCP serializer options) and runs the method.</summary>
    private static async Task<JsonElement> Invoke(IDictionary<string, JsonElement> args)
    {
        var function = AIFunctionFactory.Create(QueryMethod, target: null,
            new AIFunctionFactoryOptions { SerializerOptions = McpJsonUtilities.DefaultOptions });
        var result = await function.InvokeAsync(new AIFunctionArguments(args.ToDictionary(p => p.Key, p => (object?)p.Value)));
        var text = result is JsonElement e && e.ValueKind == JsonValueKind.String ? e.GetString()! : result!.ToString()!;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task NumericId_ForAStringParameter_FailsWithoutCoercion_AndBindsWithIt()
    {
        var args = Args("{\"id\": 2206191, \"nameContains\": 1.10, \"pageSize\": 20}");

        await Assert.ThrowsAnyAsync<JsonException>(() => Invoke(args));   // what the testers saw

        var changed = new List<string>();
        var coerced = ToolArgumentCoercion.Coerce(args, Schema(), changed)!;
        var bound = await Invoke(coerced);
        Assert.Equal("2206191", bound.GetProperty("id").GetString());
        Assert.Equal("1.10", bound.GetProperty("nameContains").GetString());   // the exact JSON text, not 1.1
        Assert.Equal(20, bound.GetProperty("pageSize").GetInt32());
        Assert.Equal(["id", "nameContains"], changed.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void NumbersAndBooleans_ForNumberAndBooleanParameters_AreLeftAlone()
    {
        var args = Args("{\"pageSize\": 20, \"toleranceMm\": 2.5, \"compact\": true, \"id\": \"12\"}");
        Assert.Same(args, ToolArgumentCoercion.Coerce(args, Schema()));
    }

    [Fact]
    public async Task BooleanForAString_AndNumbersInAStringArray_BecomeStrings()
    {
        var coerced = ToolArgumentCoercion.Coerce(Args("{\"nameContains\": true, \"names\": [\"a\", 7, 1.5]}"), Schema())!;
        var bound = await Invoke(coerced);
        Assert.Equal("true", bound.GetProperty("nameContains").GetString());
        Assert.Equal(["a", "7", "1.5"], bound.GetProperty("names").EnumerateArray().Select(n => n.GetString()));
    }

    [Fact]
    public void UnknownArguments_MissingSchema_AndNoArguments_PassThrough()
    {
        var args = Args("{\"other\": 5}");
        Assert.Same(args, ToolArgumentCoercion.Coerce(args, Schema()));
        Assert.Same(args, ToolArgumentCoercion.Coerce(args, default));
        Assert.Null(ToolArgumentCoercion.Coerce(null, Schema()));
    }

    [Fact]
    public void HandWrittenSchemas_WithTypeArraysAndAnyOf()
    {
        var schema = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{" +
            "\"a\":{\"type\":[\"string\",\"null\"]}," +
            "\"b\":{\"anyOf\":[{\"type\":\"string\"},{\"type\":\"null\"}]}," +
            "\"c\":{\"type\":[\"string\",\"integer\"]}," +
            "\"d\":{\"type\":[\"string\",\"integer\"]}}}").RootElement;
        var coerced = ToolArgumentCoercion.Coerce(Args("{\"a\": 1, \"b\": 2, \"c\": 3, \"d\": 3.5}"), schema)!;
        Assert.Equal(JsonValueKind.String, coerced["a"].ValueKind);
        Assert.Equal("2", coerced["b"].GetString());
        Assert.Equal(JsonValueKind.Number, coerced["c"].ValueKind);   // integer allowed: unchanged
        Assert.Equal("3.5", coerced["d"].GetString());               // not an integer: the string branch takes it
    }

    [Fact]
    public void UnknownNames_ListsArgumentsTheSchemaDoesNotDeclare()
    {
        // M1: revit_tools_call passed outputFolder to a tool without it and the binder dropped it silently.
        var unknown = ToolArgumentCoercion.UnknownNames(new[] { "id", "PageSize", "outputFolder", "elementIds" }, Schema());
        Assert.Equal(new[] { "outputFolder", "elementIds" }, unknown);
        Assert.Empty(ToolArgumentCoercion.UnknownNames(new[] { "x" }, JsonDocument.Parse("{}").RootElement));
    }
}
