using Newtonsoft.Json.Linq;
using RevitMCP.Village;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>Contents grouping rules and the per-warehouse activity the viewer tints and lists.</summary>
public class VillageInformativeTests
{
    [Theory]
    [InlineData("E-101", "E")]
    [InlineData("ATS201", "ATS")]
    [InlineData("a-01", "A")]
    [InlineData("101", "(numeric)")]
    [InlineData("", "")]
    public void SheetPrefix_IsTheLettersBeforeTheNumber(string number, string expected) =>
        Assert.Equal(expected, VillageContentGrouping.SheetPrefix(number));

    [Theory]
    [InlineData("Smoke Detector: ATS-O", "Smoke Detector")]
    [InlineData("  Basic Wall : Generic 200 ", "Basic Wall")]
    [InlineData("No colon", "No colon")]
    public void FamilyOf_StopsAtTheColon(string name, string expected) =>
        Assert.Equal(expected, VillageContentGrouping.FamilyOf(name));

    [Theory]
    [InlineData("Fire alarm devices list", "Fire")]
    [InlineData("Lighting-schedule", "Lighting")]
    [InlineData("Panel", "Panel")]
    public void FirstWord_GroupsSchedulesByTheirOpeningWord(string name, string expected) =>
        Assert.Equal(expected, VillageContentGrouping.FirstWord(name));

    [Theory]
    [InlineData(0, "No circuits")]
    [InlineData(8, "1–10 circuits")]
    [InlineData(30, "11–30 circuits")]
    [InlineData(61, "Over 60 circuits")]
    public void PanelLoad_BucketsByCircuitCount(long circuits, string expected) =>
        Assert.Equal(expected, VillageContentGrouping.PanelLoad(circuits));

    [Fact]
    public void LandmarkContents_CarryTheirOwnLabelsAndNoun()
    {
        var contents = VillageWarehouseContents.Build(
            new[] { new VillageContentRow { Type = "E", Count = 4 } }, "sheets", "Number prefix");

        Assert.Equal("sheets", contents.Noun);
        Assert.Equal("Number prefix", contents.Labels.Type);
        Assert.Equal("Level", contents.Labels.Level);
    }

    // ─── Per-warehouse activity ─────────────────────────────────────────────

    private const string FireAlarm = "wh_fire_alarm_devices";

    private static VillageAggregator Aggregator()
    {
        var aggregator = new VillageAggregator();
        aggregator.SetWarehouses(VillageWarehouseYard.Plan(new List<VillageThemeEvidence>
        {
            new() { Name = "Fire Alarm Devices", Count = 400 },
            new() { Name = "Data Devices", Count = 150 }
        }));
        return aggregator;
    }

    private static VillageEvent Event(string type, string tool, string activity, string? warehouse, long? affected = null) => new()
    {
        EventType = type, ToolName = tool, ClientName = "Claude Code", Area = VillageAreas.FireAlarm,
        Activity = activity, Success = type == VillageEventTypes.ToolCompleted, Warehouse = warehouse, AffectedCount = affected
    };

    [Fact]
    public void ActivityIsCountedPerWarehouse()
    {
        var aggregator = Aggregator();
        aggregator.Process(Event(VillageEventTypes.ToolCompleted, "revit_find_elements_by_parameter", VillageActivities.Search, FireAlarm));
        aggregator.Process(Event(VillageEventTypes.ToolCompleted, "revit_set_parameter", VillageActivities.Modify, FireAlarm, 24));
        aggregator.Process(Event(VillageEventTypes.ToolFailed, "revit_set_parameter", VillageActivities.Modify, FireAlarm));
        aggregator.Process(Event(VillageEventTypes.ToolCompleted, "revit_get_elements_info", VillageActivities.Inspect, "wh_data_devices"));

        var activity = aggregator.Snapshot().WarehouseActivity;
        var fire = Assert.Single(activity, a => a.Warehouse == FireAlarm);
        Assert.Equal((1L, 1L, 1L, 24L), (fire.Reads, fire.Writes, fire.Failures, fire.Affected));
        Assert.Equal(new[] { "revit_find_elements_by_parameter", "revit_set_parameter" }, fire.Tools);
        Assert.Equal("revit_set_parameter", fire.LastTool);
        Assert.Equal(1, Assert.Single(activity, a => a.Warehouse == "wh_data_devices").Reads);
    }

    [Fact]
    public void ToolsWithoutAWarehouse_LeaveNoActivityBehind()
    {
        var aggregator = Aggregator();
        aggregator.Process(Event(VillageEventTypes.ToolCompleted, "revit_list_sheets", VillageActivities.Inspect, null));
        Assert.Empty(aggregator.Snapshot().WarehouseActivity);
    }

    [Fact]
    public void ActivityForAWarehouseThatNoLongerExists_IsNotCounted()
    {
        var aggregator = Aggregator();
        aggregator.Process(Event(VillageEventTypes.ToolCompleted, "revit_get_elements_info", VillageActivities.Inspect, "wh_gone"));
        Assert.Empty(aggregator.Snapshot().WarehouseActivity);
    }

    [Fact]
    public void ActivitySerializesOnlyCountsAndToolNames()
    {
        var aggregator = Aggregator();
        aggregator.Process(Event(VillageEventTypes.ToolCompleted, "revit_set_parameter", VillageActivities.Modify, FireAlarm, 3));
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(aggregator.Snapshot());
        var activity = JObject.Parse(json)["warehouse_activity"]![0]!;
        Assert.Equal(
            new[] { "affected", "exports", "failures", "last_at", "last_tool", "reads", "tools", "warehouse", "writes" },
            activity.Children<JProperty>().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }
}
