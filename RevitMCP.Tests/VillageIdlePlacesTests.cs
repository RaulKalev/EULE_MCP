using System.Text.Json.Nodes;
using RevitMCP.Village;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>The overlook (work finished) and the park (idle a while), plus excluded categories.</summary>
public class VillageIdlePlacesTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock
    {
        public DateTimeOffset Now = Start;
        public DateTimeOffset Get() => Now;
        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
    }

    private static VillageEvent Completed(string area = VillageAreas.Elements) => new()
    {
        EventType = VillageEventTypes.ToolCompleted,
        ToolName = "revit_count_elements",
        ClientName = "Claude Code",
        Area = area,
        Activity = VillageActivities.Search,
        Success = true
    };

    private static (VillageAggregator Aggregator, Clock Clock) Build(VillageOptions? options = null)
    {
        var clock = new Clock();
        return (new VillageAggregator(options ?? VillageOptions.Default, clock.Get), clock);
    }

    // ─── Overlook and park ──────────────────────────────────────────────────

    [Fact]
    public void BothPlacesExistAndBelongToNoArea()
    {
        var overlook = VillageLayout.Default.Single(b => b.Id == VillageLayout.Overlook);
        var park = VillageLayout.Default.Single(b => b.Id == VillageLayout.Park);

        Assert.Empty(overlook.Areas);
        Assert.Empty(park.Areas);
        Assert.Equal("overlook", overlook.Sprite);
        Assert.Equal("park", park.Sprite);

        // No tool can route to either: they are only reachable by going idle.
        foreach (var area in VillageAreas.All)
        {
            var building = VillageLayout.BuildingFor(area);
            Assert.NotEqual(VillageLayout.Overlook, building);
            Assert.NotEqual(VillageLayout.Park, building);
        }
    }

    [Fact]
    public void TheOverlookSitsOnTheRightEdge_AndTheParkInTheMiddle()
    {
        double X(VillageBuilding b) => (b.TileX - b.TileY) / 2.0;
        var overlook = VillageLayout.Default.Single(b => b.Id == VillageLayout.Overlook);
        var park = VillageLayout.Default.Single(b => b.Id == VillageLayout.Park);
        var others = VillageLayout.Default.Where(b => b.Id != VillageLayout.Overlook).ToList();

        Assert.True(X(overlook) > others.Max(X), "the overlook must be the rightmost landmark");

        var left = others.Min(X);
        var right = others.Max(X);
        Assert.InRange(X(park), left + (right - left) * 0.25, left + (right - left) * 0.75);
    }

    [Fact]
    public void AfterFinishingWorkTheCharacterWalksToTheOverlook()
    {
        var (aggregator, clock) = Build();
        aggregator.Process(Completed());
        Assert.Equal(VillageLayout.Houses, aggregator.Agents.Values.Single().Building);

        clock.Advance(20);            // past IdleAfterMs, well short of parkAfterSeconds
        aggregator.Tick();

        var agent = aggregator.Agents.Values.Single();
        Assert.Equal(VillageLayout.Overlook, agent.Building);
        Assert.Equal(VillageAreas.Unknown, agent.Area);
    }

    [Fact]
    public void StillIdleAfterTheParkDelay_TheCharacterMovesOnToThePark()
    {
        var (aggregator, clock) = Build(new VillageOptions { ParkAfterSeconds = 120, AgentIdleSeconds = 3600 });
        aggregator.Process(Completed());

        clock.Advance(20);
        aggregator.Tick();
        Assert.Equal(VillageLayout.Overlook, aggregator.Agents.Values.Single().Building);

        clock.Advance(130);
        aggregator.Tick();
        Assert.Equal(VillageLayout.Park, aggregator.Agents.Values.Single().Building);
    }

    [Fact]
    public void OnceInTheParkTheCharacterStaysThere()
    {
        var (aggregator, clock) = Build(new VillageOptions { ParkAfterSeconds = 60, AgentIdleSeconds = 3600 });
        aggregator.Process(Completed());
        clock.Advance(90);
        aggregator.Tick();
        Assert.Equal(VillageLayout.Park, aggregator.Agents.Values.Single().Building);

        // It must not bounce back to the overlook on the next tick.
        clock.Advance(5);
        aggregator.Tick();
        Assert.Equal(VillageLayout.Park, aggregator.Agents.Values.Single().Building);
    }

    [Fact]
    public void RepeatedTicksEmitOneMoveStepPerPlace()
    {
        var (aggregator, clock) = Build(new VillageOptions { ParkAfterSeconds = 60, AgentIdleSeconds = 3600 });
        var moves = new List<VillageStoryStep>();
        aggregator.StepEmitted += s => { if (s.Kind == VillageStepKinds.Move) moves.Add(s); };

        aggregator.Process(Completed());
        clock.Advance(20);
        for (var i = 0; i < 5; i++) aggregator.Tick();      // many ticks, still one walk
        clock.Advance(60);
        for (var i = 0; i < 5; i++) aggregator.Tick();

        var idleMoves = moves.Where(m => m.Building is VillageLayout.Overlook or VillageLayout.Park).ToList();
        Assert.Equal(2, idleMoves.Count);
        Assert.Equal("Heads to the overlook", idleMoves[0].Label);
        Assert.Equal("Heads to the park", idleMoves[1].Label);
    }

    [Fact]
    public void NewWorkPullsTheCharacterBackOutOfThePark()
    {
        var (aggregator, clock) = Build(new VillageOptions { ParkAfterSeconds = 30, AgentIdleSeconds = 3600 });
        aggregator.Process(Completed());
        clock.Advance(60);
        aggregator.Tick();
        Assert.Equal(VillageLayout.Park, aggregator.Agents.Values.Single().Building);

        aggregator.Process(Completed(VillageAreas.Sheets));
        Assert.Equal(VillageLayout.Archive, aggregator.Agents.Values.Single().Building);
    }

    [Fact]
    public void AToolStillRunningKeepsTheCharacterAtItsWork()
    {
        var (aggregator, clock) = Build();
        aggregator.Process(new VillageEvent
        {
            EventType = VillageEventTypes.ToolStarted,
            ToolName = "revit_count_elements",
            ClientName = "Claude Code",
            Area = VillageAreas.Elements,
            Activity = VillageActivities.Search
        });

        clock.Advance(20);
        aggregator.Tick();

        Assert.Equal(VillageLayout.Houses, aggregator.Agents.Values.Single().Building);
    }

    [Fact]
    public void TheBuildingCapLeavesRoomForEveryLandmark()
    {
        Assert.True(VillageOptions.Default.MaxBuildings >= VillageLayout.Default.Count,
            "maxBuildings must not silently drop a landmark from the viewer");
    }

    // ─── Excluded categories ────────────────────────────────────────────────

    private static List<VillageThemeEvidence> Categories(params (string Name, long Count)[] rows) =>
        rows.Select(r => new VillageThemeEvidence { Name = r.Name, Count = r.Count }).ToList();

    [Fact]
    public void DefinitionAndViewCategoriesNeverGetAWarehouse()
    {
        var yard = VillageWarehouseYard.Plan(Categories(
            ("Materials", 5000), ("Legend Components", 400), ("Material Assets", 900),
            ("RVT Links", 12), ("Cameras", 40), ("Fire Alarm Devices", 120)));

        Assert.Single(yard);
        Assert.Equal("Fire Alarm Devices", yard[0].Category);
    }

    [Fact]
    public void ExclusionIgnoresCaseAndSurroundingSpace()
    {
        var yard = VillageWarehouseYard.Plan(Categories(("  materials  ", 500), ("rvt links", 30), ("Walls", 10)));

        Assert.Single(yard);
        Assert.Equal("Walls", yard[0].Category);
    }

    [Fact]
    public void ExcludedCategoriesDoNotCountTowardsShares()
    {
        // Materials would otherwise swamp the denominator and make every real share look tiny.
        var yard = VillageWarehouseYard.Plan(Categories(("Materials", 9000), ("A", 300), ("B", 100)));

        Assert.Equal(new[] { "A", "B" }, yard.Select(w => w.Category));
        Assert.Equal(0.75, yard[0].Share);
        Assert.Equal(0.25, yard[1].Share);
    }

    [Fact]
    public void AConfiguredListReplacesTheDefaults()
    {
        var yard = VillageWarehouseYard.Plan(
            Categories(("Materials", 500), ("Walls", 400)),
            excludedCategories: new[] { "Walls" });

        Assert.Single(yard);
        Assert.Equal("Materials", yard[0].Category);
    }

    [Fact]
    public void AnEmptyConfiguredListExcludesNothing()
    {
        var yard = VillageWarehouseYard.Plan(Categories(("Materials", 500)), excludedCategories: Array.Empty<string>());
        Assert.Single(yard);
    }

    [Fact]
    public void TheExclusionListComesFromTheConfig()
    {
        var user = VillageOptions.ParseConfig("{\"village\":{\"warehouseExcludeCategories\":[\"Walls\",\" Ducts \"]}}");
        var options = VillageOptions.FromConfig(user, null);

        Assert.Equal(new[] { "Walls", "Ducts" }, options.WarehouseExcludeCategories);
    }

    [Fact]
    public void WithoutConfigTheDefaultExclusionsApply()
    {
        var options = VillageOptions.FromConfig(null, null);
        Assert.Equal(VillageWarehouseYard.DefaultExcludedCategories, options.WarehouseExcludeCategories);
    }

    [Fact]
    public void ParkDelayIsClampedIntoRange()
    {
        JsonObject? Cfg(string v) => VillageOptions.ParseConfig("{\"village\":{\"parkAfterSeconds\":" + v + "}}");

        Assert.Equal(15, VillageOptions.FromConfig(Cfg("1"), null).ParkAfterSeconds);
        Assert.Equal(3600, VillageOptions.FromConfig(Cfg("999999"), null).ParkAfterSeconds);
        Assert.Equal(45, VillageOptions.FromConfig(Cfg("45"), null).ParkAfterSeconds);
        Assert.Equal(120, VillageOptions.Default.ParkAfterSeconds);
    }
}
