using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json;

namespace RevitMCP.Addin.Village;

/// <summary>Counters for the diagnostics panel and tests.</summary>
public sealed class VillageServerStats
{
    [JsonProperty("connections")] public long Connections { get; set; }
    [JsonProperty("viewers_rejected")] public long ViewersRejected { get; set; }
    [JsonProperty("bad_requests")] public long BadRequests { get; set; }
    [JsonProperty("forbidden_hosts")] public long ForbiddenHosts { get; set; }
    [JsonProperty("messages_sent")] public long MessagesSent { get; set; }
    [JsonProperty("messages_dropped")] public long MessagesDropped { get; set; }
    [JsonProperty("max_pending_observed")] public int MaxPendingObserved { get; set; }
    [JsonProperty("active_clients")] public int ActiveClients { get; set; }
}

/// <summary>
/// Minimal HTTP/1.1 + Server-Sent Events server for the village viewer, over a loopback
/// <see cref="TcpListener"/> (no URL ACL needed, works on .NET Framework 4.8 and .NET 8).
///
/// Security model:
/// - binds to a loopback address only (the options coerce anything else back to 127.0.0.1);
/// - accepts GET/HEAD for a fixed route allow-list: "/", "/events", "/snapshot", "/instances", "/health";
///   every other method or path is refused, and a request body is never read;
/// - the Host header must name a loopback host (blocks DNS-rebinding pages from reading local state);
/// - no CORS headers are sent, so foreign origins cannot read responses or open the stream;
/// - the request head is capped at 8 KB and must arrive within a few seconds;
/// - each viewer has a bounded outbound queue: a slow viewer loses old state messages, never the connector.
/// Nothing received from a client is ever passed to the hub, MCP or Revit. No Revit API dependency.
/// </summary>
public sealed class VillageSseServer : IDisposable
{
    public const int MaxRequestHeadBytes = 8 * 1024;
    public const int RequestReadTimeoutMs = 5000;
    public const int HeartbeatIntervalMs = 15000;
    public const int PerClientQueueCapacity = 256;

    private static readonly string[] AllowedHosts = { "127.0.0.1", "localhost", "[::1]", "::1" };

    private readonly VillageOptions _options;
    private readonly Func<string> _snapshotJson;
    private readonly Func<string> _html;
    private readonly Func<string> _instancesJson;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly List<Client> _clients = new();
    private readonly VillageServerStats _stats = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public VillageSseServer(
        VillageOptions options,
        Func<string> snapshotJsonProvider,
        Func<string> htmlProvider,
        Func<string>? instancesJsonProvider = null,
        Action<string>? log = null)
    {
        _options = options;
        _snapshotJson = snapshotJsonProvider;
        _html = htmlProvider;
        _instancesJson = instancesJsonProvider ?? (() => "[]");
        _log = log;
    }

    public int Port { get; private set; }
    public bool IsListening => _listener != null && _cts != null && !_cts.IsCancellationRequested;
    public string BaseUrl => "http://" + HostForUrl() + ":" + Port.ToString(CultureInfo.InvariantCulture) + "/";

    public int ClientCount
    {
        get { lock (_gate) return _clients.Count; }
    }

    public VillageServerStats Stats
    {
        get
        {
            lock (_gate)
            {
                return new VillageServerStats
                {
                    Connections = _stats.Connections,
                    ViewersRejected = _stats.ViewersRejected,
                    BadRequests = _stats.BadRequests,
                    ForbiddenHosts = _stats.ForbiddenHosts,
                    MessagesSent = _stats.MessagesSent,
                    MessagesDropped = _stats.MessagesDropped,
                    MaxPendingObserved = _stats.MaxPendingObserved,
                    ActiveClients = _clients.Count
                };
            }
        }
    }

    // ─── Lifecycle ──────────────────────────────────────────────────────────

    /// <summary>Binds the loopback listener. Tries the next ports when fallback is enabled. Never throws.</summary>
    public bool Start()
    {
        if (IsListening) return true;
        var address = ResolveLoopback();
        var attempts = _options.Port == 0 ? 1 : (_options.PortFallback ? VillageOptions.PortFallbackAttempts : 1);

        for (var i = 0; i < attempts; i++)
        {
            var port = _options.Port == 0 ? 0 : _options.Port + i;
            if (port > 65535) break;
            var listener = new TcpListener(address, port);
            try
            {
                listener.ExclusiveAddressUse = true;
                listener.Start(backlog: 16);
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                _listener = listener;
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, token), CancellationToken.None);
                Log($"listening on {BaseUrl}");
                return true;
            }
            catch (SocketException ex)
            {
                Log($"port {port} unavailable: {ex.SocketErrorCode}");
                try { listener.Stop(); } catch { }
            }
            catch (Exception ex)
            {
                Log($"listener error: {ex.GetType().Name}: {ex.Message}");
                try { listener.Stop(); } catch { }
                break;
            }
        }
        return false;
    }

    public void Stop()
    {
        var cts = _cts;
        var listener = _listener;
        _cts = null;
        _listener = null;
        try { cts?.Cancel(); } catch { }
        try { listener?.Stop(); } catch { }

        List<Client> clients;
        lock (_gate)
        {
            clients = new List<Client>(_clients);
            _clients.Clear();
        }
        foreach (var c in clients) c.Close();
        try { _acceptLoop?.Wait(1000); } catch { }
        _acceptLoop = null;
    }

    public void Dispose() => Stop();

    // ─── Broadcasting ───────────────────────────────────────────────────────

    /// <summary>Queues one message for every connected viewer. Never blocks the caller.</summary>
    public void Broadcast(string eventName, string json)
    {
        List<Client> clients;
        lock (_gate) clients = new List<Client>(_clients);
        foreach (var c in clients)
        {
            var dropped = c.Enqueue(eventName, json, out var pending);
            lock (_gate)
            {
                if (dropped) _stats.MessagesDropped++;
                if (pending > _stats.MaxPendingObserved) _stats.MaxPendingObserved = pending;
            }
        }
    }

    // ─── Accept / handle ────────────────────────────────────────────────────

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient socket;
            try
            {
                socket = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException) { break; }
            catch (InvalidOperationException) { break; }
            catch (SocketException) { if (ct.IsCancellationRequested) break; continue; }
            catch (Exception ex)
            {
                Log("accept error: " + ex.Message);
                if (ct.IsCancellationRequested) break;
                try { await Task.Delay(200, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                continue;
            }

            lock (_gate) _stats.Connections++;
            _ = Task.Run(() => HandleAsync(socket, ct), CancellationToken.None);
        }
    }

    private async Task HandleAsync(TcpClient socket, CancellationToken ct)
    {
        Client? client = null;
        try
        {
            socket.NoDelay = true;
            var stream = socket.GetStream();
            var head = await ReadHeadAsync(stream, ct).ConfigureAwait(false);
            if (head == null)
            {
                lock (_gate) _stats.BadRequests++;
                await WriteSimpleAsync(stream, 400, "Bad Request", "text/plain", "bad request", ct).ConfigureAwait(false);
                return;
            }

            var request = ParseRequest(head);
            if (request == null)
            {
                lock (_gate) _stats.BadRequests++;
                await WriteSimpleAsync(stream, 400, "Bad Request", "text/plain", "bad request", ct).ConfigureAwait(false);
                return;
            }

            if (!IsAllowedHost(request.Host))
            {
                lock (_gate) _stats.ForbiddenHosts++;
                await WriteSimpleAsync(stream, 421, "Misdirected Request", "text/plain", "loopback only", ct).ConfigureAwait(false);
                return;
            }

            if (request.Method != "GET" && request.Method != "HEAD")
            {
                lock (_gate) _stats.BadRequests++;
                await WriteSimpleAsync(stream, 405, "Method Not Allowed", "text/plain", "read-only: GET only", ct, "Allow: GET, HEAD").ConfigureAwait(false);
                return;
            }

            switch (request.Path)
            {
                case "/":
                case "/index.html":
                    await WriteSimpleAsync(stream, 200, "OK", "text/html; charset=utf-8", SafeProvide(_html, "<!doctype html><title>Project Village</title><p>Viewer unavailable.</p>"), ct, headOnly: request.Method == "HEAD").ConfigureAwait(false);
                    return;
                case "/snapshot":
                    await WriteSimpleAsync(stream, 200, "OK", "application/json; charset=utf-8", SafeProvide(_snapshotJson, "{}"), ct, headOnly: request.Method == "HEAD").ConfigureAwait(false);
                    return;
                case "/instances":
                    await WriteSimpleAsync(stream, 200, "OK", "application/json; charset=utf-8", SafeProvide(_instancesJson, "[]"), ct, headOnly: request.Method == "HEAD").ConfigureAwait(false);
                    return;
                case "/health":
                    await WriteSimpleAsync(stream, 200, "OK", "application/json; charset=utf-8", HealthJson(), ct, headOnly: request.Method == "HEAD").ConfigureAwait(false);
                    return;
                case "/events":
                    if (request.Method == "HEAD")
                    {
                        await WriteSimpleAsync(stream, 200, "OK", "text/event-stream", string.Empty, ct, headOnly: true).ConfigureAwait(false);
                        return;
                    }
                    break;
                default:
                    lock (_gate) _stats.BadRequests++;
                    await WriteSimpleAsync(stream, 404, "Not Found", "text/plain", "not found", ct).ConfigureAwait(false);
                    return;
            }

            // SSE stream
            lock (_gate)
            {
                if (_clients.Count >= _options.MaxViewers)
                {
                    _stats.ViewersRejected++;
                    client = null;
                }
                else
                {
                    client = new Client(socket, stream);
                    _clients.Add(client);
                }
            }
            if (client == null)
            {
                await WriteSimpleAsync(stream, 503, "Service Unavailable", "text/plain", "too many viewers", ct, "Retry-After: 10").ConfigureAwait(false);
                return;
            }

            await WriteHeadAsync(stream, 200, "OK", "text/event-stream", null, ct, "Cache-Control: no-store", "Connection: keep-alive").ConfigureAwait(false);
            await client.WriteRawAsync("retry: " + _options.ReconnectBackoffMs.ToString(CultureInfo.InvariantCulture) + "\n\n", ct).ConfigureAwait(false);
            await client.WriteMessageAsync("snapshot", SafeProvide(_snapshotJson, "{}"), ct).ConfigureAwait(false);
            lock (_gate) _stats.MessagesSent++;

            while (!ct.IsCancellationRequested && client.IsOpen)
            {
                var message = await client.DequeueAsync(HeartbeatIntervalMs, ct).ConfigureAwait(false);
                if (message == null)
                {
                    await client.WriteRawAsync(": ping\n\n", ct).ConfigureAwait(false);
                    continue;
                }
                await client.WriteMessageAsync(message.Value.Name, message.Value.Json, ct).ConfigureAwait(false);
                lock (_gate) _stats.MessagesSent++;
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            Log("client error: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            if (client != null)
            {
                lock (_gate) _clients.Remove(client);
                client.Close();
            }
            else
            {
                try { socket.Close(); } catch { }
            }
        }
    }

    // ─── Request parsing ────────────────────────────────────────────────────

    private sealed class Request
    {
        public string Method = string.Empty;
        public string Path = string.Empty;
        public string Host = string.Empty;
    }

    /// <summary>Reads up to the end of the request head. Returns null on timeout, oversize or disconnect.</summary>
    private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxRequestHeadBytes];
        var total = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestReadTimeoutMs);
        try
        {
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer, total, buffer.Length - total, timeout.Token).ConfigureAwait(false);
                if (read <= 0) return null;
                total += read;
                var text = Encoding.ASCII.GetString(buffer, 0, total);
                var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end >= 0) return text.Substring(0, end);
                end = text.IndexOf("\n\n", StringComparison.Ordinal);
                if (end >= 0) return text.Substring(0, end);
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        return null; // head too large
    }

    /// <summary>Parses the request line and the Host header only. Anything unexpected → null.</summary>
    private static Request? ParseRequest(string head)
    {
        var lines = head.Split('\n');
        if (lines.Length == 0) return null;
        var parts = lines[0].TrimEnd('\r').Split(' ');
        if (parts.Length != 3) return null;
        if (!parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return null;
        var method = parts[0];
        if (method.Length == 0 || method.Length > 10 || !method.All(char.IsLetter)) return null;

        var target = parts[1];
        if (target.Length == 0 || target.Length > 512 || target[0] != '/') return null;
        var q = target.IndexOf('?');
        var path = q >= 0 ? target.Substring(0, q) : target;
        if (path.IndexOf("..", StringComparison.Ordinal) >= 0 || path.Any(c => c < 32 || c > 126)) return null;

        var host = string.Empty;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
            {
                host = line.Substring(5).Trim();
                break;
            }
        }
        return new Request { Method = method.ToUpperInvariant(), Path = path, Host = host };
    }

    /// <summary>Loopback hosts only. An absent Host header is accepted for HTTP/1.0-style local tools.</summary>
    internal static bool IsAllowedHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;
        var h = host!.Trim().ToLowerInvariant();
        // strip the port
        if (h.StartsWith("[", StringComparison.Ordinal))
        {
            var close = h.IndexOf(']');
            if (close < 0) return false;
            h = h.Substring(0, close + 1);
        }
        else
        {
            var colon = h.IndexOf(':');
            if (colon >= 0) h = h.Substring(0, colon);
        }
        return Array.IndexOf(AllowedHosts, h) >= 0;
    }

    // ─── Responses ──────────────────────────────────────────────────────────

    private static async Task WriteSimpleAsync(NetworkStream stream, int status, string reason, string contentType, string body,
        CancellationToken ct, string? extraHeader = null, bool headOnly = false)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        await WriteHeadAsync(stream, status, reason, contentType, bytes.Length, ct, extraHeader, "Connection: close").ConfigureAwait(false);
        if (!headOnly && bytes.Length > 0)
            await stream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteHeadAsync(NetworkStream stream, int status, string reason, string contentType, int? contentLength,
        CancellationToken ct, params string?[] extraHeaders)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(reason).Append("\r\n");
        sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
        if (contentLength.HasValue) sb.Append("Content-Length: ").Append(contentLength.Value.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        sb.Append("Cache-Control: no-store\r\n");
        sb.Append("X-Content-Type-Options: nosniff\r\n");
        sb.Append("X-Frame-Options: DENY\r\n");
        sb.Append("Referrer-Policy: no-referrer\r\n");
        sb.Append("X-Village-Read-Only: true\r\n");
        sb.Append("Content-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; connect-src 'self'; font-src data:\r\n");
        foreach (var h in extraHeaders)
            if (!string.IsNullOrEmpty(h)) sb.Append(h).Append("\r\n");
        sb.Append("\r\n");
        var bytes = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
    }

    private string HealthJson()
    {
        var s = Stats;
        return "{\"ok\":true,\"read_only\":true,\"clients\":" + s.ActiveClients.ToString(CultureInfo.InvariantCulture) +
               ",\"max_viewers\":" + _options.MaxViewers.ToString(CultureInfo.InvariantCulture) +
               ",\"schema_version\":" + VillageSchema.Version.ToString(CultureInfo.InvariantCulture) + "}";
    }

    private static string SafeProvide(Func<string> provider, string fallback)
    {
        try { return provider() ?? fallback; }
        catch { return fallback; }
    }

    private IPAddress ResolveLoopback()
    {
        var host = VillageOptions.CoerceHost(_options.Host);
        return host == "::1" || host == "[::1]" ? IPAddress.IPv6Loopback : IPAddress.Loopback;
    }

    private string HostForUrl()
    {
        var host = VillageOptions.CoerceHost(_options.Host);
        return host == "::1" ? "[::1]" : host;
    }

    private void Log(string message)
    {
        try { _log?.Invoke("[village-server] " + message); } catch { }
    }

    // ─── Client ─────────────────────────────────────────────────────────────

    private sealed class Client
    {
        private readonly TcpClient _socket;
        private readonly NetworkStream _stream;
        private readonly object _gate = new();
        private readonly LinkedList<(string Name, string Json)> _pending = new();
        private readonly SemaphoreSlim _signal = new(0, 1);
        private bool _closed;

        public Client(TcpClient socket, NetworkStream stream)
        {
            _socket = socket;
            _stream = stream;
        }

        public bool IsOpen
        {
            get { lock (_gate) return !_closed && _socket.Connected; }
        }

        /// <summary>Returns true when an older message had to be dropped to make room.</summary>
        public bool Enqueue(string name, string json, out int pending)
        {
            var dropped = false;
            lock (_gate)
            {
                if (_closed) { pending = 0; return false; }
                if (_pending.Count >= PerClientQueueCapacity)
                {
                    // Prefer dropping an older state snapshot (the next one supersedes it); else the oldest anything.
                    var node = _pending.First;
                    while (node != null && node.Value.Name != "state") node = node.Next;
                    _pending.Remove(node ?? _pending.First!);
                    dropped = true;
                }
                _pending.AddLast((name, json));
                pending = _pending.Count;
            }
            try { if (_signal.CurrentCount == 0) _signal.Release(); } catch (SemaphoreFullException) { }
            return dropped;
        }

        public async Task<(string Name, string Json)?> DequeueAsync(int timeoutMs, CancellationToken ct)
        {
            lock (_gate)
            {
                if (_pending.First != null)
                {
                    var first = _pending.First.Value;
                    _pending.RemoveFirst();
                    return first;
                }
            }
            if (!await _signal.WaitAsync(timeoutMs, ct).ConfigureAwait(false)) return null;
            lock (_gate)
            {
                if (_pending.First == null) return null;
                var first = _pending.First.Value;
                _pending.RemoveFirst();
                return first;
            }
        }

        public Task WriteMessageAsync(string name, string json, CancellationToken ct)
        {
            // JSON from Newtonsoft never contains raw newlines, so one data line suffices.
            var text = "event: " + name + "\ndata: " + json + "\n\n";
            return WriteRawAsync(text, ct);
        }

        public async Task WriteRawAsync(string text, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await _stream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }

        public void Close()
        {
            lock (_gate)
            {
                if (_closed) return;
                _closed = true;
            }
            try { _stream.Close(); } catch { }
            try { _socket.Close(); } catch { }
            try { _signal.Release(); } catch { }
        }
    }
}
