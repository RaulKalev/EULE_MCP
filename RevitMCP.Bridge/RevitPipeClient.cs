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
/// instance registry. When no explicit pipe name is configured, the bridge never guesses
/// between Revit windows (see <see cref="RevitInstanceRegistry.ResolveRoute"/>): a pinned
/// instance is the only target, a single live instance is used, and several unpinned
/// instances return an error asking for a selection. The legacy shared pipe name is tried
/// only when no instance is registered, for older add-in builds.
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

        var (candidates, targetProcessId, routeError) = ResolveCandidatePipeNames();
        if (routeError != null)
            return Error(request.RequestId, routeError);

        var (connected, accessDenied) = await ConnectAsync(candidates, cancellationToken);
        if (connected == null)
            return accessDenied ? AccessDenied(request.RequestId) : NotConnected(request.RequestId);
        using var pipe = connected;

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
            return TimedOut(request.RequestId, "Request timed out while sending to Revit.", targetProcessId);
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
            return TimedOut(request.RequestId, $"Request timed out after {_requestTimeoutMs / 1000} s without an answer from Revit.", targetProcessId);
        }
    }

    /// <summary>
    /// Connects to the routed Revit instance, trying candidate pipes in order.
    /// Returns a null pipe when no instance could be reached; <c>accessDenied</c> is true when at
    /// least one pipe existed but refused us (typically Revit running as administrator while the
    /// agent is not).
    /// </summary>
    private async Task<(NamedPipeClientStream? Pipe, bool AccessDenied)> ConnectAsync(
        List<string> candidates, CancellationToken cancellationToken)
    {
        var accessDenied = false;

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
                return (pipe, false);
            }
            catch (UnauthorizedAccessException)
            {
                pipe.Dispose();
                accessDenied = true;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException)
            {
                pipe.Dispose();
                if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                    throw;
            }
        }

        return (null, accessDenied);
    }

    /// <summary>
    /// Pipe names to try for this request, or an error when the request must not be sent.
    /// An explicitly configured pipe name (via --pipe or configuration) always wins.
    /// </summary>
    private (List<string> Candidates, int? TargetProcessId, string? Error) ResolveCandidatePipeNames()
    {
        if (!string.IsNullOrWhiteSpace(_explicitPipeName))
            return ([_explicitPipeName!], null, null);

        var route = ResolveRoute();
        if (route.Error != null) return ([], null, route.Error);

        // Legacy fallback for add-in builds that pre-date the instance registry.
        return ([route.Target?.PipeName ?? RevitMcpDefaults.PipeName], route.Target?.ProcessId, null);
    }

    /// <summary>Where requests go right now (see <see cref="RevitInstanceRegistry.ResolveRoute"/>).</summary>
    public InstanceRoute ResolveRoute() =>
        RevitInstanceRegistry.ResolveRoute(DiscoverLiveInstances(), _registry.GetActiveProcessId());

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
                  "Use revit_list_instances to see running instances."
    };

    private static McpToolResult AccessDenied(string requestId) => new()
    {
        RequestId = requestId,
        Success = false,
        Message = "Revit refused the connection (access denied). This usually means Revit is running as administrator " +
                  "while the AI agent is not. Start Revit and the agent at the same privilege level (normally both " +
                  "without 'Run as administrator')."
    };

    /// <summary>
    /// A request the add-in never answered. The bridge cannot see inside Revit, but it can tell whether the
    /// Revit process still answers window messages, which separates "Revit is blocked" from "the connector
    /// is gone" (#87). The add-in itself answers queued requests with status revit_busy before this fires.
    /// </summary>
    private static McpToolResult TimedOut(string requestId, string message, int? processId)
    {
        var responding = ProbeResponding(processId);
        var diagnosis = responding switch
        {
            false => " Revit's main window is not responding: a long operation (synchronize, load, save, regenerate) " +
                     "or a dialog is blocking it. Wait for it to finish, then retry.",
            true => " Revit's window is responding, so the request is probably still running or the connector is stuck. " +
                    "Call revit_get_connection_status (it answers even while Revit is busy) and retry.",
            null => " Revit may be busy or the connector has stopped. Call revit_get_connection_status and retry."
        };

        return new McpToolResult
        {
            RequestId = requestId,
            Success = false,
            Status = responding == false ? "revit_busy" : "request_timeout",
            Message = message + diagnosis,
            Data = new { revitProcessId = processId, revitResponding = responding }
        };
    }

    /// <summary>Process.Responding for the routed Revit instance; null when unknown.</summary>
    private static bool? ProbeResponding(int? processId)
    {
        if (processId == null) return null;
        try
        {
            using var process = Process.GetProcessById(processId.Value);
            return process.HasExited ? null : process.Responding;
        }
        catch
        {
            return null;
        }
    }

    private static McpToolResult Error(string requestId, string message) => new()
    {
        RequestId = requestId,
        Success = false,
        Message = message
    };
}
