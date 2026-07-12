using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using RevitMCP.Core.Configuration;
using RevitMCP.Core.Instances;
using RevitMCP.Core.Models;

namespace RevitMCP.Bridge;

/// <summary>
/// Connects to the Revit plugin named pipe, sends a tool request, and returns the result.
/// A new connection is made per request — this keeps the bridge stateless and avoids
/// lingering connections when the Revit connector is stopped and restarted.
///
/// Each Revit process hosts its own unique pipe and registers itself in a shared
/// instance registry. When no explicit pipe name is configured, the bridge discovers
/// running instances and routes to them by preference: the user-selected active
/// instance first, then the highest Revit version (2026 before 2024), then the most
/// recently started. The legacy shared pipe name is kept as a final fallback for
/// older add-in builds.
/// </summary>
public class RevitPipeClient
{
    /// <summary>Connect timeout per discovered instance — the pipe server accepts immediately when alive.</summary>
    private const int InstanceConnectTimeoutMs = 2000;

    private readonly string? _explicitPipeName;
    private readonly int _connectTimeoutMs;
    private readonly int _requestTimeoutMs;
    private readonly string _clientName;
    private readonly RevitInstanceRegistry _registry;

    public RevitPipeClient(IConfiguration config)
    {
        _explicitPipeName = config["RevitMCP:PipeName"];
        _connectTimeoutMs = int.TryParse(config["RevitMCP:ConnectTimeoutMs"], out var ct) ? ct : RevitMcpDefaults.ConnectTimeoutMs;
        _requestTimeoutMs = int.TryParse(config["RevitMCP:RequestTimeoutMs"], out var rt) ? rt : RevitMcpDefaults.RequestTimeoutMs;
        _clientName = config["RevitMCP:ClientName"] ?? RevitMcpDefaults.ClientName;
        _registry = new RevitInstanceRegistry();
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

        using var pipe = await ConnectAsync(cancellationToken);
        if (pipe == null)
            return NotConnected(request.RequestId);

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

    /// <summary>
    /// Connects to the preferred Revit instance, trying candidate pipes in order.
    /// Returns null when no instance could be reached.
    /// </summary>
    private async Task<NamedPipeClientStream?> ConnectAsync(CancellationToken cancellationToken)
    {
        var candidates = ResolveCandidatePipeNames();

        for (var i = 0; i < candidates.Count; i++)
        {
            // Discovered instances accept immediately when alive, so use a short timeout for
            // them and reserve the full configured timeout for the last (fallback) candidate.
            var timeoutMs = i == candidates.Count - 1
                ? _connectTimeoutMs
                : Math.Min(_connectTimeoutMs, InstanceConnectTimeoutMs);

            var pipe = new NamedPipeClientStream(".", candidates[i], PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(timeoutMs, cancellationToken);
                return pipe;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException)
            {
                pipe.Dispose();
                if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                    throw;
            }
        }

        return null;
    }

    /// <summary>
    /// Builds the ordered list of pipe names to try. An explicitly configured pipe name
    /// (via --pipe or configuration) always wins; otherwise registered live instances are
    /// preferred (active first, then highest Revit version, then most recent), with the
    /// legacy shared pipe name as a final fallback.
    /// </summary>
    private List<string> ResolveCandidatePipeNames()
    {
        if (!string.IsNullOrWhiteSpace(_explicitPipeName))
            return [_explicitPipeName!];

        var candidates = new List<string>();
        foreach (var instance in DiscoverLiveInstances())
            candidates.Add(instance.PipeName);

        // Legacy fallback for add-in builds that pre-date the instance registry.
        if (!candidates.Contains(RevitMcpDefaults.PipeName))
            candidates.Add(RevitMcpDefaults.PipeName);

        return candidates;
    }

    /// <summary>
    /// Lists registered Revit instances whose process is still alive, ordered by routing
    /// preference. Registrations left behind by crashed Revit processes are pruned.
    /// </summary>
    public List<RevitInstanceInfo> DiscoverLiveInstances()
    {
        var live = new List<RevitInstanceInfo>();
        foreach (var instance in _registry.List())
        {
            if (IsProcessAlive(instance.ProcessId))
                live.Add(instance);
            else
                _registry.Unregister(instance.ProcessId);
        }

        return RevitInstanceRegistry.OrderByPreference(live, _registry.GetActiveProcessId());
    }

    /// <summary>Process id of the user-selected active instance, if any.</summary>
    public int? GetActiveProcessId() => _registry.GetActiveProcessId();

    /// <summary>Marks a registered Revit instance as the active routing target.</summary>
    public bool SelectInstance(int processId) => _registry.SetActive(processId);

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // The process exited between GetProcessById and the HasExited check.
            return false;
        }
        catch
        {
            // If we cannot inspect the process, assume it is alive and let the
            // connection attempt decide.
            return true;
        }
    }

    private static McpToolResult NotConnected(string requestId) => new()
    {
        RequestId = requestId,
        Success = false,
        Message = "Revit is not connected. Open Revit (2024 or 2026), open a model, and start the Revit MCP Connector. " +
                  "If several Revit instances are running, use revit_list_instances to see them and " +
                  "revit_select_instance (or the 'Make This Project Active' button in the plugin) to pick one."
    };

    private static McpToolResult Error(string requestId, string message) => new()
    {
        RequestId = requestId,
        Success = false,
        Message = message
    };
}
