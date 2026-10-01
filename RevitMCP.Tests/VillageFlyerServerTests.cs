using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json.Linq;
using RevitMCP.Village;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>The flyer routes of the loopback listener and the guards on its only POST requests.</summary>
public class VillageFlyerServerTests
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private sealed class Fixture : IDisposable
    {
        public readonly VillageFlyerBoard Board = new(clock: () => DateTimeOffset.UtcNow);
        public readonly List<VillageShowRequest> Shown = new();
        public readonly VillageSseServer Server;
        public readonly VillageFlyer Flyer;

        public Fixture(bool withActions = true, bool withShow = true)
        {
            var found = new VillageFlyerExtraction();
            foreach (var id in new long[] { 101, 102, 103 })
                found.Items.Add(new VillageFlyerItem { Id = id, Name = "Detector " + id, Category = "Fire Alarm Devices" });
            Flyer = Board.Post("revit_find_elements", "Claude Code", new VillageProjectContext { ModelTitle = "M" }, found)!;

            Func<VillageShowRequest, CancellationToken, Task<VillageShowResult>> show = (req, ct) =>
            {
                lock (Shown) Shown.Add(req);
                return Task.FromResult(new VillageShowResult { Ok = true, Status = VillageShowStatus.Shown, Selected = req.ElementIds.Count, Message = "Selected." });
            };
            var actions = withActions ? new VillageFlyerActions(Board, () => withShow ? show : null) : null;
            Server = new VillageSseServer(new VillageOptions { Port = 0, PortFallback = false }, () => "{}",
                () => "<!doctype html><title>Project Village</title><script>const ACTION_TOKEN = '" + VillageSseServer.TokenPlaceholder + "';</script>",
                actions: actions);
            Assert.True(Server.Start());
        }

        public string Url(string path) => Server.BaseUrl.TrimEnd('/') + path;
        public string Origin => "http://127.0.0.1:" + Server.Port;

        public HttpRequestMessage Post(string path, string body, string? token = null, string? origin = null)
        {
            var m = new HttpRequestMessage(HttpMethod.Post, Url(path)) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            m.Headers.Add("X-Village-Token", token ?? Server.ActionToken);
            m.Headers.Add("Origin", origin ?? Origin);
            return m;
        }

        public void Dispose() => Server.Dispose();
    }

    private static async Task<string> RawAsync(int port, string request, int waitMs = 3000)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();
        var bytes = Encoding.UTF8.GetBytes(request);
        await stream.WriteAsync(bytes, 0, bytes.Length);
        using var cts = new CancellationTokenSource(waitMs);
        var buffer = new byte[64 * 1024];
        var total = 0;
        try
        {
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer, total, buffer.Length - total, cts.Token);
                if (read <= 0) break;
                total += read;
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    [Fact]
    public async Task ServesTheBoard_AndOneFlyerWithItems()
    {
        using var f = new Fixture();
        var board = JObject.Parse(await Http.GetStringAsync(f.Url("/flyers")));
        Assert.True(board.Value<bool>("enabled"));
        Assert.True(board.Value<bool>("show_available"));
        var listed = (JObject)((JArray)board["flyers"]!).Single();
        Assert.Equal(f.Flyer.Id, listed.Value<string>("id"));
        Assert.Null(listed["items"]);

        var one = JObject.Parse(await Http.GetStringAsync(f.Url("/flyers/" + f.Flyer.Id)));
        Assert.Equal(3, ((JArray)one["items"]!).Count);

        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync(f.Url("/flyers/f0000000000000000"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync(f.Url("/flyers/BAD-ID"))).StatusCode);
    }

    [Fact]
    public async Task PageCarriesThePerStartToken()
    {
        using var f = new Fixture();
        Assert.Matches("^[0-9a-f]{48}$", f.Server.ActionToken);
        var html = await Http.GetStringAsync(f.Url("/"));
        Assert.Contains("'" + f.Server.ActionToken + "'", html);
        Assert.DoesNotContain(VillageSseServer.TokenPlaceholder, html);

        using var other = new Fixture();
        Assert.NotEqual(f.Server.ActionToken, other.Server.ActionToken);
    }

    [Fact]
    public async Task Show_SelectsThroughTheHandler_OnlyFlyerIds()
    {
        using var f = new Fixture();
        var r = await Http.SendAsync(f.Post("/show", "{\"flyer\":\"" + f.Flyer.Id + "\",\"ids\":[102,999]}"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = JObject.Parse(await r.Content.ReadAsStringAsync());
        Assert.True(body.Value<bool>("ok"));
        var req = Assert.Single(f.Shown);
        Assert.Equal(new long[] { 102 }, req.ElementIds);
        Assert.Equal(new VillageProjectContext { ModelTitle = "M" }.ComputeModelId(), req.ModelId);

        var all = await Http.SendAsync(f.Post("/show", "{\"flyer\":\"" + f.Flyer.Id + "\"}"));
        Assert.True(JObject.Parse(await all.Content.ReadAsStringAsync()).Value<bool>("ok"));
        Assert.Equal(3, f.Shown[1].ElementIds.Count);

        var bad = await Http.SendAsync(f.Post("/show", "{\"ids\":[1]}"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(2, f.Shown.Count);
        Assert.True(f.Server.Stats.ActionsHandled >= 2);
    }

    [Fact]
    public async Task FlyerActions_ArchiveAndDismiss()
    {
        using var f = new Fixture();
        var archived = await Http.SendAsync(f.Post("/flyers/" + f.Flyer.Id + "/archive", "{}"));
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        Assert.Equal(VillageFlyerStates.Archived, f.Board.Get(f.Flyer.Id)!.State);

        var dismissed = await Http.SendAsync(f.Post("/flyers/" + f.Flyer.Id + "/dismiss", ""));
        Assert.Equal(HttpStatusCode.OK, dismissed.StatusCode);
        Assert.Null(f.Board.Get(f.Flyer.Id));

        var again = await Http.SendAsync(f.Post("/flyers/" + f.Flyer.Id + "/dismiss", "{}"));
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task ActionsRefuse_WrongTokenForeignOriginAndCrossSiteRequests()
    {
        using var f = new Fixture();
        var path = "/flyers/" + f.Flyer.Id + "/dismiss";

        Assert.Equal(HttpStatusCode.Forbidden, (await Http.SendAsync(f.Post(path, "{}", token: "0123"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Http.SendAsync(f.Post(path, "{}", token: new string('a', 48)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Http.SendAsync(f.Post(path, "{}", origin: "http://evil.example.com"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Http.SendAsync(f.Post(path, "{}", origin: "http://127.0.0.1:1"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Http.SendAsync(f.Post(path, "{}", origin: "null"))).StatusCode);

        var crossSite = f.Post(path, "{}");
        crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.Forbidden, (await Http.SendAsync(crossSite)).StatusCode);

        var noToken = new HttpRequestMessage(HttpMethod.Post, f.Url(path)) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        Assert.Equal(HttpStatusCode.Forbidden, (await Http.SendAsync(noToken)).StatusCode);

        // A form post (the one cross-site request a browser sends without a preflight) has neither the token nor JSON.
        var form = await RawAsync(f.Server.Port, $"POST /show HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/x-www-form-urlencoded\r\nContent-Length: 3\r\n\r\na=b");
        Assert.Contains("403", form);

        var sameOrigin = f.Post(path, "{}");
        sameOrigin.Headers.Add("Sec-Fetch-Site", "same-origin");
        Assert.NotNull(f.Board.Get(f.Flyer.Id));
        Assert.True(f.Server.Stats.ActionsRefused >= 8);
        Assert.Empty(f.Shown);
        Assert.Equal(HttpStatusCode.OK, (await Http.SendAsync(sameOrigin)).StatusCode);
    }

    [Fact]
    public async Task ActionsRefuse_BadBodies()
    {
        using var f = new Fixture();
        var t = f.Server.ActionToken;
        var port = f.Server.Port;

        var text = f.Post("/show", "{\"flyer\":\"" + f.Flyer.Id + "\"}");
        text.Content = new StringContent("{\"flyer\":\"" + f.Flyer.Id + "\"}", Encoding.UTF8, "text/plain");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await Http.SendAsync(text)).StatusCode);

        var big = "{\"flyer\":\"" + f.Flyer.Id + "\",\"pad\":\"" + new string('x', VillageSseServer.MaxActionBodyBytes) + "\"}";
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Http.SendAsync(f.Post("/show", big))).StatusCode);

        Assert.Contains("411", await RawAsync(port, $"POST /show HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Village-Token: {t}\r\nContent-Type: application/json\r\n\r\n"));
        Assert.Contains("411", await RawAsync(port, $"POST /show HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Village-Token: {t}\r\nTransfer-Encoding: chunked\r\nContent-Type: application/json\r\n\r\n0\r\n\r\n"));
        Assert.Contains("400", await RawAsync(port, $"POST /show HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Village-Token: {t}\r\nContent-Type: application/json\r\nContent-Length: 9\r\n\r\nnot json!"));
        Assert.Contains("400", await RawAsync(port, $"POST /show HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Village-Token: {t}\r\nContent-Type: application/json\r\nContent-Length: 2\r\n\r\n[]"));
        // Body promised but never sent: answered after the read timeout, never acted on.
        Assert.Contains("400", await RawAsync(port, $"POST /show HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Village-Token: {t}\r\nContent-Type: application/json\r\nContent-Length: 50\r\n\r\n{{\"flyer\"", VillageSseServer.RequestReadTimeoutMs + 3000));
        var tooMany = "{\"flyer\":\"" + f.Flyer.Id + "\",\"ids\":[" + string.Join(",", Enumerable.Range(1, VillageFlyerActions.MaxShowIds + 1)) + "]}";
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Http.SendAsync(f.Post("/show", tooMany))).StatusCode);
        Assert.Empty(f.Shown);

        // A foreign Host header is refused before any action is considered.
        Assert.Contains("421", await RawAsync(port, $"POST /show HTTP/1.1\r\nHost: evil.example.com\r\nX-Village-Token: {t}\r\nContent-Type: application/json\r\nContent-Length: 2\r\n\r\n{{}}"));
    }

    [Fact]
    public async Task OnlyTheActionRoutesTakePost()
    {
        using var f = new Fixture();
        foreach (var path in new[] { "/snapshot", "/flyers", "/flyers/" + f.Flyer.Id, "/flyers/" + f.Flyer.Id + "/delete", "/events", "/" })
        {
            var r = await Http.SendAsync(f.Post(path, "{}"));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, r.StatusCode);
        }
        var put = new HttpRequestMessage(HttpMethod.Put, f.Url("/show")) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        put.Headers.Add("X-Village-Token", f.Server.ActionToken);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await Http.SendAsync(put)).StatusCode);
    }

    [Fact]
    public async Task WithoutActions_TheListenerStaysGetOnly()
    {
        using var f = new Fixture(withActions: false);
        Assert.Equal(string.Empty, f.Server.ActionToken);
        Assert.False(f.Server.AcceptsActions);
        var html = await Http.GetStringAsync(f.Url("/"));
        Assert.Contains("const ACTION_TOKEN = '';", html);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync(f.Url("/flyers"))).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await Http.SendAsync(f.Post("/show", "{\"flyer\":\"" + f.Flyer.Id + "\"}", token: "x"))).StatusCode);
        Assert.Contains("\"actions\":false", await Http.GetStringAsync(f.Url("/health")));
    }

    [Fact]
    public async Task WithoutRevitHandler_ShowAnswersUnavailable()
    {
        using var f = new Fixture(withShow: false);
        var board = JObject.Parse(await Http.GetStringAsync(f.Url("/flyers")));
        Assert.False(board.Value<bool>("show_available"));
        var r = await Http.SendAsync(f.Post("/show", "{\"flyer\":\"" + f.Flyer.Id + "\"}"));
        var body = JObject.Parse(await r.Content.ReadAsStringAsync());
        Assert.False(body.Value<bool>("ok"));
        Assert.Equal(VillageShowStatus.Unavailable, body.Value<string>("status"));
    }
}
