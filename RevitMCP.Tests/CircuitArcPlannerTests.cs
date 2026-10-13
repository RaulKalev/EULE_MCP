using RevitMCP.Addin.Electrical;
using Xunit;

namespace RevitMCP.Tests;

public class CircuitArcPlannerTests
{
    private static CircuitArcPlanner.Node N(long id, double x, double y) => new(id, x, y);

    [Fact]
    public void Chain_JoinsEachDeviceToTheNearestOneNotJoinedYet()
    {
        // A row of sockets given out of order, plus one off to the side
        var nodes = new[] { N(1, 0, 0), N(2, 30, 0), N(3, 10, 0), N(4, 20, 0), N(5, 20, 8) };

        var plan = CircuitArcPlanner.Build(nodes, 0, CircuitArcPlanner.Chain, 0.2, "left", 0.1);

        Assert.Equal(new[] { (1L, 3L), (3L, 4L), (4L, 5L), (5L, 2L) }, plan.Arcs.Select(a => (a.FromId, a.ToId)).ToArray());
    }

    [Fact]
    public void Tree_BranchesInsteadOfDoublingBack()
    {
        var nodes = new[] { N(1, 0, 0), N(2, 30, 0), N(3, 10, 0), N(4, 20, 0), N(5, 20, 8) };

        var plan = CircuitArcPlanner.Build(nodes, 0, CircuitArcPlanner.Tree, 0.2, "left", 0.1);

        // 5 hangs off 4, and 2 is reached from 4 as well (10) rather than from 5 (12.8)
        Assert.Equal(new[] { (1L, 3L), (3L, 4L), (4L, 5L), (4L, 2L) }, plan.Arcs.Select(a => (a.FromId, a.ToId)).ToArray());
        Assert.Equal(4, plan.Arcs.Count);   // n - 1 arcs: every device joined once
    }

    [Fact]
    public void StackedDevices_GetNoArc_AndDoNotMoveTheChain()
    {
        var nodes = new[] { N(1, 0, 0), N(2, 0.01, 0), N(3, 10, 0) };

        var plan = CircuitArcPlanner.Build(nodes, 0, CircuitArcPlanner.Chain, 0.2, "left", 0.5);

        Assert.Equal(new[] { 2L }, plan.Coincident);
        var arc = Assert.Single(plan.Arcs);
        Assert.Equal((1L, 3L), (arc.FromId, arc.ToId));
    }

    [Theory]
    [InlineData("left", 2.0)]
    [InlineData("right", -2.0)]
    public void Midpoint_IsOffTheChordByBulgeTimesLength(string side, double expectedY)
    {
        // Left of the direction +X is +Y
        var plan = CircuitArcPlanner.Build(new[] { N(1, 0, 0), N(2, 10, 0) }, 0, CircuitArcPlanner.Chain, 0.2, side, 0.1);

        var arc = Assert.Single(plan.Arcs);
        Assert.Equal(5.0, arc.MidX, 6);
        Assert.Equal(expectedY, arc.MidY, 6);
        Assert.Equal(10.0, arc.Chord, 6);
    }

    [Fact]
    public void Alternate_SwitchesSidesArcByArc()
    {
        var plan = CircuitArcPlanner.Build(new[] { N(1, 0, 0), N(2, 10, 0), N(3, 20, 0) }, 0, CircuitArcPlanner.Chain, 0.1, "alternate", 0.1);

        Assert.True(plan.Arcs[0].MidY > 0);
        Assert.True(plan.Arcs[1].MidY < 0);
    }

    [Fact]
    public void Nearest_PicksTheDeviceClosestToThePanel()
    {
        var nodes = new[] { N(1, 0, 0), N(2, 30, 0), N(3, 10, 0) };
        Assert.Equal(1, CircuitArcPlanner.Nearest(nodes, 40, 5));
    }

    [Fact]
    public void HomeRun_PointsAtThePanel_AndStopsShort()
    {
        // Panel straight to the right, 100 away: a home run of 9 ends at x = 9, bulging right of the direction (down)
        var arc = CircuitArcPlanner.HomeRun(N(7, 0, 0), 100, 0, 9, 0.2, "right");

        Assert.NotNull(arc);
        Assert.Equal((7L, 0L), (arc!.FromId, arc.ToId));
        Assert.Equal(9.0, arc.X2, 6);
        Assert.Equal(0.0, arc.Y2, 6);
        Assert.Equal(-1.8, arc.MidY, 6);

        // Close to the panel: never more than 80 % of the way
        Assert.Equal(4.0, CircuitArcPlanner.HomeRun(N(7, 0, 0), 5, 0, 9, 0.2, "left")!.Chord, 6);
        // On the panel: nothing to draw
        Assert.Null(CircuitArcPlanner.HomeRun(N(7, 3, 3), 3, 3, 9, 0.2, "left"));
    }

    [Fact]
    public void FewerThanTwoDevices_DrawNothing()
    {
        Assert.Empty(CircuitArcPlanner.Build(new[] { N(1, 0, 0) }, 0, CircuitArcPlanner.Chain, 0.2, "left", 0.1).Arcs);
        Assert.Empty(CircuitArcPlanner.Build(Array.Empty<CircuitArcPlanner.Node>(), 0, CircuitArcPlanner.Chain, 0.2, "left", 0.1).Arcs);
    }
}
