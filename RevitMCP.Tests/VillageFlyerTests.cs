using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCP.Village;
using Xunit;

namespace RevitMCP.Tests;

public class VillageFlyerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rkmcp_flyers_" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static VillageProjectContext Context(string title = "1626_PP_EN") => new()
    {
        ModelTitle = title,
        CentralPath = @"\\server\bim\" + title + ".rvt",
        RevitVersion = "2026",
        IsWorkshared = true
    };

    private VillageFlyerBoard Board(string? folder = null, int maxFlyers = 30, int archiveDays = 30) =>
        new(maxFlyers, archiveDays, folder, clock: () => _now, zone: TimeZoneInfo.Utc);

    private static VillageFlyerExtraction Found(params long[] ids)
    {
        var e = new VillageFlyerExtraction();
        foreach (var id in ids) e.Items.Add(new VillageFlyerItem { Id = id, Name = "E" + id, Category = id % 2 == 0 ? "Lighting Fixtures" : "Fire Alarm Devices" });
        e.Total = ids.Length;
        return e;
    }

    // Shapes the add-in's tools return (see ElementInfoDto / ParameterValueDto).
    public sealed class ParameterDto
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public object? RawValue { get; set; }
        public long? TypeElementId { get; set; }
    }

    public sealed class ElementDto
    {
        public long ElementId { get; set; }
        public string Category { get; set; } = string.Empty;
        public string Family { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public long? TypeElementId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Level { get; set; } = string.Empty;
        public Dictionary<string, ParameterDto> Parameters { get; set; } = new();
    }

    public sealed class Cyclic
    {
        public long ElementId { get; set; } = 77;
        public string Name { get; set; } = "loop";
        public Cyclic? Self { get; set; }
    }

    // ─── Extraction ─────────────────────────────────────────────────────────

    [Fact]
    public void Extract_ElementDtos_KeepsRoutingFieldsAndNeverParameterValues()
    {
        var data = new
        {
            itemsReturned = 2,
            elements = new List<ElementDto>
            {
                new()
                {
                    ElementId = 1001, Category = "Fire Alarm Devices", Family = "Smoke Detector", Type = "ATS-O", Name = "ATS-O",
                    Level = "Level 1", TypeElementId = 555,
                    Parameters = { ["Mark"] = new ParameterDto { Name = "Mark", Value = "SECRET-123", RawValue = 9999, TypeElementId = 4444 } }
                },
                new() { ElementId = 1002, Category = "Lighting Fixtures", Name = "Downlight", Level = "Level 2" }
            }
        };

        var found = VillageFlyerExtractor.Extract(data, 100);

        Assert.Equal(new long[] { 1001, 1002 }, found.Items.Select(i => i.Id));
        var first = found.Items[0];
        Assert.Equal("Fire Alarm Devices", first.Category);
        Assert.Equal("Smoke Detector", first.Family);
        Assert.Equal("ATS-O", first.Type);
        Assert.Equal("Level 1", first.Level);
        Assert.False(found.Truncated);
        var json = JsonConvert.SerializeObject(found.Items);
        Assert.DoesNotContain("SECRET", json);
        Assert.DoesNotContain("9999", json);
        Assert.DoesNotContain("4444", json);
        Assert.DoesNotContain("555", json); // a type id is not an element on the flyer
    }

    [Fact]
    public void Extract_GraphNodesWithStringIds_AndSkipsExtraHints()
    {
        var data = JObject.Parse(@"{
            ""operation"": ""subtree"",
            ""root"": { ""id"": ""123456"", ""kind"": ""panel"", ""name"": ""JK-1"", ""level"": ""1. korrus"" },
            ""nodes"": [
                { ""depth"": 1, ""parentId"": ""123456"", ""node"": { ""id"": ""234567"", ""kind"": ""circuit"", ""name"": ""JK-1/3"", ""extra"": { ""id"": ""999999"", ""name"": ""hint"" } } },
                { ""depth"": 2, ""parentId"": ""234567"", ""node"": { ""id"": ""345678"", ""kind"": ""element"", ""name"": ""Socket"", ""category"": ""Electrical Fixtures"" } }
            ],
            ""stale"": false,
            ""note"": ""Graph values are for routing only""
        }");

        var found = VillageFlyerExtractor.Extract(data, 100);

        Assert.Equal(new long[] { 123456, 234567, 345678 }, found.Items.Select(i => i.Id));
        Assert.Equal("Electrical Fixtures", found.Items[2].Category);
        Assert.DoesNotContain(found.Items, i => i.Id == 999999);
    }

    [Fact]
    public void Extract_IdListsAndBareIds()
    {
        // Selection tools return id lists; a bare "id" with no name or category is not an element.
        var selection = new { selectedCount = 3, selectedElementIds = new List<long> { 5, 6, 7 }, invalidCount = 0 };
        Assert.Equal(new long[] { 5, 6, 7 }, VillageFlyerExtractor.Extract(selection, 100).Items.Select(i => i.Id));

        var issues = new { issues = new[] { new { id = 1, severity = "warn" }, new { id = 2, severity = "err" } } };
        Assert.Empty(VillageFlyerExtractor.Extract(issues, 100).Items);

        var skipped = new { invalidIds = new long[] { 8, 9 }, warnings = new[] { new { elementId = 10L, name = "x" } } };
        Assert.Empty(VillageFlyerExtractor.Extract(skipped, 100).Items);

        Assert.Empty(VillageFlyerExtractor.Extract(null, 100).Items);
        Assert.Empty(VillageFlyerExtractor.Extract("just text", 100).Items);
        Assert.Empty(VillageFlyerExtractor.Extract(new { elementId = -1L, name = "invalid" }, 100).Items);
    }

    [Fact]
    public void Extract_CapsItemsButCountsTheRest_AndMergesDuplicates()
    {
        var list = Enumerable.Range(1, 50).Select(i => new { elementId = (long)i, name = "E" + i }).ToList();
        var found = VillageFlyerExtractor.Extract(new { elements = list, again = new[] { new { elementId = 3L, category = "Doors" } } }, 10);

        Assert.Equal(10, found.Items.Count);
        Assert.Equal(50, found.Total);
        Assert.True(found.Truncated);
        Assert.Equal("Doors", found.Items.Single(i => i.Id == 3).Category);
    }

    [Fact]
    public void Extract_SurvivesCyclesAndHugeResults()
    {
        var c = new Cyclic(); c.Self = c;
        Assert.Single(VillageFlyerExtractor.Extract(c, 100).Items);

        var huge = Enumerable.Range(1, VillageFlyerExtractor.MaxVisitedNodes + 10).Select(i => (object)i).ToList();
        var found = VillageFlyerExtractor.Extract(new { values2 = huge }, 100);
        Assert.Empty(found.Items);
        Assert.True(found.Truncated);
    }

    [Fact]
    public void Extract_TrimsLongAndControlCharacterText()
    {
        var found = VillageFlyerExtractor.Extract(new { elementId = 1L, name = "A\u0001B" + new string('x', 500) }, 10);
        Assert.Equal(VillageFlyerExtractor.MaxTextLength, found.Items[0].Name!.Length);
        Assert.StartsWith("AB", found.Items[0].Name);
    }

    // ─── Board ──────────────────────────────────────────────────────────────

    [Fact]
    public void Post_PinsAFlyerWithSummaryFields_AndRepeatsOnlyRefreshIt()
    {
        var board = Board();
        var changes = 0;
        board.Changed += () => changes++;

        var f = board.Post("revit_get_elements_info", "Claude Code", Context(), Found(1, 2, 3))!;
        Assert.Equal("Get elements info", f.Title);
        Assert.Equal("revit_get_elements_info", f.Tool);
        Assert.Equal(Context().ComputeModelId(), f.ModelId);
        Assert.Equal(3, f.Total);
        Assert.Equal("Fire Alarm Devices", f.Categories[0].Name);
        Assert.Equal(2, f.Categories[0].Count);
        Assert.True(VillageFlyerBoard.IsValidId(f.Id));

        _now = _now.AddMinutes(1);
        var again = board.Post("revit_get_elements_info", "Claude Code", Context(), Found(1, 2, 3))!;
        Assert.Equal(f.Id, again.Id);
        Assert.Single(board.Summaries());
        Assert.Equal(_now, board.Summaries()[0].UpdatedAt);

        board.Post("revit_get_elements_info", "Claude Code", Context(), Found(1, 2));
        Assert.Equal(2, board.Summaries().Count);
        Assert.Equal(3, changes);

        Assert.Null(board.Post("revit_get_elements_info", "Claude Code", Context(), new VillageFlyerExtraction()));
        Assert.Null(board.Summaries()[0].Items);
        Assert.Equal(2, board.Get(board.Summaries()[0].Id)!.Items!.Count);
    }

    [Fact]
    public void TodaysFlyersClearAtMidnight_ArchivedOnesLastTheirDays()
    {
        var board = Board(archiveDays: 2);
        var keep = board.Post("revit_find_elements", null, Context(), Found(1))!;
        board.Post("revit_find_elements", null, Context(), Found(2));
        Assert.True(board.Apply(keep.Id, VillageFlyerBoard.ActionArchive));

        _now = _now.AddHours(11).AddMinutes(59);       // 23:59 the same day
        board.Tick();
        Assert.Equal(2, board.Count);

        _now = _now.AddMinutes(2);                      // next day
        var changed = false;
        board.Changed += () => changed = true;
        board.Tick();
        Assert.True(changed);
        var left = Assert.Single(board.Summaries());
        Assert.Equal(keep.Id, left.Id);
        Assert.Equal(VillageFlyerStates.Archived, left.State);

        _now = _now.AddDays(2);
        Assert.Empty(board.Summaries());
    }

    [Fact]
    public void Apply_ArchiveUnarchiveDismiss_AndRefusesUnknown()
    {
        var board = Board();
        var f = board.Post("revit_find_elements", null, Context(), Found(1))!;

        Assert.False(board.Apply("nope", VillageFlyerBoard.ActionArchive));
        Assert.False(board.Apply(f.Id, "delete_model"));
        Assert.True(board.Apply(f.Id, VillageFlyerBoard.ActionArchive));
        Assert.Equal(VillageFlyerStates.Archived, board.Get(f.Id)!.State);

        // Unarchived the next day: back on today's board, cleared with today's flyers.
        _now = _now.AddDays(1);
        Assert.True(board.Apply(f.Id, VillageFlyerBoard.ActionUnarchive));
        Assert.Equal(VillageFlyerStates.New, board.Get(f.Id)!.State);
        Assert.Single(board.Summaries());

        Assert.True(board.Apply(f.Id, VillageFlyerBoard.ActionDismiss));
        Assert.Null(board.Get(f.Id));
        Assert.False(board.Apply(f.Id, VillageFlyerBoard.ActionDismiss));
    }

    [Fact]
    public void Board_KeepsOnlyTheNewestFlyers()
    {
        var board = Board(maxFlyers: 5);
        for (var i = 1; i <= 8; i++) board.Post("revit_find_elements", null, Context(), Found(i));
        var list = board.Summaries();
        Assert.Equal(5, list.Count);
        Assert.Equal(8, board.Get(list[0].Id)!.Items![0].Id);
    }

    [Fact]
    public void Board_PersistsPerModelAndReloads()
    {
        var board = Board(_root);
        var a = board.Post("revit_find_elements", null, Context("A"), Found(1, 2))!;
        var b = board.Post("revit_find_elements", null, Context("B"), Found(3))!;
        board.Apply(b.Id, VillageFlyerBoard.ActionArchive);
        board.Flush();

        Assert.Equal(2, Directory.GetFiles(_root, "*.json").Length);
        var reloaded = Board(_root);
        reloaded.Load();
        Assert.Equal(new[] { b.Id, a.Id }.OrderBy(x => x), reloaded.Summaries().Select(f => f.Id).OrderBy(x => x));
        Assert.Equal(2, reloaded.Get(a.Id)!.Items!.Count);

        // Next day: today's flyer is gone on load, the archived one stays.
        _now = _now.AddDays(1);
        var tomorrow = Board(_root);
        tomorrow.Load();
        Assert.Equal(b.Id, Assert.Single(tomorrow.Summaries()).Id);

        // A model with no flyers left loses its file: A's expired on load, B's is dismissed.
        tomorrow.Apply(b.Id, VillageFlyerBoard.ActionDismiss);
        tomorrow.Flush();
        Assert.Empty(Directory.GetFiles(_root, "*.json"));

        File.WriteAllText(Path.Combine(_root, "broken.json"), "{ not json");
        var tolerant = Board(_root);
        tolerant.Load(); // never throws
    }

    [Theory]
    [InlineData("revit_get_elements_info", "Get elements info")]
    [InlineData("graph_query", "Graph query")]
    [InlineData("", "Query")]
    public void TitleFor_HumanizesToolNames(string tool, string title) => Assert.Equal(title, VillageFlyer.TitleFor(tool));

    // ─── Actions ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Show_SendsOnlyIdsOnTheFlyer_ForTheFlyersModel()
    {
        var board = Board();
        var f = board.Post("revit_find_elements", null, Context(), Found(1, 2, 3))!;
        VillageShowRequest? seen = null;
        var actions = new VillageFlyerActions(board, () => (req, ct) =>
        {
            seen = req;
            return Task.FromResult(new VillageShowResult { Ok = true, Status = VillageShowStatus.Shown, Selected = req.ElementIds.Count });
        });

        var all = await actions.ShowAsync(f.Id, null, CancellationToken.None);
        Assert.True(all.Ok);
        Assert.Equal(new long[] { 1, 2, 3 }, seen!.ElementIds.OrderBy(x => x));
        Assert.Equal(Context().ComputeModelId(), seen.ModelId);

        var subset = await actions.ShowAsync(f.Id, new long[] { 2, 2, 999 }, CancellationToken.None);
        Assert.True(subset.Ok);
        Assert.Equal(new long[] { 2 }, seen.ElementIds);

        var foreign = await actions.ShowAsync(f.Id, new long[] { 999 }, CancellationToken.None);
        Assert.Equal(VillageShowStatus.BadRequest, foreign.Status);

        var gone = await actions.ShowAsync("f0000000000000000", null, CancellationToken.None);
        Assert.Equal(VillageShowStatus.NotFound, gone.Status);
    }

    [Fact]
    public async Task Show_WithoutHandlerOrWhenOff_IsUnavailable()
    {
        var board = Board();
        var f = board.Post("revit_find_elements", null, Context(), Found(1))!;

        var noHandler = new VillageFlyerActions(board);
        Assert.False(noHandler.ShowAvailable);
        Assert.Equal(VillageShowStatus.Unavailable, (await noHandler.ShowAsync(f.Id, null, CancellationToken.None)).Status);
        Assert.Contains("\"show_available\":false", noHandler.BoardJson());

        var off = new VillageFlyerActions(board, () => (r, c) => Task.FromResult(new VillageShowResult { Ok = true }), enabled: false);
        Assert.Equal(VillageShowStatus.Unavailable, (await off.ShowAsync(f.Id, null, CancellationToken.None)).Status);
        Assert.Contains("\"enabled\":false", off.BoardJson());
        Assert.Contains("\"flyers\":[]", off.BoardJson());
        Assert.Null(off.FlyerJson(f.Id));
        Assert.False(off.Apply(f.Id, VillageFlyerBoard.ActionArchive));
    }

    [Fact]
    public async Task Show_HandlerFailuresBecomeResults()
    {
        var board = Board();
        var f = board.Post("revit_find_elements", null, Context(), Found(1))!;
        var throwing = new VillageFlyerActions(board, () => (r, c) => throw new InvalidOperationException("boom"));
        var r1 = await throwing.ShowAsync(f.Id, null, CancellationToken.None);
        Assert.False(r1.Ok);
        Assert.Equal(VillageShowStatus.Error, r1.Status);

        using var cts = new CancellationTokenSource();
        var never = new TaskCompletionSource<VillageShowResult>();
        var hanging = new VillageFlyerActions(board, () => (r, c) => never.Task);
        cts.CancelAfter(100);
        var r2 = await hanging.ShowAsync(f.Id, null, cts.Token);
        Assert.Equal(VillageShowStatus.Busy, r2.Status);
    }

    // ─── Hub ────────────────────────────────────────────────────────────────

    [Fact]
    public void Hub_PostsFlyersForReadsOnly()
    {
        var board = Board();
        using var hub = new VillageStateHub(new VillageOptions(), clock: () => _now, flyers: board);
        var elements = new { elements = new[] { new { elementId = 11L, name = "A", category = "Doors" } } };

        hub.ToolCompleted("revit_get_elements_info", "Claude Code", true, null, 5, elements, Context());
        Assert.Equal(1, board.Count);

        hub.ToolCompleted("revit_set_parameter", "Claude Code", true, null, 5, new { elements = new[] { new { elementId = 12L, name = "B" } } }, Context());
        hub.ToolCompleted("revit_export_schedule", "Claude Code", true, null, 5, new { elements = new[] { new { elementId = 13L, name = "C" } } }, Context());
        hub.ToolCompleted("revit_find_elements", "Claude Code", false, "tool_execution_failed", 5, new { elements = new[] { new { elementId = 14L, name = "D" } } }, Context());
        hub.ToolCompleted("revit_list_sheets", "Claude Code", true, null, 5, new { returned = 3 }, Context());
        Assert.Equal(1, board.Count);

        using var off = new VillageStateHub(new VillageOptions { FlyersEnabled = false }, clock: () => _now, flyers: board);
        off.ToolCompleted("revit_find_elements", "Claude Code", true, null, 5, new { elements = new[] { new { elementId = 15L, name = "E" } } }, Context());
        Assert.Equal(1, board.Count);
    }

    [Fact]
    public void Options_ReadFlyerSettingsAndClampThem()
    {
        var user = VillageOptions.ParseConfig(@"{ ""village"": { ""flyersEnabled"": false, ""flyerMaxItems"": 5, ""maxFlyers"": 9999, ""flyerArchiveDays"": 7, ""showInRevit"": ""false"" } }");
        var o = VillageOptions.FromConfig(user, null);
        Assert.False(o.FlyersEnabled);
        Assert.Equal(50, o.FlyerMaxItems);
        Assert.Equal(200, o.MaxFlyers);
        Assert.Equal(7, o.FlyerArchiveDays);
        Assert.False(o.ShowInRevit);

        var d = VillageOptions.FromConfig(null, null);
        Assert.True(d.FlyersEnabled);
        Assert.True(d.ShowInRevit);
        Assert.Equal(2000, d.FlyerMaxItems);
    }
}
