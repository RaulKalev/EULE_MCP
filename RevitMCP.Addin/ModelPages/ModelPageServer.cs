using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.ModelPages;

/// <summary>The Revit side of a page request. Implementations answer with a JSON-serializable object.</summary>
public interface IModelPageBackend
{
    /// <summary>Whether Revit is on the page's model.</summary>
    Task<object> StatusAsync(ModelPageInfo page, CancellationToken ct);

    /// <summary>Selects the elements, and zooms to them when asked.</summary>
    Task<object> SelectAsync(ModelPageInfo page, IReadOnlyList<long> ids, bool zoom, CancellationToken ct);

    /// <summary>Current values of the named parameters on the elements.</summary>
    Task<object> ValuesAsync(ModelPageInfo page, IReadOnlyList<long> ids, IReadOnlyList<string> parameters, CancellationToken ct);

    /// <summary>
    /// Writes parameter values. This is the one action that changes the model, so the implementation sends it
    /// through the connector's approval path; the answer may be "approval_required" with a request id.
    /// </summary>
    Task<object> SetAsync(ModelPageInfo page, IReadOnlyList<ModelPageChange> changes, CancellationToken ct);

    /// <summary>What became of a write that needed approval.</summary>
    Task<object> ApprovalAsync(ModelPageInfo page, string requestId, CancellationToken ct);

    /// <summary>
    /// A shared-memory review action from the memory page (confirm, retire, promote, accept or reject a proposal).
    /// Like <see cref="SetAsync"/> it goes through the connector's approval path.
    /// </summary>
    Task<object> MemoryAsync(ModelPageInfo page, ModelPageMemoryAction action, CancellationToken ct);
}

/// <summary>A validated memory review request from a page.</summary>
public sealed class ModelPageMemoryAction
{
    public static readonly string[] Actions = { "confirm", "retire", "promote", "accept", "reject" };
    public const int MaxIds = 100;

    public string Action { get; set; } = string.Empty;
    public List<string> Ids { get; set; } = new();
    public string? Proposal { get; set; }

    /// <summary>Parses the body: a known action, ids and proposal of letters, digits and hyphens only. Null with the problem.</summary>
    public static ModelPageMemoryAction? Parse(JObject json, out string? problem)
    {
        problem = null;
        var action = json["action"] is JValue a && a.Type == JTokenType.String ? a.Value<string>() : null;
        if (action == null || Array.IndexOf(Actions, action) < 0)
        {
            problem = "Unknown memory action.";
            return null;
        }
        var result = new ModelPageMemoryAction { Action = action };
        if (json["ids"] is JArray ids)
        {
            if (ids.Count > MaxIds)
            {
                problem = $"At most {MaxIds} entries at once.";
                return null;
            }
            foreach (var t in ids)
            {
                var id = t is JValue v && v.Type == JTokenType.String ? v.Value<string>() : null;
                if (!IsSafeId(id))
                {
                    problem = "Entry ids are letters, digits and hyphens.";
                    return null;
                }
                result.Ids.Add(id!);
            }
        }
        if (json["proposal"] is JValue pv && pv.Type == JTokenType.String)
        {
            var proposal = pv.Value<string>();
            if (!IsSafeId(proposal))
            {
                problem = "A proposal id is letters, digits and hyphens.";
                return null;
            }
            result.Proposal = proposal;
        }
        var forProposal = action is "accept" or "reject";
        if (forProposal ? result.Proposal == null : result.Ids.Count == 0)
        {
            problem = forProposal ? "Name the proposal." : "Name the entries.";
            return null;
        }
        return result;
    }

    private static bool IsSafeId(string? id) =>
        !string.IsNullOrEmpty(id) && id!.Length <= 64 && id.All(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-');
}

/// <summary>
/// Loopback HTTP/1.1 server for model pages: HTML an AI agent generated from a model, served back to
/// the user's browser with a helper script that can select elements in Revit, read live parameter
/// values and change them — without the agent, so it costs no tokens. Same shape as the village listener
/// (<see cref="TcpListener"/>, no URL ACL, .NET Framework 4.8 and .NET 8).
///
/// Security model:
/// - binds to 127.0.0.1 only, and the Host header must name a loopback host (blocks DNS rebinding);
/// - GET/HEAD for "/", "/p/{id}", "/pages.js", "/pages.css" and "/health"; POST for "/api/status",
///   "/api/select", "/api/values", "/api/set", "/api/memory" and "/api/approval"; everything else is refused;
/// - a POST must carry this listener's per-start token in <c>X-Revit-Page-Token</c> (written only into
///   the pages it serves), a same-origin Sec-Fetch-Site and Origin when the browser sends them, and a
///   JSON body of at most <see cref="MaxBodyBytes"/> naming a published page;
/// - no CORS headers, so another origin can neither read a page (and its token) nor send the header;
/// - every response carries a policy that lets a page reach this listener only: a generated page cannot
///   load from or send to any other origin, and cannot be framed;
/// - the actions are fixed. Status, select and values never reach the MCP dispatcher and cannot change the
///   model. "/api/set" is the one model write: the backend hands it to the connector as a parameter write that
///   needs the user's approval in Revit (unless they switched Direct Edit on), exactly like an agent's.
///   "/api/memory" (the memory review page) takes the same approval path for shared-memory changes.
/// Pure — no Revit API, unit tested in RevitMCP.Tests.
/// </summary>
public sealed class ModelPageServer : IDisposable
{
    public const int DefaultPort = 47830;
    public const int PortFallbackAttempts = 10;
    public const int MaxRequestHeadBytes = 8 * 1024;
    public const int MaxBodyBytes = 256 * 1024;
    public const int RequestReadTimeoutMs = 5000;

    /// <summary>Most body bytes read and dropped after a 413, so the client can read the refusal.</summary>
    public const int MaxDiscardBytes = 1024 * 1024;
    public const int DiscardTimeoutMs = 2000;
    public const int ActionTimeoutMs = 20000;
    public const int MaxSelectIds = 20000;
    public const int MaxValueIds = 2000;
    public const int MaxValueParameters = 40;
    public const string TokenHeader = "X-Revit-Page-Token";

    internal const string CspHeader =
        "Content-Security-Policy: default-src 'none'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob:; font-src data:; connect-src 'self'; form-action 'none'; base-uri 'none'; frame-ancestors 'none'";

    private static readonly string[] AllowedHosts = { "127.0.0.1", "localhost" };

    private readonly ModelPageStore _store;
    private readonly IModelPageBackend _backend;
    private readonly Func<string> _script;
    private readonly Func<string> _styles;
    private readonly Action<string>? _log;
    private readonly int _preferredPort;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public ModelPageServer(ModelPageStore store, IModelPageBackend backend, Func<string> scriptProvider, int port = DefaultPort,
        Action<string>? log = null, Func<string>? stylesProvider = null)
    {
        _store = store;
        _backend = backend;
        _script = scriptProvider;
        _styles = stylesProvider ?? (() => string.Empty);
        _preferredPort = port;
        _log = log;
        Token = NewToken();
    }

    /// <summary>The secret a POST must echo. New on every start of the add-in.</summary>
    public string Token { get; }

    public int Port { get; private set; }
    public bool IsListening => _listener != null && _cts != null && !_cts.IsCancellationRequested;
    public string BaseUrl => "http://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture) + "/";
    public string PageUrl(string id) => BaseUrl + "p/" + id;

    /// <summary>Binds the loopback listener, trying the next ports when one is taken. Never throws.</summary>
    public bool Start()
    {
        if (IsListening) return true;
        var attempts = _preferredPort == 0 ? 1 : PortFallbackAttempts;
        for (var i = 0; i < attempts; i++)
        {
            var port = _preferredPort == 0 ? 0 : _preferredPort + i;
            if (port > 65535) break;
            var listener = new TcpListener(IPAddress.Loopback, port);
            try
            {
                listener.ExclusiveAddressUse = true;
                listener.Start(backlog: 16);
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                _listener = listener;
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _ = Task.Run(() => AcceptLoopAsync(listener, token), CancellationToken.None);
                Log("listening on " + BaseUrl);
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
    }

    public void Dispose() => Stop();

    // ─── Accept / handle ────────────────────────────────────────────────────

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient socket;
            try { socket = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
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
            _ = Task.Run(() => HandleAsync(socket, ct), CancellationToken.None);
        }
    }

    private async Task HandleAsync(TcpClient socket, CancellationToken ct)
    {
        try
        {
            socket.NoDelay = true;
            var stream = socket.GetStream();
            var (head, leftover) = await ReadHeadAsync(stream, ct).ConfigureAwait(false);
            var request = head == null ? null : ParseRequest(head);
            if (request == null)
            {
                await WriteAsync(stream, 400, "Bad Request", "text/plain", "bad request", ct).ConfigureAwait(false);
                return;
            }
            if (!IsAllowedHost(request.Host))
            {
                await WriteAsync(stream, 421, "Misdirected Request", "text/plain", "loopback only", ct).ConfigureAwait(false);
                return;
            }

            if (request.Method == "POST" && IsApiRoute(request.Path))
            {
                await HandleApiAsync(stream, request, leftover, ct).ConfigureAwait(false);
                return;
            }
            if (request.Method != "GET" && request.Method != "HEAD")
            {
                await WriteAsync(stream, 405, "Method Not Allowed", "text/plain", "not allowed", ct, extraHeader: "Allow: GET, HEAD").ConfigureAwait(false);
                return;
            }

            var headOnly = request.Method == "HEAD";
            if (request.Path == "/" || request.Path == "/index.html")
            {
                await WriteAsync(stream, 200, "OK", "text/html; charset=utf-8", ModelPageHtml.Index(_store.List()), ct, headOnly).ConfigureAwait(false);
                return;
            }
            if (request.Path == ModelPageHtml.ScriptPath)
            {
                await WriteAsync(stream, 200, "OK", "application/javascript; charset=utf-8", Safe(_script), ct, headOnly).ConfigureAwait(false);
                return;
            }
            if (request.Path == ModelPageHtml.StylesPath)
            {
                await WriteAsync(stream, 200, "OK", "text/css; charset=utf-8", Safe(_styles), ct, headOnly).ConfigureAwait(false);
                return;
            }
            if (request.Path == "/health")
            {
                await WriteAsync(stream, 200, "OK", "application/json; charset=utf-8", "{\"ok\":true}", ct, headOnly).ConfigureAwait(false);
                return;
            }

            var pageId = PageIdFrom(request.Path);
            var html = pageId == null ? null : _store.ReadHtml(pageId);
            if (html == null || _store.Find(pageId!) == null)
            {
                await WriteAsync(stream, 404, "Not Found", "text/plain", "not found", ct).ConfigureAwait(false);
                return;
            }
            await WriteAsync(stream, 200, "OK", "text/html; charset=utf-8", ModelPageHtml.Inject(html, pageId!, Token, _store.Find(pageId!)?.Title), ct, headOnly).ConfigureAwait(false);
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
            try { socket.Close(); } catch { }
        }
    }

    // ─── Routes ─────────────────────────────────────────────────────────────

    public static bool IsApiRoute(string path) =>
        path == "/api/status" || path == "/api/select" || path == "/api/values" || path == "/api/set" || path == "/api/memory" || path == "/api/approval";

    /// <summary>The id in "/p/{id}", or null.</summary>
    public static string? PageIdFrom(string path)
    {
        const string prefix = "/p/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var id = path.Substring(prefix.Length);
        return ModelPageStore.IsValidId(id) ? id : null;
    }

    /// <summary>Loopback hosts only; a request without a Host header is not a browser's and is refused too.</summary>
    public static bool IsAllowedHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var h = host!.Trim().ToLowerInvariant();
        var colon = h.IndexOf(':');
        if (colon >= 0) h = h.Substring(0, colon);
        return Array.IndexOf(AllowedHosts, h) >= 0;
    }

    /// <summary>Why a POST is refused, or null when it may proceed.</summary>
    private string? Refuse(Request request)
    {
        if (!FixedTimeEquals(request.Token, Token)) return "missing or wrong page token";
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

    private async Task HandleApiAsync(NetworkStream stream, Request request, byte[] leftover, CancellationToken ct)
    {
        var refusal = Refuse(request);
        if (refusal != null)
        {
            Log("request refused: " + refusal);
            await WriteJsonAsync(stream, 403, "Forbidden", Fail("forbidden", refusal), ct).ConfigureAwait(false);
            return;
        }
        if (request.Chunked || request.ContentLength == null || request.ContentLength < 0)
        {
            await WriteJsonAsync(stream, 411, "Length Required", Fail("bad_request", "Content-Length required."), ct).ConfigureAwait(false);
            return;
        }
        if (request.ContentLength > MaxBodyBytes)
        {
            await WriteJsonAsync(stream, 413, "Payload Too Large", Fail("too_large", "The request is too large."), ct).ConfigureAwait(false);
            // Closing with the body unread resets the connection, and the client then sees the reset
            // instead of the refusal. Drop what it sends (bounded), never acting on it.
            await DiscardBodyAsync(stream, request.ContentLength.Value - leftover.Length, ct).ConfigureAwait(false);
            return;
        }
        if (!IsJsonType(request.ContentType))
        {
            await WriteJsonAsync(stream, 415, "Unsupported Media Type", Fail("bad_request", "application/json only."), ct).ConfigureAwait(false);
            return;
        }

        var body = await ReadBodyAsync(stream, leftover, (int)request.ContentLength.Value, ct).ConfigureAwait(false);
        var json = body == null ? null : ParseBody(body);
        if (json == null)
        {
            await WriteJsonAsync(stream, 400, "Bad Request", Fail("bad_request", "The request body is not a JSON object."), ct).ConfigureAwait(false);
            return;
        }

        var pageId = json["page"] is JValue pv && pv.Type == JTokenType.String ? pv.Value<string>() : null;
        var page = pageId == null ? null : _store.Find(pageId);
        if (page == null)
        {
            await WriteJsonAsync(stream, 404, "Not Found", Fail("unknown_page", "This page is no longer published."), ct).ConfigureAwait(false);
            return;
        }

        object result;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ActionTimeoutMs);
        try
        {
            switch (request.Path)
            {
                case "/api/select":
                {
                    var ids = ReadIds(json["ids"], MaxSelectIds, out var tooMany);
                    if (tooMany || ids.Count == 0)
                    {
                        await WriteJsonAsync(stream, 400, "Bad Request", Fail("bad_request",
                            tooMany ? $"At most {MaxSelectIds} elements can be selected at once." : "Name the elements to select."), ct).ConfigureAwait(false);
                        return;
                    }
                    var zoom = !(json["zoom"] is JValue zv && zv.Type == JTokenType.Boolean && !zv.Value<bool>());
                    result = await _backend.SelectAsync(page, ids, zoom, timeout.Token).ConfigureAwait(false);
                    break;
                }
                case "/api/values":
                {
                    var ids = ReadIds(json["ids"], MaxValueIds, out var tooMany);
                    var parameters = ReadNames(json["parameters"], MaxValueParameters, out var tooManyNames);
                    if (tooMany || tooManyNames || ids.Count == 0 || parameters.Count == 0)
                    {
                        await WriteJsonAsync(stream, 400, "Bad Request", Fail("bad_request",
                            tooMany || tooManyNames
                                ? $"At most {MaxValueIds} elements and {MaxValueParameters} parameters per request."
                                : "Name the elements and the parameters to read."), ct).ConfigureAwait(false);
                        return;
                    }
                    result = await _backend.ValuesAsync(page, ids, parameters, timeout.Token).ConfigureAwait(false);
                    break;
                }
                case "/api/set":
                {
                    var changes = ModelPageEdits.Parse(json["changes"], out var problem);
                    if (changes == null)
                    {
                        await WriteJsonAsync(stream, 400, "Bad Request", Fail("bad_request", problem ?? "Name the values to change."), ct).ConfigureAwait(false);
                        return;
                    }
                    result = await _backend.SetAsync(page, changes, timeout.Token).ConfigureAwait(false);
                    break;
                }
                case "/api/memory":
                {
                    var action = ModelPageMemoryAction.Parse(json, out var problem);
                    if (action == null)
                    {
                        await WriteJsonAsync(stream, 400, "Bad Request", Fail("bad_request", problem ?? "Name the memory action."), ct).ConfigureAwait(false);
                        return;
                    }
                    result = await _backend.MemoryAsync(page, action, timeout.Token).ConfigureAwait(false);
                    break;
                }
                case "/api/approval":
                {
                    var requestId = json["requestId"] is JValue rv && rv.Type == JTokenType.String ? rv.Value<string>() : null;
                    if (string.IsNullOrWhiteSpace(requestId) || requestId!.Length > 100)
                    {
                        await WriteJsonAsync(stream, 400, "Bad Request", Fail("bad_request", "Name the request."), ct).ConfigureAwait(false);
                        return;
                    }
                    result = await _backend.ApprovalAsync(page, requestId.Trim(), timeout.Token).ConfigureAwait(false);
                    break;
                }
                default:
                    result = await _backend.StatusAsync(page, timeout.Token).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            result = Fail("busy", "Revit did not respond. Close any open dialog in Revit and try again.");
        }
        catch (Exception ex)
        {
            result = Fail("error", "The request failed in Revit: " + ex.Message);
        }
        await WriteJsonAsync(stream, 200, "OK", result, ct).ConfigureAwait(false);
    }

    /// <summary>The shape every refusal has, also used by the backend: ok=false, a status word and a sentence.</summary>
    public static Dictionary<string, object?> Fail(string status, string message) =>
        new() { ["ok"] = false, ["status"] = status, ["message"] = message };

    /// <summary>Distinct positive whole numbers from a JSON array (numbers or digit strings); anything else is dropped.</summary>
    public static List<long> ReadIds(JToken? token, int max, out bool tooMany)
    {
        tooMany = false;
        var ids = new List<long>();
        if (token is not JArray array) return ids;
        var seen = new HashSet<long>();
        foreach (var item in array)
        {
            long id;
            if (item.Type == JTokenType.Integer) id = item.Value<long>();
            else if (item.Type != JTokenType.String ||
                     !long.TryParse(item.Value<string>(), NumberStyles.None, CultureInfo.InvariantCulture, out id)) continue;
            if (id <= 0 || !seen.Add(id)) continue;
            if (ids.Count >= max) { tooMany = true; return ids; }
            ids.Add(id);
        }
        return ids;
    }

    /// <summary>Distinct non-empty parameter names of a sane length.</summary>
    public static List<string> ReadNames(JToken? token, int max, out bool tooMany)
    {
        tooMany = false;
        var names = new List<string>();
        if (token is not JArray array) return names;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array)
        {
            if (item.Type != JTokenType.String) continue;
            var name = item.Value<string>()?.Trim();
            if (string.IsNullOrEmpty(name) || name!.Length > 200 || !seen.Add(name)) continue;
            if (names.Count >= max) { tooMany = true; return names; }
            names.Add(name);
        }
        return names;
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
                if (end >= 0)
                {
                    var rest = new byte[total - end - 4];
                    Array.Copy(buffer, end + 4, rest, 0, rest.Length);
                    return (text.Substring(0, end), rest);
                }
            }
        }
        catch (OperationCanceledException) { }
        return (null, Array.Empty<byte>());
    }

    private static Request? ParseRequest(string head)
    {
        var lines = head.Split('\n');
        var parts = lines[0].TrimEnd('\r').Split(' ');
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return null;
        var method = parts[0];
        if (method.Length == 0 || method.Length > 10 || !method.All(char.IsLetter)) return null;

        var target = parts[1];
        if (target.Length == 0 || target.Length > 512 || target[0] != '/') return null;
        var q = target.IndexOf('?');
        var path = q >= 0 ? target.Substring(0, q) : target;
        if (path.IndexOf("..", StringComparison.Ordinal) >= 0 || path.Any(c => c < 33 || c > 126)) return null;

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
                if (hostSeen) return null;
                hostSeen = true;
                request.Host = value;
            }
            else if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase)) request.Origin = value;
            else if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) request.ContentType = value;
            else if (name.Equals(TokenHeader, StringComparison.OrdinalIgnoreCase)) request.Token = value;
            else if (name.Equals("Sec-Fetch-Site", StringComparison.OrdinalIgnoreCase)) request.FetchSite = value;
            else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) request.Chunked = true;
            else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                request.ContentLength = long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1;
        }
        return request;
    }

    private static bool IsJsonType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return false;
        var semi = contentType!.IndexOf(';');
        var media = (semi >= 0 ? contentType.Substring(0, semi) : contentType).Trim();
        return media.Equals("application/json", StringComparison.OrdinalIgnoreCase);
    }

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

    private static async Task DiscardBodyAsync(NetworkStream stream, long remaining, CancellationToken ct)
    {
        remaining = Math.Min(remaining, MaxDiscardBytes);
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
            using var reader = new JsonTextReader(new StringReader(Encoding.UTF8.GetString(body))) { MaxDepth = 8, DateParseHandling = DateParseHandling.None };
            return JToken.ReadFrom(reader) as JObject;
        }
        catch
        {
            return null;
        }
    }

    // ─── Responses ──────────────────────────────────────────────────────────

    private static Task WriteJsonAsync(NetworkStream stream, int status, string reason, object payload, CancellationToken ct) =>
        WriteAsync(stream, status, reason, "application/json; charset=utf-8", JsonConvert.SerializeObject(payload), ct);

    private static async Task WriteAsync(NetworkStream stream, int status, string reason, string contentType, string body,
        CancellationToken ct, bool headOnly = false, string? extraHeader = null)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(reason).Append("\r\n");
        sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
        sb.Append("Content-Length: ").Append(bytes.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        sb.Append("Cache-Control: no-store\r\n");
        sb.Append("X-Content-Type-Options: nosniff\r\n");
        sb.Append("X-Frame-Options: DENY\r\n");
        sb.Append("Referrer-Policy: no-referrer\r\n");
        sb.Append(CspHeader).Append("\r\n");
        if (!string.IsNullOrEmpty(extraHeader)) sb.Append(extraHeader).Append("\r\n");
        sb.Append("Connection: close\r\n\r\n");
        var head = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(head, 0, head.Length, ct).ConfigureAwait(false);
        if (!headOnly && bytes.Length > 0) await stream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string Safe(Func<string> provider)
    {
        try { return provider() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string NewToken()
    {
        var bytes = new byte[24];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        return string.Concat(bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
    }

    private void Log(string message)
    {
        try { _log?.Invoke("[model-pages] " + message); } catch { }
    }
}
