using System.IO;
using System.IO.Pipes;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using RevitMCP.Core.Models;

namespace RevitMCP.Bridge;

/// <summary>
/// Connects to the Revit plugin named pipe, sends a tool request, and returns the result.
/// A new connection is made per request — this keeps the bridge stateless and avoids
/// lingering connections when the Revit connector is stopped and restarted.
/// </summary>
public class RevitPipeClient
{
    private readonly string _pipeName;
    private readonly int _connectTimeoutMs;
    private readonly int _requestTimeoutMs;

    public RevitPipeClient(IConfiguration config)
    {
        _pipeName = config["RevitMCP:PipeName"] ?? "RKTools.RevitMCP.2026";
        _connectTimeoutMs = int.TryParse(config["RevitMCP:ConnectTimeoutMs"], out var ct) ? ct : 5_000;
        _requestTimeoutMs = int.TryParse(config["RevitMCP:RequestTimeoutMs"], out var rt) ? rt : 30_000;
    }

    public async Task<McpToolResult> SendAsync(
        string toolName,
        Dictionary<string, object?> arguments,
        string clientName = "Claude Code",
        CancellationToken cancellationToken = default)
    {
        var request = new McpToolRequest
        {
            ToolName = toolName,
            Arguments = arguments,
            ClientName = clientName
        };

        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(_connectTimeoutMs, cancellationToken);
        }
        catch (TimeoutException)
        {
            return NotConnected(request.RequestId);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            return NotConnected(request.RequestId);
        }

        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

        var requestJson = JsonConvert.SerializeObject(request);
        await writer.WriteLineAsync(requestJson);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_requestTimeoutMs);

        try
        {
            var responseJson = await reader.ReadLineAsync(cts.Token);
            if (responseJson == null)
                return Error(request.RequestId, "Revit connector closed the connection before returning a result.");

            return JsonConvert.DeserializeObject<McpToolResult>(responseJson)
                   ?? Error(request.RequestId, "Received an empty response from Revit.");
        }
        catch (OperationCanceledException)
        {
            return Error(request.RequestId, "Request timed out. Revit may be busy or the connector has stopped.");
        }
    }

    private static McpToolResult NotConnected(string requestId) => new()
    {
        RequestId = requestId,
        Success = false,
        Message = "Revit is not connected. Open Revit 2026, open a model, and start the Revit MCP Connector."
    };

    private static McpToolResult Error(string requestId, string message) => new()
    {
        RequestId = requestId,
        Success = false,
        Message = message
    };
}
