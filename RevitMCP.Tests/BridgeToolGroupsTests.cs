using System.Text.RegularExpressions;
using RevitMCP.Bridge;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>
/// #66: the tool grouping behind the bridge profiles. The bridge is not referenced by the test
/// project, so tool names are read from RevitMcpTools.cs (the [McpServerTool(Name = ...)] attributes).
/// </summary>
public class BridgeToolGroupsTests
{
    private static readonly Lazy<List<string>> AllTools = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitMCP.slnx"))) dir = dir.Parent;
        var source = File.ReadAllText(Path.Combine(dir!.FullName, "RevitMCP.Bridge", "RevitMcpTools.cs")) +
                     File.ReadAllText(Path.Combine(dir.FullName, "RevitMCP.Bridge", "ToolDiscoveryTools.cs"));
        return Regex.Matches(source, @"McpServerTool\(Name = ""([^""]+)""").Select(m => m.Groups[1].Value).ToList();
    });

    [Fact]
    public void EveryTool_HasAKnownGroup()
    {
        var missing = AllTools.Value.Where(t => BridgeToolGroups.GroupOf(t) is not { } g || !BridgeToolGroups.IsKnownGroup(g)).ToList();
        Assert.True(missing.Count == 0, "Tools without a group: " + string.Join(", ", missing));
    }

    [Fact]
    public void AllGroupsTogether_CoverEveryTool_SoTheFullProfileLosesNothing()
    {
        var all = BridgeToolGroups.SelectByGroups(AllTools.Value, ["all"], out var unknown);
        Assert.Empty(unknown);
        Assert.Equal(AllTools.Value.OrderBy(x => x), all.OrderBy(x => x));
    }

    [Fact]
    public void CoreProfile_IsSmall_AndHasTheGraphFirstWorkflow()
    {
        var core = BridgeToolGroups.SelectByGroups(AllTools.Value, [], out _);
        foreach (var required in new[]
                 {
                     "revit_get_connection_status", "revit_get_selected_elements", "revit_get_elements_info",
                     "revit_get_element_parameters", "revit_graph_route", "revit_graph_status", "revit_graph_summary",
                     "revit_graph_query", "revit_graph_build",
                     "revit_tools_search", "revit_tools_describe", "revit_tools_call", "revit_tools_load"
                 })
            Assert.Contains(required, core);
        Assert.True(core.Count <= 25, $"core profile has {core.Count} tools");
        Assert.True(core.Count * 8 < AllTools.Value.Count, "core should be a small fraction of the catalog");
    }

    [Fact]
    public void ExtraGroups_AreAdded_AndUnknownOnesReported()
    {
        var withElectrical = BridgeToolGroups.SelectByGroups(AllTools.Value, ["electrical", "nope"], out var unknown);
        Assert.Contains("revit_get_circuit_info", withElectrical);
        Assert.Contains("revit_graph_query", withElectrical);
        Assert.DoesNotContain("revit_place_tags", withElectrical);
        Assert.Equal(["nope"], unknown);
    }

    [Theory]
    [InlineData("revit_export_voltage_drop_input_to_excel", "electrical")]   // "voltage" contains "tag"
    [InlineData("revit_place_tags", "tags")]
    [InlineData("revit_place_in_room", "devices")]
    [InlineData("revit_check_fire_alarm", "devices")]
    [InlineData("revit_run_fire_alarm_circuit_preset", "electrical")]
    [InlineData("revit_place_from_cad", "cad")]
    [InlineData("revit_create_clash_review_view", "coordination")]
    [InlineData("revit_place_views_on_sheets", "views")]
    [InlineData("convert_ifc_spaces_to_rooms", "ifc")]
    [InlineData("excel_read_range", "office")]
    public void GroupOf_ResolvesAmbiguousNames(string tool, string expected) =>
        Assert.Equal(expected, BridgeToolGroups.GroupOf(tool));
}
