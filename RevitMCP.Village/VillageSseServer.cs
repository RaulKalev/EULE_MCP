using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Village;

/// <summary>Counters for the diagnostics panel and tests.</summary>
public sealed class VillageServerStats
{
    [JsonProperty("connections")] public long Connections { get; set; }
    [JsonProperty("viewers_rejected")] public long ViewersRejected { get; set; }
    [JsonProperty("bad_requests")] public long BadRequests { get; set; }
    [JsonProperty("forbidden_hosts")] public long ForbiddenHosts { get; set; }
    [JsonProperty("actions_refused")] public long ActionsRefused { get; set; }
    [JsonProperty("actions_handled")] public long ActionsHandled { get; set; }
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
/// - accepts GET/HEAD for a fixed route allow-list: "/", "/events", "/snapshot", "/instances", "/contents", "/health",
///   and, when flyers are on, "/flyers" and "/flyers/{id}";
/// - accepts POST only on the flyer action routes ("/flyers/{id}/archive|unarchive|dismiss", "/show"), and only
///   when an <see cref="IVillageActions"/> was supplied. A POST must carry this listener's per-start token in
///   <c>X-Village-Token</c> (the token is written into the page it serves, which another origin cannot read),
///   an Origin, if any, of this listener, and a JSON body of at most <see cref="MaxActionBodyBytes"/>;
///   every other method, path or body is refused;
/// - the Host header must name a loopback host (blocks DNS-rebinding pages from reading local state);
/// - no CORS headers are sent, so foreign origins cannot read responses, open the stream, or send the token header;
/// - the request head is capped at 8 KB and must arrive within a few seconds;
/// - each viewer has a bounded outbound queue: a slow viewer loses old state messages, never the connector.
/// A request can reach the flyer board and, through the add-in's handler, select a flyer's elements in
/// Revit; nothing received from a client is passed to the hub, MCP, the agent or the tool registry, and
/// no request can change the model. No Revit API dependency.
/// </summary>
public sealed class VillageSseServer : IDisposable
{
    public const int MaxRequestHeadBytes = 8 * 1024;
    public const int RequestReadTimeoutMs = 5000;

    /// <summary>Most body bytes read and dropped after a 413, so the client can read the refusal.</summary>
    public const int MaxDiscardBytes = 1024 * 1024;

    /// <summary>How long a refused upload may take to finish before the connection is cut.</summary>
    public const int DiscardTimeoutMs = 2000;
    public const int HeartbeatIntervalMs = 15000;
    public const int PerClientQueueCapacity = 256;
    public const int MaxActionBodyBytes = 96 * 1024;

    /// <summary>Replaced in the served page by the per-start action token (empty when actions are off).</summary>
    public const string TokenPlaceholder = "__VILLAGE_ACTION_TOKEN__";

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
        Action<string>? log = null,
        Func<VillageModelLibrary?>? modelLibraryProvider = null,
        Func<string, byte[]?>? vendorProvider = null,
        Func<string>? contentsJsonProvider = null,
        IVillageActions? actions = null)
    {
        _actions = actions;
        ActionToken = actions == null ? string.Empty : NewToken();
        _contentsJson = contentsJsonProvider ?? (() => "{}");
        _options = options;
        _snapshotJson = snapshotJsonProvider;
        _html = htmlProvider;
        _instancesJson = instancesJsonProvider ?? (() => "[]");
        _log = log;
        _modelLibrary = modelLibraryProvider ?? (() => null);
        _vendor = vendorProvider ?? (_ => null);
    }

    private readonly Func<string> _contentsJson;
    private readonly Func<VillageModelLibrary?> _modelLibrary;
    private readonly Func<string, byte[]?> _vendor;
    private readonly IVillageActions? _actions;
    private readonly SemaphoreSlim _showGate = new(1, 1);

    /// <summary>The secret a POST must echo; empty when this listener takes no actions.</summary>
    public string ActionToken { get; }

    public bool AcceptsActions => _actions != null;

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
                    ActionsRefused = _stats.ActionsRefused,
                    ActionsHandled = _stats.ActionsHandled,
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
            var (head, leftover) = await ReadHeadAsync(stream, ct).ConfigureAwait(false);
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

            if (request.Method == "POST" && _actions != null && IsActionRoute(request.Path))
            {
                await HandleActionAsync(stream, request, leftover, ct).ConfigureAwait(false);
                return;
            }

            if (request.Method != "GET" && request.Method != "HEAD")
            {
                lock (_gate) _stats.BadRequests++;
                await WriteSimpleAsync(stream, 405, "Method Not Allowed", "text/plain", "GET only", ct, "Allow: GET, HEAD").ConfigureAwait(false);
                return;
            }

            switch (request.Path)
            {
                case "/":
                case "/index.html":
                    await WriteSimpleAsync(stream, 200, "OK", "text/html; charset=utf-8", PageHtml(), ct, headOnly: request.Method == "HEAD").ConfigureAwait(false);
                    return;
                case "/flyers" when _actions != null:
                    await WriteSimpleAsync(stream, 200, "OK", "application/json; charset=utf-8", SafeProvide(_actions.BoardJson, "{\"enabled\":false,\"flyers\":[]}"), ct, headOnly: request.Method == "HEAD").ConfigureAwait(false);
                    return;
                case "/snapshot":
                    await WriteSimpleAsync(stream, 200, "OK", "application/json; charset=utf-8", SafeProvide(_snapshotJson, "{}"), ct, headOnly: request.Method == "HEAD").ConfigureAwait(false);
                    return;
                case "/instances":
                    await WriteSimpleAsync(stream, 200, "OK", "application/json; charset=utf-8", SafeProvide(_instancesJson, "[]"), ct, headOnly: request.Method == "HEAD").ConfigureAwait(false);
                    return;
                case "/contents":
                    await WriteSimpleAsync(stream, 200, "OK", "application/json; charset=utf-8", SafeProvide(_contentsJson, "{}"), ct, headOnly: request.Method == "HEAD").ConfigureAwait(false);
                    return;
                case "/health":
                    await WriteSimpleAsync(stream, 200, "OK", "application/json; charset=utf-8", HealthJson(), ct, headOnly: request.Method == "HEAD").ConfigureAwait(false);
                    return;
                case "/models/index.json":
                {
                    var library = SafeLibrary();
                    var json = JsonConvert.SerializeObject(library == null
                        ? new VillageModelIndex()
                        : library.Index(_options.ModelsFolder != null));
                    await WriteSimpleAsync(stream, 200, "OK", "application/json; charset=utf-8", json, ct, headOnly: request.Method == "HEAD").ConfigureAwait(false);
                    return;
                }
                case "/events":
                    if (request.Method == "HEAD")
                    {
                        await WriteSimpleAsync(stream, 200, "OK", "text/event-stream", string.Empty, ct, headOnly: true).ConfigureAwait(false);
                        return;
                    }
                    break;
                default:
                {
                    // Two read-only static areas: the vendored viewer script, and the optional
                    // glTF model folder. Both validate the path before touching the disk, and
                    // anything that does not resolve falls through to the same 404 as before —
                    // only "/events" may ever reach the stream handler below.
                    byte[]? payload = null;
                    string? type = null;
                    var flyerId = _actions != null ? FlyerIdFrom(request.Path) : null;
                    if (flyerId != null)
                    {
                        string? json;
                        try { json = _actions!.FlyerJson(flyerId); } catch { json = null; }
                        if (json != null)
                        {
                            await WriteSimpleAsync(stream, 200, "OK", "application/json; charset=utf-8", json, ct, headOnly: request.Method == "HEAD").ConfigureAwait(false);
                            return;
                        }
                    }
                    else if (request.Path.StartsWith("/vendor/", StringComparison.Ordinal))
                    {
                        payload = SafeVendor(request.Path.Substring("/vendor/".Length));
                        type = "application/javascript; charset=utf-8";
                    }
                    else if (request.Path.StartsWith("/models/", StringComparison.Ordinal))
                    {
                        var file = SafeResolveModel(request.Path.Substring("/models/".Length));
                        if (file != null)
                        {
                            try { payload = File.ReadAllBytes(file); } catch { payload = null; }
                            type = "model/gltf-binary";
                        }
                    }

                    if (payload != null && type != null)
                    {
                        await WriteBytesAsync(stream, type, payload, ct, request.Method == "HEAD").ConfigureAwait(false);
                        return;
                    }

                    lock (_gate) _stats.BadRequests++;
                    await WriteSimpleAsync(stream, 404, "Not Found", "text/plain", "not found", ct).ConfigureAwait(false);
                    return;
                }
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
        public string? Origin;
        public string? ContentType;
        public long? ContentLength;
        public string? Token;
        public string? FetchSite;
        public bool Chunked;
    }

    /// <summary>
    /// Reads up to the end of the request head. Returns a null head on timeout, oversize or
    /// disconnect; otherwise the head and any body bytes that arrived with it.
    /// </summary>
    private static async Task<(string? Head, byte[] Leftover)> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
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
                if (read <= 0) return (null, Array.Empty<byte>());
                total += read;
                // ASCII decoding maps every byte to one char, so a char index is a byte index.
                var text = Encoding.ASCII.GetString(buffer, 0, total);
                var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                var sep = 4;
                var bare = text.IndexOf("\n\n", StringComparison.Ordinal);
                if (end < 0 || (bare >= 0 && bare < end)) { end = bare; sep = 2; }
                if (end >= 0)
                {
                    var rest = new byte[total - end - sep];
                    Array.Copy(buffer, end + sep, rest, 0, rest.Length);
                    return (text.Substring(0, end), rest);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return (null, Array.Empty<byte>());
        }
        return (null, Array.Empty<byte>()); // head too large
    }

    /// <summary>Parses the request line and the few headers the server acts on. Anything unexpected → null.</summary>
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

        var request = new Request { Method = method.ToUpperInvariant(), Path = path };
        var hostSeen = false;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line.Substring(0, colon).Trim();
            var value = line.Substring(colon + 1).Trim();
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                if (hostSeen) continue;
                hostSeen = true;
                request.Host = value;
            }
            else if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase)) request.Origin = value;
            else if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) request.ContentType = value;
            else if (name.Equals("X-Village-Token", StringComparison.OrdinalIgnoreCase)) request.Token = value;
            else if (name.Equals("Sec-Fetch-Site", StringComparison.OrdinalIgnoreCase)) request.FetchSite = value;
            else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) request.Chunked = true;
            else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                request.ContentLength = long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1;
        }
        return request;
    }

    /// <summary>Loopback hosts only. An absent Host header is accepted for HTTP/1.0-style local tools.</summary>
    public static bool IsAllowedHost(string? host)
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

    // ─── Flyer actions ──────────────────────────────────────────────────────

    /// <summary>"/show" or "/flyers/{id}/{archive|unarchive|dismiss}".</summary>
    public static bool IsActionRoute(string path) =>
        path == "/show" || FlyerAction(path, out _, out _);

    /// <summary>The id in "/flyers/{id}", or null.</summary>
    public static string? FlyerIdFrom(string path)
    {
        const string prefix = "/flyers/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var id = path.Substring(prefix.Length);
        return VillageFlyerBoard.IsValidId(id) ? id : null;
    }

    /// <summary>Splits "/flyers/{id}/{action}" for one of the board's actions.</summary>
    public static bool FlyerAction(string path, out string id, out string action)
    {
        id = action = string.Empty;
        const string prefix = "/flyers/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var parts = path.Substring(prefix.Length).Split('/');
        if (parts.Length != 2 || !VillageFlyerBoard.IsValidId(parts[0]) || !VillageFlyerBoard.IsAction(parts[1])) return false;
        id = parts[0];
        action = parts[1];
        return true;
    }

    /// <summary>
    /// Why an action request is refused, or null when it may proceed: the per-start token must
    /// match, and the browser's Origin and Sec-Fetch-Site, when sent, must name this listener.
    /// </summary>
    private string? RefuseAction(Request request)
    {
        if (string.IsNullOrEmpty(ActionToken) || !FixedTimeEquals(request.Token, ActionToken)) return "missing or wrong action token";
        if (request.FetchSite != null && !request.FetchSite.Equals("same-origin", StringComparison.OrdinalIgnoreCase)) return "cross-site request";
        if (request.Origin != null && !IsOwnOrigin(request.Origin)) return "foreign origin";
        return null;
    }

    private bool IsOwnOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != "http" || uri.Port != Port) return false;
        if (!string.IsNullOrEmpty(uri.PathAndQuery) && uri.PathAndQuery != "/") return false;
        return IsAllowedHost(uri.Host);
    }

    private static bool FixedTimeEquals(string? a, string b)
    {
        if (a == null || a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    private async Task HandleActionAsync(NetworkStream stream, Request request, byte[] leftover, CancellationToken ct)
    {
        var refusal = RefuseAction(request);
        if (refusal != null)
        {
            lock (_gate) _stats.ActionsRefused++;
            Log("action refused: " + refusal);
            await WriteSimpleAsync(stream, 403, "Forbidden", "text/plain", refusal, ct).ConfigureAwait(false);
            return;
        }
        if (request.Chunked || request.ContentLength == null || request.ContentLength < 0)
        {
            lock (_gate) _stats.ActionsRefused++;
            await WriteSimpleAsync(stream, 411, "Length Required", "text/plain", "Content-Length required", ct).ConfigureAwait(false);
            return;
        }
        if (request.ContentLength > MaxActionBodyBytes)
        {
            lock (_gate) _stats.ActionsRefused++;
            await WriteSimpleAsync(stream, 413, "Payload Too Large", "text/plain", "body too large", ct).ConfigureAwait(false);
            // Closing with the body still unread makes the OS reset the connection, and a client
            // that is still uploading then sees the reset instead of the 413. Discard what it sends
            // (bounded), never acting on it.
            await DiscardBodyAsync(stream, request.ContentLength.Value - leftover.Length, ct).ConfigureAwait(false);
            return;
        }
        var length = (int)request.ContentLength.Value;
        if (length > 0 && !IsJsonType(request.ContentType))
        {
            lock (_gate) _stats.ActionsRefused++;
            await WriteSimpleAsync(stream, 415, "Unsupported Media Type", "text/plain", "application/json only", ct).ConfigureAwait(false);
            return;
        }

        var body = await ReadBodyAsync(stream, leftover, length, ct).ConfigureAwait(false);
        JObject? json = null;
        if (body == null || (length > 0 && (json = ParseBody(body)) == null))
        {
            lock (_gate) _stats.BadRequests++;
            await WriteSimpleAsync(stream, 400, "Bad Request", "text/plain", "bad request body", ct).ConfigureAwait(false);
            return;
        }

        if (request.Path == "/show")
        {
            await HandleShowAsync(stream, json, ct).ConfigureAwait(false);
            return;
        }

        FlyerAction(request.Path, out var id, out var action);
        bool applied;
        try { applied = _actions!.Apply(id, action); }
        catch { applied = false; }
        lock (_gate) _stats.ActionsHandled++;
        if (applied)
            await WriteSimpleAsync(stream, 200, "OK", "application/json; charset=utf-8", "{\"ok\":true}", ct).ConfigureAwait(false);
        else
            await WriteSimpleAsync(stream, 404, "Not Found", "application/json; charset=utf-8", "{\"ok\":false,\"status\":\"not_found\"}", ct).ConfigureAwait(false);
    }

    private async Task HandleShowAsync(NetworkStream stream, JObject? json, CancellationToken ct)
    {
        var flyer = json?["flyer"] is JValue fv && fv.Type == JTokenType.String ? fv.Value<string>() : null;
        List<long>? ids = null;
        if (json?["ids"] is JArray array)
        {
            if (array.Count > VillageFlyerActions.MaxShowIds)
            {
                await WriteJsonAsync(stream, 413, "Payload Too Large", VillageShowResult.Fail(VillageShowStatus.BadRequest, $"At most {VillageFlyerActions.MaxShowIds} elements can be shown at once."), ct).ConfigureAwait(false);
                return;
            }
            ids = new List<long>(array.Count);
            foreach (var t in array)
                if (VillageFlyerExtractor.TryId(t, out var id)) ids.Add(id);
        }
        if (flyer == null || !VillageFlyerBoard.IsValidId(flyer))
        {
            await WriteJsonAsync(stream, 400, "Bad Request", VillageShowResult.Fail(VillageShowStatus.BadRequest, "Name the flyer to show."), ct).ConfigureAwait(false);
            return;
        }

        // One selection at a time: Revit runs them one by one anyway, and a double click should not queue two.
        if (!await _showGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            await WriteJsonAsync(stream, 429, "Too Many Requests", VillageShowResult.Fail(VillageShowStatus.Busy, "Revit is still showing the previous selection."), ct).ConfigureAwait(false);
            return;
        }
        VillageShowResult result;
        try
        {
            result = await _actions!.ShowAsync(flyer, ids, ct).ConfigureAwait(false)
                     ?? VillageShowResult.Fail(VillageShowStatus.Error, "No result.");
        }
        catch (Exception ex)
        {
            result = VillageShowResult.Fail(VillageShowStatus.Error, "Show in Revit failed: " + VillageEventSerializer.Truncate(ex.Message));
        }
        finally
        {
            _showGate.Release();
        }
        lock (_gate) _stats.ActionsHandled++;
        await WriteJsonAsync(stream, 200, "OK", result, ct).ConfigureAwait(false);
    }

    private static bool IsJsonType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return false;
        var semi = contentType!.IndexOf(';');
        var media = (semi >= 0 ? contentType.Substring(0, semi) : contentType).Trim();
        return media.Equals("application/json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Exactly <paramref name="length"/> body bytes, within the request timeout; null otherwise.</summary>
    private static async Task<byte[]?> ReadBodyAsync(NetworkStream stream, byte[] leftover, int length, CancellationToken ct)
    {
        var body = new byte[length];
        var have = Math.Min(length, leftover.Length);
        Array.Copy(leftover, body, have);
        if (have == length) return body;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestReadTimeoutMs);
        try
        {
            while (have < length)
            {
                var read = await stream.ReadAsync(body, have, length - have, timeout.Token).ConfigureAwait(false);
                if (read <= 0) return null;
                have += read;
            }
            return body;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads and drops up to <paramref name="remaining"/> body bytes (at most
    /// <see cref="MaxDiscardBytes"/>, within <see cref="DiscardTimeoutMs"/>) so a refused upload can
    /// finish and read its response. Larger or slower uploads are simply cut off.
    /// </summary>
    private static async Task DiscardBodyAsync(NetworkStream stream, long remaining, CancellationToken ct)
    {
        remaining = Math.Min(remaining, MaxDiscardBytes);
        if (remaining <= 0) return;
        var buffer = new byte[8192];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DiscardTimeoutMs);
        try
        {
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, remaining), timeout.Token).ConfigureAwait(false);
                if (read <= 0) return;
                remaining -= read;
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private static JObject? ParseBody(byte[] body)
    {
        try
        {
            var text = Encoding.UTF8.GetString(body);
            using var reader = new JsonTextReader(new StringReader(text)) { MaxDepth = 8, DateParseHandling = DateParseHandling.None };
            return JToken.ReadFrom(reader) as JObject;
        }
        catch
        {
            return null;
        }
    }

    private static Task WriteJsonAsync(NetworkStream stream, int status, string reason, object payload, CancellationToken ct) =>
        WriteSimpleAsync(stream, status, reason, "application/json; charset=utf-8", JsonConvert.SerializeObject(payload), ct);

    private string PageHtml()
    {
        var html = SafeProvide(_html, "<!doctype html><title>Project Village</title><p>Viewer unavailable.</p>");
        return html.Replace(TokenPlaceholder, ActionToken);
    }

    private static string NewToken()
    {
        var bytes = new byte[24];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        return string.Concat(bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
    }

    // ─── Responses ──────────────────────────────────────────────────────────

    /// <summary>
    /// One policy for every response. <c>script-src 'self'</c> is what lets the page load the
    /// vendored three.js bundle from this same loopback listener; no other origin is reachable,
    /// and <c>default-src 'none'</c> still blocks everything that is not named here.
    /// </summary>
    internal const string CspHeader =
        "Content-Security-Policy: default-src 'none'; script-src 'self' 'unsafe-inline'; " +
        "style-src 'unsafe-inline'; img-src 'self' data: blob:; connect-src 'self'; font-src data:\r\n";

    private VillageModelLibrary? SafeLibrary()
    {
        try { return _modelLibrary(); } catch { return null; }
    }

    private string? SafeResolveModel(string relative)
    {
        try { return SafeLibrary()?.Resolve(relative); } catch { return null; }
    }

    private byte[]? SafeVendor(string name)
    {
        try { return IsSafeVendorName(name) ? _vendor(name) : null; }
        catch { return null; }
    }

    /// <summary>
    /// A vendored file name: one or more dot-separated parts, each letters, digits, underscore or
    /// hyphen — so <c>village-three.min.js</c> passes, while any path separator, empty part or
    /// <c>..</c> does not. The old check looked at the name minus only its last extension, which
    /// left the dot in <c>village-three.min</c> and turned the viewer script away.
    /// </summary>
    public static bool IsSafeVendorName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name!.Length > 120) return false;
        var parts = name.Split('.');
        if (parts.Length < 2) return false;
        foreach (var part in parts)
            if (!VillageModelLibrary.IsSafeName(part)) return false;
        return true;
    }

    /// <summary>Binary response for the two static areas. Cached hard: these change only on redeploy.</summary>
    private static async Task WriteBytesAsync(NetworkStream stream, string contentType, byte[] body, CancellationToken ct, bool headOnly)
    {
        var head = new StringBuilder();
        head.Append("HTTP/1.1 200 OK\r\n");
        head.Append("Content-Type: ").Append(contentType).Append("\r\n");
        head.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        head.Append("Cache-Control: no-cache\r\n");
        head.Append("X-Content-Type-Options: nosniff\r\n");
        head.Append("X-Frame-Options: DENY\r\n");
        head.Append("Referrer-Policy: no-referrer\r\n");
        head.Append("X-Village-Read-Only: true\r\n");
        head.Append(CspHeader);
        head.Append("Connection: close\r\n\r\n");
        var headBytes = Encoding.ASCII.GetBytes(head.ToString());
        await stream.WriteAsync(headBytes, 0, headBytes.Length, ct).ConfigureAwait(false);
        if (!headOnly) await stream.WriteAsync(body, 0, body.Length, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

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
        sb.Append(CspHeader);
        foreach (var h in extraHeaders)
            if (!string.IsNullOrEmpty(h)) sb.Append(h).Append("\r\n");
        sb.Append("\r\n");
        var bytes = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
    }

    private string HealthJson()
    {
        var s = Stats;
        return "{\"ok\":true,\"read_only\":true,\"actions\":" + (AcceptsActions ? "true" : "false") +
               ",\"clients\":" + s.ActiveClients.ToString(CultureInfo.InvariantCulture) +
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
