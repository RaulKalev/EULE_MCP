using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace RevitMCP.Benchmark;

/// <summary>
/// Drives the real bridge over stdio with the official MCP client: one bridge process per tool
/// profile, exactly as an MCP client would start it.
/// </summary>
public sealed class BenchmarkRunner : IAsyncDisposable
{
    private readonly string _bridgePath;
    private readonly Dictionary<string, McpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SchemaMeasurement> _schemas = new(StringComparer.OrdinalIgnoreCase);

    public BenchmarkRunner(string bridgePath) => _bridgePath = bridgePath;

    private async Task<McpClient> ClientFor(string profile)
    {
        if (_clients.TryGetValue(profile, out var existing)) return existing;
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = $"RevitMCP.Bridge ({profile})",
            Command = _bridgePath,
            Arguments = ["--client", "Benchmark", "--tool-profile", profile]
        });
        var client = await McpClient.CreateAsync(transport);
        _clients[profile] = client;
        return client;
    }

    public async Task<SchemaMeasurement> MeasureSchema(string profile)
    {
        if (_schemas.TryGetValue(profile, out var cached)) return cached;
        var m = new SchemaMeasurement { Profile = profile };
        try
        {
            var client = await ClientFor(profile);
            var tools = await client.ListToolsAsync();
            // What the model receives: name, description and input schema of every tool.
            var payload = JsonSerializer.Serialize(tools.Select(t => t.ProtocolTool).ToList(), McpJsonUtilities.DefaultOptions);
            m.ToolCount = tools.Count;
            m.SchemaBytes = BenchmarkMath.Utf8Bytes(payload);
            m.SchemaTokens = BenchmarkMath.EstimateTokens(payload);
            m.InstructionTokens = BenchmarkMath.EstimateTokens(client.ServerInstructions);
        }
        catch (Exception ex)
        {
            m.Error = ex.Message;
        }
        _schemas[profile] = m;
        return m;
    }

    public async Task<(StepMeasurement Measurement, JsonNode? Json)> Call(string profile, string tool, JsonObject args)
    {
        var step = new StepMeasurement { Tool = tool };
        JsonNode? json = null;
        var sw = Stopwatch.StartNew();
        try
        {
            var client = await ClientFor(profile);
            var dict = args.ToDictionary(p => p.Key, p => (object?)(p.Value == null ? null : JsonSerializer.SerializeToElement(p.Value)));
            var result = await client.CallToolAsync(tool, dict);
            sw.Stop();
            var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
            var imageBytes = result.Content.OfType<ImageContentBlock>().Sum(c => c.Data.Length);
            step.ElapsedMs = sw.ElapsedMilliseconds;
            step.ResultBytes = BenchmarkMath.Utf8Bytes(text) + imageBytes;
            step.ResultTokens = BenchmarkMath.EstimateTokens(text) + (int)Math.Ceiling(imageBytes / BenchmarkMath.CharsPerToken);
            step.LiveElements = BenchmarkMath.CountLiveElements(tool, text);
            try { json = JsonNode.Parse(text); } catch (JsonException) { json = null; }
            var success = result.IsError != true && (json?["success"]?.GetValue<bool>() ?? true);
            step.Success = success;
            if (!success) step.Error = json?["message"]?.ToString() ?? text.Substring(0, Math.Min(200, text.Length));
        }
        catch (Exception ex)
        {
            sw.Stop();
            step.ElapsedMs = sw.ElapsedMilliseconds;
            step.Success = false;
            step.Error = ex.Message;
        }
        return (step, json);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _clients.Values)
        {
            try { await c.DisposeAsync(); } catch { }
        }
        _clients.Clear();
    }
}
