using System.IO;
using System.IO.Pipes;
using Newtonsoft.Json;
using RevitMCP.Core.Models;
using RevitMCP.Core.Safety;

namespace RevitMCP.Addin.Services;

/// <summary>
/// Hosts a named pipe server. Each client connection is handled on its own background thread.
/// Revit API calls are never made here — all requests are forwarded to ExternalEventService.
/// </summary>
public class PipeServer
{
    private static readonly string DiagLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RevitMCP_startup.log");

    private static void DiagLog(string msg)
    {
        try { File.AppendAllText(DiagLogPath, $"[PIPE {DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}"); } catch { }
    }

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
                var pipe = CreatePipe(_pipeName);

                await pipe.WaitForConnectionAsync(ct);

                DiagLog("Client connected to pipe");
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

    /// <summary>
    /// Creates one pipe instance whose ACL grants the Windows user running Revit read/write.
    /// The default ACL of a pipe made by an elevated ("Run as administrator") Revit only lets
    /// elevated administrators write to it, so a normal, non-elevated agent under the same
    /// account (e.g. Codex) was refused with access denied. Granting the user SID works for
    /// both elevation levels and is narrower than the default, which also gives Everyone read.
    /// </summary>
    private static NamedPipeServerStream CreatePipe(string pipeName)
    {
        var security = new PipeSecurity();
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User;
        if (user != null)
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, System.Security.AccessControl.AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));

#if REVIT2024
        return new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0, 0,
            security);
#else
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0, 0,
            security);
#endif
    }

    private const string ConnectionStatusTool = "revit_get_connection_status";

    /// <summary>How long the connection status waits for the Revit API thread before answering from the pipe.</summary>
    private const int ConnectionStatusTimeoutMs = 3000;

    /// <summary>
    /// Connection status must answer even while Revit is busy (#87). It asks the Revit API thread for live
    /// document info, but if that does not happen within a few seconds it answers from the pipe thread with
    /// the last known document context and the reason Revit is busy.
    /// </summary>
    private async Task<McpToolResult> GetConnectionStatusAsync(McpToolRequest request, CancellationToken ct)
    {
        var live = await _eventService.DispatchAsync(request, timeoutMs: ConnectionStatusTimeoutMs, cancellationToken: ct);
        // queue_full counts too: repeated status checks against a blocked Revit must keep answering.
        if (live.Success || live.Status is not (RevitBusyDiagnosis.BusyStatus or "request_timeout" or "queue_full"))
            return live;

        var snapshot = _eventService.GetBusySnapshot();
        var context = _eventService.GetLastContext();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Status = RevitBusyDiagnosis.BusyStatus,
            Message = $"Connector is running, but Revit is busy: {RevitBusyDiagnosis.Describe(snapshot)} {RevitBusyDiagnosis.Hint(snapshot)}",
            Data = new
            {
                connectorStatus = "Running",
                busy = ExternalEventService.BusyData(snapshot),
                lastKnownContext = context == null ? null : new
                {
                    revitVersion = context.RevitVersion,
                    documentTitle = context.ModelTitle,
                    activeViewName = context.ActiveViewName,
                    isWorkshared = context.IsWorkshared,
                    centralModelPath = context.CentralPath,
                    localModelPath = context.LocalPath,
                    revitUsername = context.RevitUsername
                },
                note = "Document values are the last known context, captured when Revit last processed a request."
            },
            Warnings = new List<string> { "Revit API thread did not respond; document info is the last known context." }
        };
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        DiagLog("HandleClientAsync started");
        // NOTE: ALL construction is inside the try so any exception is caught and logged.
        try
        {
            var enc = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            DiagLog($"Pipe state before readers: IsConnected={pipe.IsConnected}, CanRead={pipe.CanRead}, CanWrite={pipe.CanWrite}");
            using var reader = new StreamReader(pipe, enc, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            DiagLog("StreamReader created");
            using var writer = new StreamWriter(pipe, enc, bufferSize: 4096, leaveOpen: true);
            writer.AutoFlush = true;
            DiagLog("StreamWriter created");

            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                DiagLog("Waiting for ReadLineAsync...");
#if REVIT2024
                var line = await reader.ReadLineAsync();
#else
                var line = await reader.ReadLineAsync(ct);
#endif
                if (line == null) { DiagLog("ReadLineAsync returned null (client disconnected)"); break; }

                DiagLog($"Read line ({line.Length} chars), dispatching...");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                McpToolRequest? request = null;
                McpToolResult result;

                try
                {
                    request = JsonConvert.DeserializeObject<McpToolRequest>(line);
                    if (request == null) throw new InvalidOperationException("Null request deserialized.");

                    DiagLog($"Dispatching tool: {request.ToolName}");
                    // Project Village observer (Village\): O(1), never throws, no-op when disabled. Does not touch the request.
                    Village.Hosting.VillageHooks.ToolStarted(request, _eventService.GetLastContext());
                    // QueryLimits controls the maximum time a tool may run before the dispatch layer returns a timeout.
                    var timeoutMs = Math.Max(1, QueryLimits.Default.TimeoutSeconds) * 1000;
                    result = request.ToolName == ConnectionStatusTool
                        ? await GetConnectionStatusAsync(request, ct)
                        : await _eventService.DispatchAsync(request, timeoutMs: timeoutMs, cancellationToken: ct);
                    DiagLog($"DispatchAsync returned: success={result.Success} in {sw.ElapsedMilliseconds}ms");
                }
                catch (Exception ex)
                {
                    DiagLog($"Inner exception: {ex.GetType().Name}: {ex.Message}");
                    result = new McpToolResult
                    {
                        RequestId = request?.RequestId ?? string.Empty,
                        Success = false,
                        Message = $"Request handling failed: {ex.Message}"
                    };
                }

                sw.Stop();
                result.DurationMs = sw.ElapsedMilliseconds;

                var (__, responseJson) = ResponseGuard.GuardSerialized(result);
                var responseBytes = System.Text.Encoding.UTF8.GetByteCount(responseJson);
                await writer.WriteLineAsync(responseJson);

                if (request != null)
                    await _logger.WriteAsync(request, result, _eventService.GetLastContext(), responseBytes);
            }
            DiagLog($"While loop exited: IsCancelled={ct.IsCancellationRequested}, IsConnected={pipe.IsConnected}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            DiagLog($"OUTER EXCEPTION in HandleClientAsync: {ex.GetType().Name}: {ex.Message}");
            await _logger.WriteRawAsync($"Client handler error: {ex.Message}");
        }
        finally
        {
            DiagLog("HandleClientAsync finally - disposing pipe");
            pipe.Dispose();
        }
    }
}
