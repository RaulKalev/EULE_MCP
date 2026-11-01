using RevitMCP.Addin.Coordination.ClashFix;
using Xunit;

namespace RevitMCP.Tests;

public class ClashFixMathTests
{
    private static List<FixVector> Box(double x0, double y0, double z0, double x1, double y1, double z1) => new()
    {
        new(x0, y0, z0), new(x1, y0, z0), new(x0, y1, z0), new(x1, y1, z0),
        new(x0, y0, z1), new(x1, y0, z1), new(x0, y1, z1), new(x1, y1, z1)
    };

    private static string[] Names(IEnumerable<FixDirection> directions) => directions.Select(d => d.Name).ToArray();

    [Fact]
    public void Directions_HorizontalRun_UpDownAndSquareSideways()
    {
        var directions = ClashFixMath.Directions(MoverShape.LinearRun, new FixVector(1, 0, 0), null, null);
        Assert.Equal(new[] { "up", "down", "side+", "side-" }, Names(directions));
        var side = directions.Single(d => d.Name == "side+").Vector;
        Assert.Equal(0, side.X, 9);
        Assert.Equal(1, Math.Abs(side.Y), 9);
    }

    [Fact]
    public void Directions_VerticalRun_ModelAxesOnly()
    {
        var directions = ClashFixMath.Directions(MoverShape.LinearRun, new FixVector(0, 0, -1), null, null);
        Assert.Equal(new[] { "+x", "-x", "+y", "-y" }, Names(directions));
    }

    [Fact]
    public void Directions_WallHostedInstance_StaysInTheWallPlane()
    {
        // A wall facing +Y: no move along Y (into or out of the wall).
        var directions = ClashFixMath.Directions(MoverShape.PointInstance, null, new FixVector(0, 1, 0), null);
        Assert.Equal(new[] { "up", "down", "+x", "-x" }, Names(directions));
    }

    [Fact]
    public void Directions_Filter()
    {
        Assert.Equal(new[] { "up", "down" },
            Names(ClashFixMath.Directions(MoverShape.LinearRun, new FixVector(1, 0, 0), null, new[] { "vertical" })));
        Assert.Equal(new[] { "side+", "side-" },
            Names(ClashFixMath.Directions(MoverShape.LinearRun, new FixVector(1, 0, 0), null, new[] { "horizontal" })));
        Assert.Equal(new[] { "down" },
            Names(ClashFixMath.Directions(MoverShape.PointInstance, null, null, new[] { "DOWN" })));
    }

    [Fact]
    public void TravelToPass_TrayThroughBeam()
    {
        // Tray 100 high at z 2900..3000, beam z 2700..3100.
        var tray = Box(0, 0, 2900, 5000, 300, 3000);
        var beam = Box(2000, -1000, 2700, 2300, 1000, 3100);
        Assert.Equal(200, ClashFixMath.TravelToPass(tray, beam, FixVector.Up)!.Value, 6);       // 3100 - 2900
        Assert.Equal(300, ClashFixMath.TravelToPass(tray, beam, -FixVector.Up)!.Value, 6);      // 3000 - 2700
        Assert.Null(ClashFixMath.TravelToPass(new List<FixVector>(), beam, FixVector.Up));
    }

    [Fact]
    public void MinimalClearingTravel_Bisects()
    {
        // Clear from 137.5 on.
        var travel = ClashFixMath.MinimalClearingTravel(400, t => t >= 137.5, precision: 1);
        Assert.NotNull(travel);
        Assert.InRange(travel!.Value, 137.5, 138.5);
        Assert.Null(ClashFixMath.MinimalClearingTravel(100, t => t >= 137.5, precision: 1));
    }

    [Theory]
    [InlineData(137.6, 25, 10, 170)]
    [InlineData(140, 0, 10, 140)]
    [InlineData(0.4, 25, 5, 30)]
    [InlineData(12.34, 0, 0, 12.3)]
    public void ProposedTravel_AddsClearanceAndRoundsUp(double clearing, double clearance, double step, double expected) =>
        Assert.Equal(expected, ClashFixMath.ProposedTravel(clearing, clearance, step), 6);

    [Fact]
    public void ConnectedMoveBlocker_OnlyAlongTheNeighbours()
    {
        // A horizontal segment between two vertical drops may move up or down: the drops stretch.
        var drops = new[] { new FixVector(0, 0, 1), new FixVector(0, 0, -1) };
        Assert.Null(ClashFixMath.ConnectedMoveBlocker(FixVector.Up, drops, 0));
        Assert.NotNull(ClashFixMath.ConnectedMoveBlocker(new FixVector(0, 1, 0), drops, 0));

        // Between two horizontal runs (elbows in plan), lifting it would slope both.
        var flat = new[] { new FixVector(0, 1, 0), new FixVector(1, 0, 0) };
        Assert.Contains("2 joined", ClashFixMath.ConnectedMoveBlocker(FixVector.Up, flat, 0));

        Assert.Contains("equipment", ClashFixMath.ConnectedMoveBlocker(FixVector.Up, Array.Empty<FixVector>(), 1));
    }

    [Fact]
    public void RankMovers_FreeAndSmallFirst()
    {
        var ranked = ClashFixMath.RankMovers(new[]
        {
            new MoverCandidate { ElementId = 1, ConnectedEnds = 2, Volume = 1 },
            new MoverCandidate { ElementId = 2, ConnectedEnds = 0, Volume = 50 },
            new MoverCandidate { ElementId = 3, ConnectedEnds = 0, Volume = 5 },
            new MoverCandidate { ElementId = 4, Blocked = "structure" }
        });
        Assert.Equal(new long[] { 3, 2, 1 }, ranked.Select(c => c.ElementId).ToArray());
    }
}
