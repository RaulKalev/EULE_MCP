using System.IO;
using System.IO.Pipes;
using Newtonsoft.Json;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Services;

/// <summary>
/// Hosts a named pipe server. Each client connection is handled on its own background thread.
/// Revit API calls are never made here — all requests are forwarded to ExternalEventService.
/// </summary>
public class PipeServer
{
    private readonly string _pipeName;
    private readonly ExternalEventService _eventService;
    private readonly Logging.ActivityLogger _logger;
    private CancellationTokenSource? _cts;

    public PipeServer(string pipeName, ExternalEventService eventService, Logging.ActivityLogger logger)
    {
        _pipeName = pipeName;
        _eventService = eventService;
        _logger = logger;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        // Wrap in a self-restarting guardian so the listen loop survives transient errors.
        _ = Task.Run(() => GuardedListenLoop(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
    }

    /// <summary>
    /// Wraps ListenLoop in a restart guard: if the loop crashes unexpectedly it restarts
    /// after a short delay, until the cancellation token is triggered.
    /// </summary>
    private async Task GuardedListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ListenLoop(ct);
                // Clean exit (cancellation) — stop the guard.
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                await _logger.WriteRawAsync($"PipeServer listen loop crashed, restarting: {ex.Message}");
                try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(ct);

                // Pass a linked token so client tasks are cancelled when the server stops.
                _ = Task.Run(() => HandleClientAsync(pipe, ct), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Log and retry — use a guarded delay so OCE doesn't escape the catch.
                await _logger.WriteRawAsync($"PipeServer accept error: {ex.Message}");
                try { await Task.Delay(500, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var _ = pipe;
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

        try
        {
            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                McpToolRequest? request = null;
                McpToolResult result;

                try
                {
                    request = JsonConvert.DeserializeObject<McpToolRequest>(line);
                    if (request == null) throw new InvalidOperationException("Null request deserialized.");

                    // Use a generous timeout (5 min) to allow time for user approval.
                    // Read-only tools complete in milliseconds; the timeout only matters
                    // for RequiresApproval tools waiting on user interaction.
                    result = await _eventService.DispatchAsync(request, timeoutMs: 300_000);
                }
                catch (Exception ex)
                {
                    result = new McpToolResult
                    {
                        RequestId = request?.RequestId ?? string.Empty,
                        Success = false,
                        Message = $"Request handling failed: {ex.Message}"
                    };
                }

                sw.Stop();
                result.DurationMs = sw.ElapsedMilliseconds;

                await writer.WriteLineAsync(JsonConvert.SerializeObject(result));

                if (request != null)
                    await _logger.WriteAsync(request, result, _eventService.GetLastContext());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await _logger.WriteRawAsync($"Client handler error: {ex.Message}");
        }
    }
}
