using System.IO;
using System.IO.Pipes;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using RevitMCP.Core.Configuration;
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
    private readonly string _clientName;

    public RevitPipeClient(IConfiguration config)
    {
        _pipeName = config["RevitMCP:PipeName"] ?? RevitMcpDefaults.PipeName;
        _connectTimeoutMs = int.TryParse(config["RevitMCP:ConnectTimeoutMs"], out var ct) ? ct : RevitMcpDefaults.ConnectTimeoutMs;
        _requestTimeoutMs = int.TryParse(config["RevitMCP:RequestTimeoutMs"], out var rt) ? rt : RevitMcpDefaults.RequestTimeoutMs;
        _clientName = config["RevitMCP:ClientName"] ?? RevitMcpDefaults.ClientName;
    }

    public async Task<McpToolResult> SendAsync(
        string toolName,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        var request = new McpToolRequest
        {
            ToolName = toolName,
            Arguments = arguments,
            ClientName = _clientName
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

        // Start the request timeout before the write so both write and read are covered.
        // This prevents an indefinite hang if the pipe write blocks (e.g. due to sandbox restrictions).
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_requestTimeoutMs);

        var requestJson = JsonConvert.SerializeObject(request);
        try
        {
            await writer.WriteLineAsync(requestJson.AsMemory(), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return Error(request.RequestId, "Request timed out while sending to Revit. Revit may be busy or the connector has stopped.");
        }

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
