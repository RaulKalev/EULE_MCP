using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCP.Village;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>
/// Walking to the category warehouse: a tool that names a Revit category sends the character to
/// that category's warehouse instead of the generic area landmark.
/// </summary>
public class VillageWarehouseRoutingTests
{
    private const string FireAlarm = "wh_fire_alarm_devices";
    private const string DataDevices = "wh_data_devices";

    private static List<VillageThemeEvidence> Yard() => new()
    {
        new VillageThemeEvidence { Name = "Fire Alarm Devices", Count = 400 },
        new VillageThemeEvidence { Name = "Data Devices", Count = 150 },
        new VillageThemeEvidence { Name = "Walls", Count = 900 }
    };

    private static VillageAggregator Aggregator()
    {
        var aggregator = new VillageAggregator();
        aggregator.SetWarehouses(VillageWarehouseYard.Plan(Yard()));
        return aggregator;
    }

    private static VillageEvent Completed(string tool, string area, string? warehouse, string activity = VillageActivities.Search) => new()
    {
        EventType = VillageEventTypes.ToolCompleted,
        ToolName = tool,
        ClientName = "Claude Code",
        Area = area,
        Activity = activity,
        Success = true,
        Warehouse = warehouse
    };

    // ─── Argument extraction ────────────────────────────────────────────────

    [Fact]
    public void CategoryIsReadOnlyFromAllowListedKeys()
    {
        Assert.Equal("Fire Alarm Devices", VillageCategoryArgument.TryExtract(
            new Dictionary<string, object?> { ["category"] = "Fire Alarm Devices" }));
        Assert.Equal("Data Devices", VillageCategoryArgument.TryExtract(
            new Dictionary<string, object?> { ["categoryName"] = " Data Devices " }));

        // Anything outside the allow-list is invisible to the village, values included.
        Assert.Null(VillageCategoryArgument.TryExtract(new Dictionary<string, object?>
        {
            ["parameterName"] = "Ahela nr",
            ["value"] = "2",
            ["filePath"] = @"C:\secret\model.rvt",
            ["elementIds"] = new[] { 123456, 234567 }
        }));
    }

    [Fact]
    public void MissingEmptyAndOverlongCategoriesAreIgnored()
    {
        Assert.Null(VillageCategoryArgument.TryExtract(null));
        Assert.Null(VillageCategoryArgument.TryExtract(new Dictionary<string, object?>()));
        Assert.Null(VillageCategoryArgument.TryExtract(new Dictionary<string, object?> { ["category"] = "   " }));
        Assert.Null(VillageCategoryArgument.TryExtract(new Dictionary<string, object?> { ["category"] = null }));
        Assert.Null(VillageCategoryArgument.TryExtract(new Dictionary<string, object?>
        {
            ["category"] = new string('x', VillageCategoryArgument.MaxLength + 1)
        }));
    }

    [Fact]
    public void ASingleCategoryInAListCounts_ButSeveralDoNot()
    {
        Assert.Equal("Data Devices", VillageCategoryArgument.TryExtract(
            new Dictionary<string, object?> { ["categories"] = new[] { "Data Devices" } }));
        Assert.Equal("Data Devices", VillageCategoryArgument.TryExtract(
            new Dictionary<string, object?> { ["categories"] = new JArray("Data Devices", "data devices") }));

        // A scan across several categories has no single warehouse to walk to.
        Assert.Null(VillageCategoryArgument.TryExtract(
            new Dictionary<string, object?> { ["categories"] = new[] { "Data Devices", "Fire Alarm Devices" } }));
    }

    // ─── Resolution against the live yard ───────────────────────────────────

    [Fact]
    public void OnlyCategoriesWithAWarehouseResolve()
    {
        using var hub = new VillageStateHub();
        hub.Aggregator.SetWarehouses(VillageWarehouseYard.Plan(Yard()));

        // The hub's lookup is filled when the graph is read; simulate that through the factory.
        var factory = new VillageEventFactory(
            warehouseResolver: c => VillageWarehouseYard.Plan(Yard())
                .FirstOrDefault(w => string.Equals(w.Category, c, StringComparison.OrdinalIgnoreCase))?.Id);

        var known = factory.ToolCompleted("revit_count_elements", "Claude Code", true, null, 5, null, null,
            new Dictionary<string, object?> { ["category"] = "fire alarm devices" });
        Assert.Equal(FireAlarm, known.Warehouse);

        var unknown = factory.ToolCompleted("revit_count_elements", "Claude Code", true, null, 5, null, null,
            new Dictionary<string, object?> { ["category"] = "Ceilings" });
        Assert.Null(unknown.Warehouse);
    }

    [Fact]
    public void WithoutAResolverNoEventEverRoutesToAWarehouse()
    {
        var factory = new VillageEventFactory();
        var e = factory.ToolCompleted("revit_count_elements", "Claude Code", true, null, 5, null, null,
            new Dictionary<string, object?> { ["category"] = "Fire Alarm Devices" });
        Assert.Null(e.Warehouse);
    }

    [Fact]
    public void AThrowingResolverNeverBreaksTheEvent()
    {
        var factory = new VillageEventFactory(warehouseResolver: _ => throw new InvalidOperationException("boom"));
        var e = factory.ToolCompleted("revit_count_elements", "Claude Code", true, null, 5, null, null,
            new Dictionary<string, object?> { ["category"] = "Fire Alarm Devices" });
        Assert.Null(e.Warehouse);
        Assert.Equal("revit_count_elements", e.ToolName);
    }

    [Fact]
    public void HubResolvesOnlyWhatTheYardPublished()
    {
        using var hub = new VillageStateHub();
        Assert.Null(hub.ResolveWarehouse("Fire Alarm Devices"));   // no graph yet, so no yard
        Assert.Null(hub.ResolveWarehouse(null));
        Assert.Null(hub.ResolveWarehouse("   "));
    }

    // ─── Routing ────────────────────────────────────────────────────────────

    [Fact]
    public void QueryingACategorySendsTheCharacterToItsWarehouse()
    {
        var aggregator = Aggregator();
        aggregator.Process(Completed("revit_find_elements_by_parameter", VillageAreas.Elements, FireAlarm));

        var agent = aggregator.Agents.Values.Single();
        Assert.Equal(FireAlarm, agent.Building);
        Assert.Equal(FireAlarm, agent.Warehouse);
        Assert.Equal(VillageAreas.Elements, agent.Area);
    }

    [Fact]
    public void WithoutACategoryTheCharacterStillGoesToTheAreaLandmark()
    {
        var aggregator = Aggregator();
        aggregator.Process(Completed("revit_get_elements_info", VillageAreas.Elements, null));

        Assert.Equal(VillageLayout.Houses, aggregator.Agents.Values.Single().Building);
    }

    [Fact]
    public void AnUnknownWarehouseIdFallsBackToTheLandmark()
    {
        var aggregator = Aggregator();
        aggregator.Process(Completed("revit_count_elements", VillageAreas.Elements, "wh_does_not_exist"));

        Assert.Equal(VillageLayout.Houses, aggregator.Agents.Values.Single().Building);
    }

    [Fact]
    public void OnlyElementAreasRedirect_DrawingsAndFilesKeepTheirLandmark()
    {
        var aggregator = Aggregator();
        aggregator.Process(Completed("revit_list_sheets", VillageAreas.Sheets, FireAlarm));
        Assert.Equal(VillageLayout.Archive, aggregator.Agents.Values.Single().Building);

        aggregator.Process(Completed("revit_graph_query", VillageAreas.Graph, FireAlarm));
        Assert.Equal(VillageLayout.TownHall, aggregator.Agents.Values.Single().Building);
    }

    [Fact]
    public void SwitchingCategoryInsideOneAreaWalksBetweenWarehouses()
    {
        var aggregator = Aggregator();
        var moves = new List<VillageStoryStep>();
        aggregator.StepEmitted += s => { if (s.Kind == VillageStepKinds.Move) moves.Add(s); };

        aggregator.Process(Completed("revit_count_elements", VillageAreas.Elements, FireAlarm));
        aggregator.Process(Completed("revit_count_elements", VillageAreas.Elements, DataDevices));

        Assert.Equal(DataDevices, aggregator.Agents.Values.Single().Building);
        Assert.Equal(2, moves.Count);
        Assert.Equal("Heads to Fire Alarm Devices", moves[0].Label);
        Assert.Equal("Heads to Data Devices", moves[1].Label);
    }

    [Fact]
    public void AFollowUpToolWithNoCategoryLeavesTheCharacterAtTheWarehouse()
    {
        // Otherwise a "get parameters for these ids" call would walk it back to Houses every time.
        var aggregator = Aggregator();
        aggregator.Process(Completed("revit_count_elements", VillageAreas.Elements, FireAlarm));
        aggregator.Process(Completed("revit_get_element_parameters", VillageAreas.Elements, null));

        var agent = aggregator.Agents.Values.Single();
        Assert.Equal(FireAlarm, agent.Building);
        Assert.Equal(FireAlarm, agent.Warehouse);
    }

    [Fact]
    public void StepsAtAWarehouseAreNamedAfterTheCategory()
    {
        var aggregator = Aggregator();
        var steps = new List<VillageStoryStep>();
        aggregator.StepEmitted += s => { if (s.Kind == VillageStepKinds.Work) steps.Add(s); };

        aggregator.Process(Completed("revit_find_elements_by_parameter", VillageAreas.Elements, FireAlarm));
        aggregator.Flush();

        var step = steps.Single();
        Assert.Equal(FireAlarm, step.Warehouse);
        Assert.Equal(FireAlarm, step.Building);
        Assert.Equal("Searched Fire Alarm Devices", step.Label);
    }

    [Fact]
    public void WorkAtDifferentWarehousesNeverMergesIntoOneStep()
    {
        var aggregator = Aggregator();
        var steps = new List<VillageStoryStep>();
        aggregator.StepEmitted += s => { if (s.Kind == VillageStepKinds.Work) steps.Add(s); };

        aggregator.Process(Completed("revit_count_elements", VillageAreas.Elements, FireAlarm));
        aggregator.Process(Completed("revit_count_elements", VillageAreas.Elements, DataDevices));
        aggregator.Flush();

        Assert.Equal(2, steps.Count);
        Assert.Equal(new[] { FireAlarm, DataDevices }, steps.Select(s => s.Warehouse));
    }

    [Fact]
    public void AreaCountersStayKeyedByArea_NotByWarehouse()
    {
        var aggregator = Aggregator();
        aggregator.Process(Completed("revit_count_elements", VillageAreas.Elements, FireAlarm));

        Assert.Equal(1, aggregator.Areas[VillageAreas.Elements].Reads);
    }

    [Fact]
    public void RebuildingTheYardWalksCharactersOffWarehousesThatVanished()
    {
        var aggregator = Aggregator();
        aggregator.Process(Completed("revit_count_elements", VillageAreas.Elements, FireAlarm));
        Assert.Equal(FireAlarm, aggregator.Agents.Values.Single().Building);

        // The model no longer has fire alarm devices.
        aggregator.SetWarehouses(VillageWarehouseYard.Plan(new List<VillageThemeEvidence>
        {
            new() { Name = "Data Devices", Count = 150 }
        }));

        var agent = aggregator.Agents.Values.Single();
        Assert.Null(agent.Warehouse);
        Assert.Equal(VillageLayout.Houses, agent.Building);
    }

    [Fact]
    public void TheSnapshotPublishesTheWarehouseForAgentsAndSteps()
    {
        var aggregator = Aggregator();
        aggregator.Process(Completed("revit_count_elements", VillageAreas.Elements, FireAlarm));
        aggregator.Flush();

        var json = JsonConvert.SerializeObject(aggregator.Snapshot());
        var snapshot = JObject.Parse(json);

        Assert.Equal(FireAlarm, snapshot["agents"]![0]!["warehouse"]!.Value<string>());
        Assert.Contains(snapshot["recent_steps"]!, s => s["warehouse"]?.Value<string>() == FireAlarm);
    }

    [Fact]
    public void TheEventStillCarriesNoArgumentsOrValues()
    {
        // The warehouse id comes from the graph-derived yard, never from the request text.
        var factory = new VillageEventFactory(warehouseResolver: _ => FireAlarm);
        var e = factory.ToolCompleted("revit_find_elements_by_parameter", "Claude Code", true, null, 12, null, null,
            new Dictionary<string, object?>
            {
                ["category"] = "Fire Alarm Devices",
                ["parameterName"] = "Ahela nr",
                ["value"] = "2",
                ["filePath"] = @"C:\Users\someone\model.rvt"
            });

        var json = VillageEventSerializer.Serialize(e);
        Assert.Contains(FireAlarm, json);
        Assert.DoesNotContain("Ahela nr", json);
        Assert.DoesNotContain("someone", json);
        Assert.DoesNotContain("model.rvt", json);
        Assert.DoesNotContain("parameterName", json);
    }
}

/// <summary>The isometric layout has to keep landmarks readable, not just distinct on the grid.</summary>
public class VillageLayoutGeometryTests
{
    // Screen position of tile (x, y) in TILE units: x_screen = (x - y) / 2, y_screen = (x + y) / 4.
    private static double ScreenX(VillageBuilding b) => (b.TileX - b.TileY) / 2.0;
    private static double ScreenY(VillageBuilding b) => (b.TileX + b.TileY) / 4.0;

    [Fact]
    public void NoTwoLandmarksSitInTheSameIsometricColumnCloseTogether()
    {
        // Equal screen x means the nearer building is drawn directly on top of the further one.
        // That is what hid the Workshop behind the Sign workshop.
        foreach (var a in VillageLayout.Default)
        foreach (var b in VillageLayout.Default)
        {
            if (ReferenceEquals(a, b) || string.CompareOrdinal(a.Id, b.Id) >= 0) continue;
            var dx = Math.Abs(ScreenX(a) - ScreenX(b));
            var dy = Math.Abs(ScreenY(a) - ScreenY(b));
            Assert.False(dx < 0.25 && dy < 1.5,
                $"{a.Id} and {b.Id} overlap: dx={dx:0.00} dy={dy:0.00} tiles");
        }
    }

    [Fact]
    public void TheWorkshopAndSignWorkshopAreClearOfEachOther()
    {
        var workshop = VillageLayout.Default.Single(b => b.Id == VillageLayout.Workshop);
        var sign = VillageLayout.Default.Single(b => b.Id == VillageLayout.SignShop);

        Assert.True(Math.Abs(ScreenX(workshop) - ScreenX(sign)) >= 0.9, "they must not share a screen column");
        Assert.True(Math.Abs(ScreenY(workshop) - ScreenY(sign)) >= 1.4, "and must be a building's height apart");
    }

    [Fact]
    public void EveryLandmarkStaysInsideTheBaseGrid()
    {
        Assert.All(VillageLayout.Default, b =>
        {
            Assert.InRange(b.TileX, 0, 11);
            Assert.InRange(b.TileY, 0, 11);
        });
    }

    [Fact]
    public void NoTwoLandmarksShareATile()
    {
        Assert.Equal(VillageLayout.Default.Count, VillageLayout.Default.Select(b => (b.TileX, b.TileY)).Distinct().Count());
    }
}
