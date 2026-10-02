using RevitMCP.Addin.Graph;
using Xunit;

namespace RevitMCP.Tests;

public class GraphRoutingTests
{
    private static GraphRoutingState Fresh() => new()
    {
        Exists = true,
        NodesByKind = new(StringComparer.OrdinalIgnoreCase) { ["element"] = 550, ["space"] = 44, ["panel"] = 3 },
        Categories = ["Fire Alarm Devices", "Lighting Fixtures", "Rooms"],
        Levels = ["Esimene korrus", "Teine korrus"]
    };

    [Theory]
    [InlineData("which devices are in room 1.12", "room")]
    [InlineData("seadmed ruumis 08", "room")]
    [InlineData("everything fed by panel JK-1", "panel")]
    [InlineData("ATS silmuse seadmed", "panel")]
    [InlineData("what is on the second floor", "level")]
    [InlineData("the selected element", "selection")]
    [InlineData("doors in the linked AR model", "linked")]
    [InlineData("count things", "general")]
    public void Classify_UsesEnglishAndEstonianKeywords(string intent, string expected) =>
        Assert.Equal(expected, GraphRouting.Classify(intent));

    [Fact]
    public void RoomPlan_FindsSpace_ThenLocatedIn_ThenLiveRead()
    {
        var plan = GraphRouting.Plan("which devices are in room 1.12?", Fresh());
        Assert.Equal("room", plan.Route);
        Assert.Equal("1.12", plan.NameHint);
        Assert.Equal(new[] { "revit_graph_query", "revit_graph_query", "revit_get_elements_info" }, plan.Steps.Select(s => s.Tool));
        Assert.Equal("space", plan.Steps[0].Args["kind"]);
        Assert.Equal("located_in", plan.Steps[1].Args["rel"]);
        Assert.True(plan.GraphUsable);
    }

    [Fact]
    public void CategoryAndLevel_AreDetectedFromTheGraph()
    {
        var plan = GraphRouting.Plan("list fire alarm devices on Teine korrus", Fresh());
        Assert.Equal("category", plan.Route);
        Assert.Equal("Fire Alarm Devices", plan.Category);
        Assert.Equal("Teine korrus", plan.Level);
        var find = plan.Steps.First(s => s.Tool == "revit_graph_query");
        Assert.Equal("Fire Alarm Devices", find.Args["category"]);
        Assert.Equal("Teine korrus", find.Args["level"]);
    }

    [Fact]
    public void PanelPlan_UsesFedBySubtree_AndWarnsWithoutPanels()
    {
        var state = Fresh();
        state.NodesByKind["panel"] = 0;
        var plan = GraphRouting.Plan("everything fed by panel \"JK-1\"", state);
        Assert.Equal("JK-1", plan.NameHint);
        Assert.Contains(plan.Steps, s => s.Args.TryGetValue("rel", out var r) && (string?)r == "fed_by");
        Assert.Contains(plan.Notes, n => n.Contains("no panel nodes"));
    }

    [Fact]
    public void MissingGraph_StartsWithBuild_AndOffersLiveFallback()
    {
        var plan = GraphRouting.Plan("devices in room 08", new GraphRoutingState());
        Assert.False(plan.GraphUsable);
        Assert.Equal("revit_graph_build", plan.Steps[0].Tool);
        Assert.Contains(plan.Notes, n => n.Contains("Live fallback"));
    }

    [Fact]
    public void StaleGraph_RebuildsFirst_AndTreatsIdsAsHints()
    {
        var state = Fresh();
        state.Stale = true;
        state.StaleReason = "element count changed";
        var plan = GraphRouting.Plan("devices in room 08", state);
        Assert.Equal("revit_graph_build", plan.Steps[0].Tool);
        Assert.Contains(plan.Notes, n => n.Contains("stale") && n.Contains("element count changed"));
        Assert.Equal("room", plan.Route);
    }

    [Fact]
    public void BroadQueryHint_OnlyForBroadToolsAboveTheThreshold()
    {
        var state = Fresh();
        Assert.Null(GraphRouting.BroadQueryHint("revit_get_elements_info", "Lines", null, 49, state));
        Assert.Null(GraphRouting.BroadQueryHint("revit_count_elements", "Lines", null, 5000, state));
        var hint = GraphRouting.BroadQueryHint("revit_get_elements_info", "Lines", "Teine korrus", 2590, state)!;
        Assert.Contains("category=\"Lines\"", hint);
        Assert.Contains("level=\"Teine korrus\"", hint);
        Assert.Contains("graphHint=false", hint);
    }

    [Fact]
    public void BroadQueryHint_MentionsMissingOrStaleGraph()
    {
        Assert.Contains("Building the model graph", GraphRouting.BroadQueryHint("revit_get_elements_info", "Lines", null, 100, new GraphRoutingState())!);
        var stale = Fresh();
        stale.Stale = true;
        stale.StaleReason = "saved since";
        Assert.Contains("stale (saved since)", GraphRouting.BroadQueryHint("revit_find_elements_by_parameter", null, null, 100, stale)!);
    }

    [Theory]
    [InlineData("room 1.12 devices", "1.12")]
    [InlineData("panel JK-1", "JK-1")]
    [InlineData("ruum \"Väikeklass\"", "Väikeklass")]
    [InlineData("everything", null)]
    public void ExtractNameHint(string text, string? expected) =>
        Assert.Equal(expected, GraphRouting.ExtractNameHint(text));
}
