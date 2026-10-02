using RevitMCP.Addin.RoomDevices;
using Xunit;

namespace RevitMCP.Tests;

public class RoomAuditMathTests
{
    private static RoomPolygon Rect(double x0, double y0, double w, double h) =>
        RoomGeometryMath.Normalize(new RoomPolygon
        {
            Outer = [new P2(x0, y0), new P2(x0 + w, y0), new P2(x0 + w, y0 + h), new P2(x0, y0 + h)]
        });

    private static RoomCandidate Room(string number, string name, double area, double floorZ, double x0 = 0) => new()
    {
        Number = number,
        Name = name,
        AreaM2 = area,
        FloorZMm = floorZ,
        HeightMm = 3000,
        Polygon = Rect(x0, 0, 4000, 4000)
    };

    [Fact]
    public void LocateRoom_PicksRoomOnTheRightFloor()
    {
        var rooms = new[] { Room("101", "Klass", 16, 0), Room("201", "Klass", 16, 3600) };
        Assert.Equal(0, RoomAuditMath.LocateRoom(rooms, new P2(2000, 2000), 300));
        Assert.Equal(1, RoomAuditMath.LocateRoom(rooms, new P2(2000, 2000), 6400));
        Assert.Equal(-1, RoomAuditMath.LocateRoom(rooms, new P2(9000, 2000), 300));
        Assert.Equal(-1, RoomAuditMath.LocateRoom(rooms, new P2(2000, 2000), 20000));
    }

    [Theory]
    [InlineData(0, 90, 90)]
    [InlineData(350, 10, 20)]
    [InlineData(10, 350, -20)]
    [InlineData(0, 180, 180)]
    public void DeltaDeg_IsShortestSignedTurn(double current, double target, double expected) =>
        Assert.Equal(expected, RoomAuditMath.DeltaDeg(current, target), 6);

    [Fact]
    public void FaceTowardDeg_PointsAtTarget()
    {
        Assert.Equal(90, RoomAuditMath.FaceTowardDeg(new P2(0, 0), new P2(0, 5))!.Value, 6);
        Assert.Null(RoomAuditMath.FaceTowardDeg(new P2(1, 1), new P2(1, 1)));
    }

    [Fact]
    public void EvaluateRules_FindsMissingAndExcess_AndHonoursExclusions()
    {
        var rooms = new[]
        {
            Room("101", "Klass", 50, 0),
            Room("102", "WC", 3, 0, 5000),
            Room("103", "WC", 6, 0, 10000)
        };
        var counts = new Dictionary<int, Dictionary<string, int>>
        {
            [0] = new(StringComparer.OrdinalIgnoreCase) { ["ANDM_2x"] = 1, ["ATS_SA"] = 3 },
            [2] = new(StringComparer.OrdinalIgnoreCase) { ["ATS_SA"] = 1 }
        };
        var rules = new[]
        {
            new DeviceRule { RoomFilter = "^klass", Code = "ANDM_2x", Min = 2 },
            // every room needs a smoke detector, except WCs under 4 m²
            new DeviceRule { Code = "ATS_SA", Min = 1, Max = 2 },
        };
        rules[1].ExcludeRoomFilter = "";
        var smallWcExcluded = new DeviceRule { Code = "ATS_SA", Min = 1, MinAreaM2 = 4 };

        var (findings, checkedRooms) = RoomAuditMath.EvaluateRules(rooms, counts, [rules[0], rules[1], smallWcExcluded]);

        Assert.Equal(new[] { 1, 3, 2 }, checkedRooms);
        Assert.Contains(findings, f => f.RuleIndex == 0 && f.RoomNumber == "101" && f.Kind == "missing" && f.Count == 1);
        Assert.Contains(findings, f => f.RuleIndex == 1 && f.RoomNumber == "101" && f.Kind == "excess");
        Assert.Contains(findings, f => f.RuleIndex == 1 && f.RoomNumber == "102" && f.Kind == "missing");
        Assert.DoesNotContain(findings, f => f.RuleIndex == 2 && f.RoomNumber == "102");
    }

    [Fact]
    public void Coverage_OneDetectorInSmallRoom_CoversAll()
    {
        var r = RoomAuditMath.Coverage(Rect(0, 0, 4000, 4000), [new CoverageDevice { Position = new P2(2000, 2000), RadiusMm = 7500 }]);
        Assert.Equal(0, r.Uncovered);
        Assert.Equal(1.0, r.CoveredFraction, 6);
        Assert.Empty(r.Regions);
    }

    [Fact]
    public void Coverage_ReportsUncoveredRegionAtFarEnd()
    {
        var r = RoomAuditMath.Coverage(Rect(0, 0, 20000, 4000), [new CoverageDevice { Position = new P2(2000, 2000), RadiusMm = 7500 }]);
        Assert.True(r.Uncovered > 0);
        var region = Assert.Single(r.Regions);
        Assert.True(region.Center.X > 10000);
        Assert.Equal(r.UncoveredAreaM2, region.AreaM2, 6);
    }

    [Fact]
    public void Coverage_Camera_OnlySeesItsSector()
    {
        var camera = new CoverageDevice { Position = new P2(0, 2000), RadiusMm = 30000, Facing = new P2(1, 0), FovDeg = 90 };
        Assert.True(RoomAuditMath.Covers(camera, new P2(5000, 2000)));
        Assert.False(RoomAuditMath.Covers(camera, new P2(-1000, 2000)));
        Assert.False(RoomAuditMath.Covers(camera, new P2(1000, 4500)));
    }

    [Fact]
    public void WallOffset_IsPositiveIntoRoom()
    {
        var face = new WallFace { Start = new P2(0, 0), End = new P2(5000, 0), NormalIntoRoom = new P2(0, 1) };
        var (along, offset) = RoomAuditMath.WallOffset(face, new P2(1200, 30));
        Assert.Equal(1200, along, 6);
        Assert.Equal(30, offset, 6);
        Assert.Equal(-50, RoomAuditMath.WallOffset(face, new P2(1200, -50)).OffsetMm, 6);
    }

    [Theory]
    [InlineData("WC", "^wc$", true)]
    [InlineData("Klass 1", "klass", true)]
    [InlineData("Klass (A", "Klass (A", true)] // invalid regex → substring
    [InlineData("Koridor", "klass", false)]
    public void TextMatches_RegexWithSubstringFallback(string text, string pattern, bool expected) =>
        Assert.Equal(expected, RoomAuditMath.TextMatches(text, pattern));

    [Fact]
    public void CheckMount_Wall_FlagsOffsetAndHeight()
    {
        var d = RoomAuditMath.CheckMount(DeviceMounts.Wall, 20,
            wallOffsetMm: -40, expectedWallOffsetMm: 0, heightMm: 310, expectedHeightMm: 300);
        var dev = Assert.Single(d);
        Assert.Equal("wallOffset", dev.Axis);
        Assert.Equal(-40, dev.DeviationMm, 6);
    }

    [Fact]
    public void CheckMount_Ceiling_UsesGapAndSkipsUnknownCeiling()
    {
        var off = RoomAuditMath.CheckMount(DeviceMounts.Ceiling, 20, ceilingGapMm: 150, expectedCeilingGapMm: 0);
        Assert.Equal("ceilingGap", Assert.Single(off).Axis);
        Assert.Empty(RoomAuditMath.CheckMount(DeviceMounts.Ceiling, 20, ceilingGapMm: null, expectedCeilingGapMm: 0));
        // wall-only inputs are ignored for a ceiling device
        Assert.Empty(RoomAuditMath.CheckMount(DeviceMounts.Ceiling, 20, wallOffsetMm: 500, expectedWallOffsetMm: 0));
    }

    [Fact]
    public void CheckMount_Floor_ChecksHeightOnly()
    {
        Assert.Equal("height", Assert.Single(RoomAuditMath.CheckMount(DeviceMounts.Floor, 20, heightMm: 60, expectedHeightMm: 0)).Axis);
        Assert.Empty(RoomAuditMath.CheckMount(DeviceMounts.Floor, 20, heightMm: 10, expectedHeightMm: 0));
    }
}
