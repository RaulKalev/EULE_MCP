using RevitMCP.Addin.RoomDevices;
using Xunit;

namespace RevitMCP.Tests;

public class RoomGeometryMathTests
{
    // 6 m x 4 m room, counter-clockwise as given.
    private static RoomPolygon Rect(double w = 6000, double h = 4000) =>
        RoomGeometryMath.Normalize(new RoomPolygon
        {
            Outer = [new P2(0, 0), new P2(w, 0), new P2(w, h), new P2(0, h)]
        });

    // L-shape: 10x10 m minus the top-right 6x6 m.
    private static RoomPolygon LShape() =>
        RoomGeometryMath.Normalize(new RoomPolygon
        {
            Outer = [new P2(0, 0), new P2(10000, 0), new P2(10000, 4000), new P2(4000, 4000), new P2(4000, 10000), new P2(0, 10000)]
        });

    [Fact]
    public void Normalize_MakesOuterClockwise_AndDropsClosingPoint()
    {
        var p = RoomGeometryMath.Normalize(new RoomPolygon
        {
            Outer = [new P2(0, 0), new P2(1000, 0), new P2(1000, 1000), new P2(0, 1000), new P2(0, 0)]
        });
        Assert.Equal(4, p.Outer.Count);
        Assert.True(RoomGeometryMath.SignedArea(p.Outer) < 0);
    }

    [Fact]
    public void Normalize_MakesHolesCounterClockwise()
    {
        var p = RoomGeometryMath.Normalize(new RoomPolygon
        {
            Outer = [new P2(0, 0), new P2(5000, 0), new P2(5000, 5000), new P2(0, 5000)],
            Holes = [[new P2(2000, 2000), new P2(2000, 3000), new P2(3000, 3000), new P2(3000, 2000)]]
        });
        Assert.True(RoomGeometryMath.SignedArea(p.Holes[0]) > 0);
        Assert.Equal(25e6 - 1e6, RoomGeometryMath.AreaMm2(p), 3);
        Assert.False(RoomGeometryMath.Contains(p, new P2(2500, 2500)));
        Assert.True(RoomGeometryMath.Contains(p, new P2(1000, 1000)));
    }

    [Fact]
    public void Faces_NormalsPointIntoRoom()
    {
        var faces = RoomGeometryMath.BuildFaces(Rect());
        Assert.Equal(4, faces.Count);
        var room = Rect();
        foreach (var f in faces)
        {
            var probe = f.PointAt(f.LengthMm / 2, 100);
            Assert.True(RoomGeometryMath.Contains(room, probe), $"face {f.Index} normal points out");
        }
    }

    [Fact]
    public void Faces_HoleNormalsPointAwayFromHole()
    {
        var p = RoomGeometryMath.Normalize(new RoomPolygon
        {
            Outer = [new P2(0, 0), new P2(5000, 0), new P2(5000, 5000), new P2(0, 5000)],
            Holes = [[new P2(2000, 2000), new P2(3000, 2000), new P2(3000, 3000), new P2(2000, 3000)]]
        });
        var holeFaces = RoomGeometryMath.BuildFaces(p).Where(f => f.LoopIndex == 1).ToList();
        Assert.Equal(4, holeFaces.Count);
        Assert.All(holeFaces, f => Assert.True(RoomGeometryMath.Contains(p, f.PointAt(f.LengthMm / 2, 100))));
    }

    [Fact]
    public void InteriorPoint_OfLShape_IsInsideAndClearOfWalls()
    {
        var l = LShape();
        var p = RoomGeometryMath.InteriorPoint(l);
        Assert.True(RoomGeometryMath.Contains(l, p));
        Assert.True(RoomGeometryMath.DistanceToBoundary(l, p) >= 1500);
    }

    [Fact]
    public void InteriorPoint_OfRectangle_IsCentroid()
    {
        var p = RoomGeometryMath.InteriorPoint(Rect());
        Assert.Equal(3000, p.X, 3);
        Assert.Equal(2000, p.Y, 3);
    }

    [Fact]
    public void ProjectOpening_MapsDoorToFaceStretch()
    {
        var face = new WallFace { Start = new P2(0, 0), End = new P2(6000, 0), NormalIntoRoom = new P2(0, 1) };
        var door = RoomGeometryMath.ProjectOpening(face, 7, "door", new P2(2000, -100), 900);
        Assert.NotNull(door);
        Assert.Equal(1550, door!.FromMm, 3);
        Assert.Equal(2450, door.ToMm, 3);
        Assert.Null(RoomGeometryMath.ProjectOpening(face, 8, "door", new P2(2000, -1000), 900));
    }

    [Theory]
    [InlineData(1, "lock", 2450 + 150)]
    [InlineData(1, "hinge", 1550 - 150)]
    [InlineData(-1, "lock", 1550 - 150)]
    public void AlongBesideDoor_PicksSide(int lockSign, string side, double expected)
    {
        var door = new FaceOpening { FromMm = 1550, ToMm = 2450 };
        Assert.Equal(expected, RoomGeometryMath.AlongBesideDoor(door, lockSign, side, 150), 3);
    }

    [Fact]
    public void LockSignFromHand_FollowsConvention()
    {
        var face = new WallFace { Start = new P2(0, 0), End = new P2(6000, 0) };
        Assert.Equal(1, RoomGeometryMath.LockSignFromHand(face, new P2(1, 0), handPointsToLatch: true));
        Assert.Equal(-1, RoomGeometryMath.LockSignFromHand(face, new P2(1, 0), handPointsToLatch: false));
        Assert.Equal(0, RoomGeometryMath.LockSignFromHand(face, new P2(0, 1), handPointsToLatch: true));
    }

    [Fact]
    public void LockSignFromCorners_PutsLockOnLongerSide()
    {
        var face = new WallFace { Start = new P2(0, 0), End = new P2(6000, 0) };
        Assert.Equal(1, RoomGeometryMath.LockSignFromCorners(face, new FaceOpening { FromMm = 100, ToMm = 1000 }));
        Assert.Equal(-1, RoomGeometryMath.LockSignFromCorners(face, new FaceOpening { FromMm = 5000, ToMm = 5900 }));
    }

    [Fact]
    public void Grid_RespectsSpacingAndWallDistance()
    {
        // 12 x 4 m, spacing 10 m, max 3 m from wall -> cell limit 6 m -> 2 x 1 points.
        var g = RoomGeometryMath.Grid(Rect(12000, 4000), maxSpacingMm: 10000, maxDistFromWallMm: 3000);
        Assert.Equal(2, g.Points.Count);
        Assert.Equal(6000, g.StepXMm, 3);
        Assert.Equal(0, g.SamplesUncovered);
    }

    [Fact]
    public void Grid_SmallRoom_GetsOnePoint()
    {
        var g = RoomGeometryMath.Grid(Rect(3000, 3000), maxSpacingMm: 10600, maxDistFromWallMm: 7500);
        var p = Assert.Single(g.Points);
        Assert.Equal(1500, p.X, 3);
    }

    [Fact]
    public void Grid_LShape_CoversEveryInteriorSample()
    {
        var l = LShape();
        var g = RoomGeometryMath.Grid(l, maxSpacingMm: 5000);
        Assert.All(g.Points, p => Assert.True(RoomGeometryMath.Contains(l, p)));
        Assert.Equal(0, g.SamplesUncovered);
    }

    [Fact]
    public void Grid_ReportsUncoveredArea_WithTightRadius()
    {
        var g = RoomGeometryMath.Grid(Rect(10000, 10000), maxSpacingMm: 10000, coverageRadiusMm: 3000);
        Assert.Single(g.Points);
        Assert.True(g.SamplesUncovered > 0);
        Assert.True(g.UncoveredAreaM2 > 0);
    }

    [Fact]
    public void CheckWallPosition_FlagsOpeningCornerAndSwing()
    {
        var face = new WallFace { Start = new P2(0, 0), End = new P2(6000, 0), NormalIntoRoom = new P2(0, 1) };
        var door = new FaceOpening { Id = 5, FromMm = 1550, ToMm = 2450 };
        var swing = RoomGeometryMath.SwingStretch(door, lockSign: 1);

        Assert.Contains(RoomGeometryMath.CheckWallPosition(face, 2000, [door], 300, []), w => w.Contains("opening"));
        Assert.Contains(RoomGeometryMath.CheckWallPosition(face, 100, [], 300, []), w => w.Contains("corner"));
        Assert.Contains(RoomGeometryMath.CheckWallPosition(face, 1000, [], 1100, [swing]), w => w.Contains("leaf"));
        Assert.Empty(RoomGeometryMath.CheckWallPosition(face, 2600, [door], 1100, [swing]));
        Assert.Empty(RoomGeometryMath.CheckWallPosition(face, 1000, [], 2400, [swing]));
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 90)]
    [InlineData(-1, 0, 180)]
    [InlineData(0, -1, 270)]
    public void AngleDeg_IsCounterClockwiseFromX(double x, double y, double expected) =>
        Assert.Equal(expected, RoomGeometryMath.AngleDeg(new P2(x, y)), 6);
}
