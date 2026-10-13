using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelPages;
using Xunit;

namespace RevitMCP.Tests;

public class ModelPageTests
{
    private sealed class FakeBackend : IModelPageBackend
    {
        public List<string> Calls { get; } = new();
        public int DelayMs { get; set; }

        public Task<object> StatusAsync(ModelPageInfo page, CancellationToken ct)
        {
            Calls.Add("status:" + page.Id);
            return Task.FromResult<object>(new { ok = true, status = "ready", activeDocument = "P125" });
        }

        public async Task<object> SelectAsync(ModelPageInfo page, IReadOnlyList<long> ids, bool zoom, CancellationToken ct)
        {
            Calls.Add($"select:{string.Join(",", ids)}:zoom={zoom}");
            if (DelayMs > 0) await Task.Delay(DelayMs, ct);
            return new { ok = true, status = "selected", selected = ids.Count };
        }

        public Task<object> ValuesAsync(ModelPageInfo page, IReadOnlyList<long> ids, IReadOnlyList<string> parameters, CancellationToken ct)
        {
            Calls.Add($"values:{string.Join(",", ids)}:{string.Join("|", parameters)}");
            return Task.FromResult<object>(new { ok = true, status = "read" });
        }

        public Task<object> SetAsync(ModelPageInfo page, IReadOnlyList<ModelPageChange> changes, CancellationToken ct)
        {
            Calls.Add("set:" + string.Join(";", changes.Select(c => $"{c.Id}/{c.Parameter}={c.Value}/was={c.Expected ?? "?"}")));
            return Task.FromResult<object>(new { ok = false, status = "approval_required", requestId = "req-1" });
        }

        public Task<object> ApprovalAsync(ModelPageInfo page, string requestId, CancellationToken ct)
        {
            Calls.Add("approval:" + requestId);
            return Task.FromResult<object>(new { ok = true, status = "written" });
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "revitmcp-pages-" + Guid.NewGuid().ToString("N"));
        public ModelPageStore Store { get; }
        public FakeBackend Backend { get; } = new();
        public ModelPageServer Server { get; }
        public HttpClient Http { get; } = new();
        public ModelPageInfo Page { get; }

        public Fixture()
        {
            Store = new ModelPageStore(Folder);
            Page = Store.Save(null, "KK.48 kontroll", "<html><body><tr data-revit-id=\"11\"><td>x</td></tr></body></html>", "model-a", "P125", "Claude", DateTimeOffset.Now);
            Server = new ModelPageServer(Store, Backend, () => "/* helper */", port: 0, stylesProvider: () => "/* theme */");
            Assert.True(Server.Start());
        }

        public string Origin => "http://127.0.0.1:" + Server.Port;

        public HttpRequestMessage Post(string path, string json, string? token = "", string? origin = null, string? fetchSite = null, string contentType = "application/json")
        {
            var message = new HttpRequestMessage(HttpMethod.Post, Origin + path) { Content = new StringContent(json, Encoding.UTF8, contentType) };
            if (token != null) message.Headers.Add(ModelPageServer.TokenHeader, token.Length == 0 ? Server.Token : token);
            if (origin != null) message.Headers.Add("Origin", origin);
            if (fetchSite != null) message.Headers.Add("Sec-Fetch-Site", fetchSite);
            return message;
        }

        public async Task<JObject> Json(HttpRequestMessage message) =>
            JObject.Parse(await (await Http.SendAsync(message)).Content.ReadAsStringAsync());

        public void Dispose()
        {
            Server.Dispose();
            Http.Dispose();
            try { Directory.Delete(Folder, true); } catch { }
        }
    }

    // ── store ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("kk-48-kontroll-3fa9c1", true)]
    [InlineData("a", true)]
    [InlineData("", false)]
    [InlineData("-a", false)]
    [InlineData("A", false)]
    [InlineData("a/b", false)]
    [InlineData("a.b", false)]
    [InlineData("..", false)]
    public void IsValidId_AllowsOnlyAFileSafeSlug(string id, bool expected) =>
        Assert.Equal(expected, ModelPageStore.IsValidId(id));

    [Fact]
    public void NewId_IsASlugOfTheTitle()
    {
        var id = ModelPageStore.NewId("KK.48 – Ahelate kontroll (öö)");
        Assert.StartsWith("kk-48-ahelate-kontroll-oo-", id);
        Assert.True(ModelPageStore.IsValidId(id));
        Assert.True(ModelPageStore.IsValidId(ModelPageStore.NewId(new string('ä', 300))));
        Assert.StartsWith("page-", ModelPageStore.NewId("???"));
    }

    [Fact]
    public void Save_ReplacesAPageByIdAndKeepsItsCreationTime()
    {
        using var f = new Fixture();
        var later = f.Page.CreatedAt.AddHours(1);
        var replaced = f.Store.Save(f.Page.Id, "Uus pealkiri", "<p>v2</p>", "model-a", "P125", null, later);
        Assert.Equal(f.Page.Id, replaced.Id);
        Assert.Equal(f.Page.CreatedAt, replaced.CreatedAt);
        Assert.Equal("<p>v2</p>", f.Store.ReadHtml(f.Page.Id));
        Assert.Single(f.Store.List());
        Assert.True(f.Store.Remove(f.Page.Id));
        Assert.Empty(f.Store.List());
        Assert.False(f.Store.Remove(f.Page.Id));
    }

    [Fact]
    public void Save_RefusesEmptyOversizeAndUnsafeIds()
    {
        using var f = new Fixture();
        Assert.Throws<ArgumentException>(() => f.Store.Save(null, "t", " ", "m", "p", null, DateTimeOffset.Now));
        Assert.Throws<ArgumentException>(() => f.Store.Save(null, "t", new string('x', ModelPageStore.MaxHtmlBytes + 1), "m", "p", null, DateTimeOffset.Now));
        Assert.Throws<ArgumentException>(() => f.Store.Save("../evil", "t", "<p>", "m", "p", null, DateTimeOffset.Now));
        Assert.Null(f.Store.ReadHtml("../" + f.Page.Id));
    }

    // ── served html ──────────────────────────────────────────────────────────

    [Fact]
    public void Inject_PutsTheHelperBeforeTheEndOfTheBody()
    {
        var html = ModelPageHtml.Inject("<html><body><p>x</p></body></html>", "page-1", "tok");
        Assert.Contains("\"id\":\"page-1\"", html);
        Assert.Contains("\"token\":\"tok\"", html);
        Assert.True(html.IndexOf("/pages.js", StringComparison.Ordinal) < html.IndexOf("</body>", StringComparison.Ordinal));
        Assert.EndsWith("</script>", ModelPageHtml.Inject("<p>fragment</p>", "page-1", "tok"));
        Assert.DoesNotContain("</script><script>alert", ModelPageHtml.Inject("<p>", "page-1", "</script><script>alert(1)"));
    }

    [Fact]
    public void Inject_LinksTheDefaultStylesBeforeThePagesOwn()
    {
        var html = ModelPageHtml.Inject("<html lang=\"et\"><head><style>body{color:red}</style></head><body></body></html>", "p", "t", "Pealkiri <1>");
        Assert.True(html.IndexOf("/pages.css", StringComparison.Ordinal) < html.IndexOf("body{color:red}", StringComparison.Ordinal));
        Assert.True(html.IndexOf("<head>", StringComparison.Ordinal) < html.IndexOf("/pages.css", StringComparison.Ordinal));
        Assert.Contains("Pealkiri \\u003c1>", html);
        Assert.StartsWith("<html><link", ModelPageHtml.Inject("<html><body></body></html>", "p", "t"));
        Assert.StartsWith("<link", ModelPageHtml.Inject("<p>fragment</p>", "p", "t"));
    }

    [Fact]
    public void Review_FlagsExternalResourcesAndPagesWithoutElements()
    {
        Assert.Empty(ModelPageHtml.Review("<table><tr data-revit-id=\"1\"><td><img src=\"data:image/png;base64,AA\"></td></tr></table>"));
        Assert.Contains(ModelPageHtml.Review("<script src=\"https://cdn.example.com/x.js\"></script><p data-revit-id=\"1\">"), n => n.Contains("another site"));
        Assert.Contains(ModelPageHtml.Review("<p>plain</p>"), n => n.Contains("data-revit-id"));
        Assert.Empty(ModelPageHtml.Review("<a href=\"https://example.com\">link</a><p data-revit-id=\"1\">"));   // a link is not a load
    }

    [Fact]
    public void Index_EscapesTitles()
    {
        var html = ModelPageHtml.Index([new ModelPageInfo { Id = "p-1", Title = "<script>x</script>", ProjectName = "A&B" }]);
        Assert.DoesNotContain("<script>x", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("A&amp;B", html);
        Assert.Contains("href=\"/p/p-1\"", html);
    }

    // ── listener ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_ServesThePageWithItsTokenAndALockedDownPolicy()
    {
        using var f = new Fixture();
        var response = await f.Http.GetAsync(f.Server.PageUrl(f.Page.Id));
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(f.Server.Token, html);
        Assert.Contains("data-revit-id=\"11\"", html);
        var csp = string.Join(" ", response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'none'", csp);
        Assert.Contains("connect-src 'self'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));

        Assert.Contains("KK.48 kontroll", await f.Http.GetStringAsync(f.Origin + "/"));
        Assert.Equal("/* helper */", await f.Http.GetStringAsync(f.Origin + "/pages.js"));
        Assert.Equal("/* theme */", await f.Http.GetStringAsync(f.Origin + "/pages.css"));
        Assert.Contains("style-src 'self'", csp);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Http.GetAsync(f.Origin + "/p/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Http.GetAsync(f.Origin + "/api/select")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await f.Http.DeleteAsync(f.Server.PageUrl(f.Page.Id))).StatusCode);
    }

    [Fact]
    public async Task Post_SelectsAndReadsThroughTheBackend()
    {
        using var f = new Fixture();
        var body = $"{{\"page\":\"{f.Page.Id}\",\"ids\":[11,\"12\",11,-3,\"x\",1.5],\"zoom\":false}}";
        var selected = await f.Json(f.Post("/api/select", body, origin: f.Origin, fetchSite: "same-origin"));
        Assert.True((bool)selected["ok"]!);
        Assert.Contains("select:11,12:zoom=False", f.Backend.Calls);

        await f.Json(f.Post("/api/values", $"{{\"page\":\"{f.Page.Id}\",\"ids\":[11],\"parameters\":[\"Comments\",\" Mark \",\"\",5]}}"));
        Assert.Contains("values:11:Comments|Mark", f.Backend.Calls);

        await f.Json(f.Post("/api/status", $"{{\"page\":\"{f.Page.Id}\"}}"));
        Assert.Contains("status:" + f.Page.Id, f.Backend.Calls);
    }

    [Fact]
    public async Task Post_IsRefusedWithoutTheTokenOrFromAnotherOrigin()
    {
        using var f = new Fixture();
        var body = $"{{\"page\":\"{f.Page.Id}\",\"ids\":[11]}}";
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Http.SendAsync(f.Post("/api/select", body, token: null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Http.SendAsync(f.Post("/api/select", body, token: new string('0', 48)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Http.SendAsync(f.Post("/api/select", body, origin: "https://evil.example"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Http.SendAsync(f.Post("/api/select", body, origin: "http://127.0.0.1:1"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Http.SendAsync(f.Post("/api/select", body, fetchSite: "cross-site"))).StatusCode);
        Assert.Empty(f.Backend.Calls);
    }

    [Fact]
    public async Task Post_ValidatesTheBody()
    {
        using var f = new Fixture();
        var id = f.Page.Id;
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await f.Http.SendAsync(f.Post("/api/select", $"{{\"page\":\"{id}\",\"ids\":[1]}}", contentType: "text/plain"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Http.SendAsync(f.Post("/api/select", "[]"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Http.SendAsync(f.Post("/api/select", "{\"page\":\"nope\",\"ids\":[1]}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Http.SendAsync(f.Post("/api/select", "{\"page\":\"../x\",\"ids\":[1]}"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Http.SendAsync(f.Post("/api/select", $"{{\"page\":\"{id}\",\"ids\":[]}}"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Http.SendAsync(f.Post("/api/values", $"{{\"page\":\"{id}\",\"ids\":[1]}}"))).StatusCode);
        var many = "[" + string.Join(",", Enumerable.Range(1, ModelPageServer.MaxValueIds + 1)) + "]";
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Http.SendAsync(f.Post("/api/values", $"{{\"page\":\"{id}\",\"ids\":{many},\"parameters\":[\"a\"]}}"))).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await f.Http.SendAsync(f.Post("/api/select", "{\"page\":\"" + new string('a', ModelPageServer.MaxBodyBytes) + "\"}"))).StatusCode);
        Assert.Empty(f.Backend.Calls);
    }

    [Fact]
    public async Task Post_Set_GoesToTheBackendOnlyWhenEveryChangeIsReadable()
    {
        using var f = new Fixture();
        var id = f.Page.Id;
        var waiting = await f.Json(f.Post("/api/set", $"{{\"page\":\"{id}\",\"changes\":[{{\"id\":\"11\",\"parameter\":\"Comments\",\"value\":\"PM\",\"expected\":\"KÜ\"}},{{\"id\":12,\"parameter\":\"Mark\",\"value\":7}}]}}"));
        Assert.Equal("approval_required", (string)waiting["status"]!);
        Assert.Contains("set:11/Comments=PM/was=KÜ;12/Mark=7/was=?", f.Backend.Calls);

        await f.Json(f.Post("/api/approval", $"{{\"page\":\"{id}\",\"requestId\":\"req-1\"}}"));
        Assert.Contains("approval:req-1", f.Backend.Calls);

        var calls = f.Backend.Calls.Count;
        foreach (var bad in new[]
                 {
                     "[]",
                     "[{\"id\":11,\"parameter\":\"\",\"value\":\"x\"}]",
                     "[{\"id\":0,\"parameter\":\"Mark\",\"value\":\"x\"}]",
                     "[{\"id\":11,\"parameter\":\"Mark\"}]",
                     "[{\"id\":11,\"parameter\":\"Mark\",\"value\":\"a\"},{\"id\":11,\"parameter\":\"Mark\",\"value\":\"b\"}]",
                     "[{\"id\":11,\"parameter\":\"Mark\",\"value\":{\"a\":1}}]"
                 })
            Assert.Equal(HttpStatusCode.BadRequest, (await f.Http.SendAsync(f.Post("/api/set", $"{{\"page\":\"{id}\",\"changes\":{bad}}}"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Http.SendAsync(f.Post("/api/approval", $"{{\"page\":\"{id}\"}}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Http.SendAsync(f.Post("/api/set", $"{{\"page\":\"{id}\",\"changes\":[{{\"id\":11,\"parameter\":\"Mark\",\"value\":\"x\"}}]}}", token: null))).StatusCode);
        Assert.Equal(calls, f.Backend.Calls.Count);
    }

    [Fact]
    public void Edits_SummaryNamesWhatAnApproverNeedsToKnow()
    {
        var one = new List<ModelPageChange> { new() { Id = 11, Parameter = "Comments", Value = "PM" } };
        Assert.Equal("Page 'KK.48 kontroll': set 'Comments' to \"PM\" on element 11.", ModelPageEdits.Summary("KK.48 kontroll", one));

        var many = new List<ModelPageChange>
        {
            new() { Id = 11, Parameter = "Comments", Value = "PM" },
            new() { Id = 12, Parameter = "Comments", Value = "PM" },
            new() { Id = 12, Parameter = "Mark", Value = "PM" }
        };
        Assert.Equal("Page 'KK.48 kontroll': set 'Comments', 'Mark' to \"PM\" on 2 elements (3 values).", ModelPageEdits.Summary("KK.48 kontroll", many));
        many[2].Value = "A1";
        Assert.DoesNotContain(" to \"", ModelPageEdits.Summary("KK.48 kontroll", many));
    }

    [Theory]
    [InlineData("Jah", true, true)]
    [InlineData("yes", true, true)]
    [InlineData("1", true, true)]
    [InlineData("ei", true, false)]
    [InlineData("No", true, false)]
    [InlineData("", true, false)]
    [InlineData("võib-olla", false, false)]
    public void Edits_YesNo_ReadsEnglishAndEstonian(string text, bool parsed, bool expected)
    {
        Assert.Equal(parsed, ModelPageEdits.TryParseYesNo(text, out var value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void Edits_StillExpected_ComparesWhatThePageShowed()
    {
        Assert.True(ModelPageEdits.StillExpected(null, "anything"));
        Assert.True(ModelPageEdits.StillExpected(" PM ", "PM"));
        Assert.True(ModelPageEdits.StillExpected("", null));
        Assert.False(ModelPageEdits.StillExpected("PM", "KÜ"));
        Assert.False(ModelPageEdits.StillExpected("", "KÜ"));
    }

    [Fact]
    public async Task ForeignHost_IsTurnedAway()
    {
        using var f = new Fixture();
        Assert.Contains("421", await RawAsync(f.Server.Port, $"GET /p/{f.Page.Id} HTTP/1.1\r\nHost: evil.example\r\n\r\n"));
        Assert.Contains("421", await RawAsync(f.Server.Port, $"GET /p/{f.Page.Id} HTTP/1.1\r\n\r\n"));
        Assert.Contains("200", await RawAsync(f.Server.Port, $"GET /p/{f.Page.Id} HTTP/1.1\r\nHost: localhost:{f.Server.Port}\r\n\r\n"));
    }

    [Fact]
    public void ReadIds_KeepsDistinctPositiveWholeNumbers()
    {
        var ids = ModelPageServer.ReadIds(JArray.Parse("[5,\"6\",5,0,-1,\"7a\",null,2.5]"), 10, out var tooMany);
        Assert.Equal([5L, 6L], ids);
        Assert.False(tooMany);
        ModelPageServer.ReadIds(JArray.Parse("[1,2,3]"), 2, out tooMany);
        Assert.True(tooMany);
    }

    private static async Task<string> RawAsync(int port, string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        var bytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(bytes);
        var buffer = new byte[4096];
        var read = await stream.ReadAsync(buffer);
        return Encoding.ASCII.GetString(buffer, 0, read).Split('\n')[0];
    }
}
