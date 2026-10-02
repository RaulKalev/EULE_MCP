using RevitMCP.Addin.RoomDevices;
using Xunit;

namespace RevitMCP.Tests;

public class FireAlarmRulesTests
{
    private static RoomPolygon Poly(params (double X, double Y)[] pts) =>
        RoomGeometryMath.Normalize(new RoomPolygon { Outer = pts.Select(p => new P2(p.X, p.Y)).ToList() });

    // ── Table 1 ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("pointSmoke", 3000, "suitable")]
    [InlineData("pointSmoke", 12000, "suitable")]
    [InlineData("pointSmoke", 14000, "conditional")]
    [InlineData("pointSmoke", 20000, "unsuitable")]
    [InlineData("pointHeat", 6000, "suitable")]
    [InlineData("pointHeat", 7000, "conditional")]
    [InlineData("pointHeat", 8000, "unsuitable")]
    [InlineData("linearHeat", 7500, "suitable")]
    [InlineData("linearHeat", 8500, "conditional")]
    [InlineData("linearHeat", 10000, "unsuitable")]
    [InlineData("linearSmoke", 16000, "suitable")]
    [InlineData("linearSmoke", 30000, "conditional")]
    [InlineData("aspirating", 12000, "suitable")]
    [InlineData("aspirating", 40000, "conditional")]
    [InlineData("flame", 40000, "suitable")]
    [InlineData("pointSmoke", 50000, "unsuitable")]
    public void CheckHeight_FollowsTable1(string type, double heightMm, string expected) =>
        Assert.Equal(expected, FireAlarmRules.CheckHeight(type, heightMm).Status);

    [Fact]
    public void CheckHeight_BandBoundariesAreInclusive()
    {
        Assert.Equal("≤ 6 m", FireAlarmRules.CheckHeight("pointHeat", 6000).Band);
        Assert.Equal("≤ 7.5 m", FireAlarmRules.CheckHeight("pointHeat", 6001).Band);
    }

    [Theory]
    [InlineData("pointHeat", 7000, "A1", "suitable")]
    [InlineData("pointHeat", 7000, "A1R", "suitable")]
    [InlineData("pointHeat", 7000, "A2", "unsuitable")]
    [InlineData("pointHeat", 5000, "B", "conditional")]
    [InlineData("linearHeat", 8500, "A1", "suitable")]
    [InlineData("linearHeat", 8500, "A2", "unsuitable")]
    [InlineData("aspirating", 40000, "C", "unsuitable")]
    [InlineData("aspirating", 40000, "B", "conditional")]
    public void CheckHeight_ClassRefinesLimitedCells(string type, double heightMm, string cls, string expected) =>
        Assert.Equal(expected, FireAlarmRules.CheckHeight(type, heightMm, cls).Status);

    // ── Spacing ──────────────────────────────────────────────────────────────

    [Fact]
    public void Spacing_SmokeAndHeat_MatchTheStandard()
    {
        var smoke = FireAlarmRules.Spacing("pointSmoke")!;
        Assert.Equal((6200, 8800, 4400, 12400, 6200), (smoke.RadiusMm, smoke.MaxSpacingMm, smoke.MaxWallDistanceMm, smoke.CorridorSpacingMm, smoke.CorridorEndDistanceMm));
        var heat = FireAlarmRules.Spacing("pointHeat")!;
        Assert.Equal((4500, 6400, 3200, 9000, 4500), (heat.RadiusMm, heat.MaxSpacingMm, heat.MaxWallDistanceMm, heat.CorridorSpacingMm, heat.CorridorEndDistanceMm));
        Assert.Equal(6200, FireAlarmRules.Spacing("co")!.RadiusMm);
        Assert.Equal(6200, FireAlarmRules.Spacing("aspirating")!.RadiusMm);
        Assert.Null(FireAlarmRules.Spacing("linearSmoke"));
        Assert.Null(FireAlarmRules.Spacing("sounder"));
    }

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(10, 1.10)]
    [InlineData(25, 1.25)]
    [InlineData(40, 1.25)]
    public void SlopeFactor_IsOnePercentPerDegree_CappedAt25(double deg, double expected) =>
        Assert.Equal(expected, FireAlarmRules.SlopeFactor(deg), 6);

    [Fact]
    public void Spacing_ScalesWithSlope()
    {
        var s = FireAlarmRules.Spacing("pointSmoke", 20)!;
        Assert.Equal(8800 * 1.2, s.MaxSpacingMm, 6);
        Assert.Equal(6200 * 1.2, s.RadiusMm, 6);
    }

    [Fact]
    public void Grid_WithSmokeRules_SatisfiesSpacingAndWallDistance()
    {
        var room = Poly((0, 0), (20000, 0), (20000, 12000), (0, 12000));
        var s = FireAlarmRules.Spacing("pointSmoke")!;
        var g = RoomGeometryMath.Grid(room, s.MaxSpacingMm, s.MaxWallDistanceMm, coverageRadiusMm: s.RadiusMm);
        Assert.True(g.StepXMm <= 8800 && g.StepYMm <= 8800);
        Assert.All(g.Points, p => Assert.True(RoomGeometryMath.DistanceToBoundary(room, p) <= 4400 + 1e-6 ||
                                              RoomGeometryMath.DistanceToBoundary(room, p) <= g.StepXMm));
        Assert.Equal(0, g.SamplesUncovered);
    }

    // ── Corridors ────────────────────────────────────────────────────────────

    [Fact]
    public void Corridor_Smoke_25m_Gets3PointsOnCentreline()
    {
        var corridor = Poly((0, 0), (25000, 0), (25000, 1800), (0, 1800));
        var layout = FireAlarmRules.Corridor(corridor, 12400, 6200);
        Assert.True(layout.IsCorridor);
        Assert.Equal(3, layout.Points.Count);
        Assert.All(layout.Points, p => Assert.Equal(900, p.Y, 3));
        var xs = layout.Points.Select(p => p.X).OrderBy(x => x).ToList();
        Assert.True(xs[0] <= 6200 && 25000 - xs[^1] <= 6200);
        Assert.True(xs[1] - xs[0] <= 12400);
    }

    [Fact]
    public void Corridor_RotatedHeat_FollowsItsAxis()
    {
        // 20 m x 1.5 m corridor rotated 30°.
        var a = Math.PI / 6;
        P2 R(double x, double y) => new(x * Math.Cos(a) - y * Math.Sin(a), x * Math.Sin(a) + y * Math.Cos(a));
        var c = RoomGeometryMath.Normalize(new RoomPolygon { Outer = [R(0, 0), R(20000, 0), R(20000, 1500), R(0, 1500)] });
        var layout = FireAlarmRules.Corridor(c, 9000, 4500);
        Assert.True(layout.IsCorridor);
        Assert.Equal(3, layout.Points.Count);
        Assert.All(layout.Points, p => Assert.True(RoomGeometryMath.Contains(c, p)));
        Assert.All(layout.Points, p => Assert.True(RoomGeometryMath.DistanceToBoundary(c, p) > 700));
    }

    [Fact]
    public void Corridor_WideRoom_IsNotACorridor()
    {
        var room = Poly((0, 0), (10000, 0), (10000, 3000), (0, 3000));
        Assert.False(FireAlarmRules.Corridor(room, 12400, 6200).IsCorridor);
    }

    // ── Sounders ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, false, 65)]
    [InlineData(50.0, false, 65)]
    [InlineData(60.0, false, 70)]
    [InlineData(null, true, 75)]
    [InlineData(70.0, true, 80)]
    public void RequiredAlarmDb_TakesTheHigherRule(double? ambient, bool sleeping, double expected) =>
        Assert.Equal(expected, FireAlarmRules.RequiredAlarmDb(ambient, sleeping), 6);

    [Fact]
    public void LevelAt_DropsSixDbPerDoubling()
    {
        Assert.Equal(100, FireAlarmRules.LevelAt(100, 0.3), 6);
        Assert.Equal(100 - 20 * Math.Log10(2), FireAlarmRules.LevelAt(100, 2), 6);
        Assert.Equal(3.01, FireAlarmRules.SumDb([0, 0]), 2);
    }

    [Fact]
    public void SoundCoverage_FindsQuietFarEnd_AndLoudSpots()
    {
        var room = Poly((0, 0), (30000, 0), (30000, 4000), (0, 4000));
        var quiet = FireAlarmRules.SoundCoverage(room, [(new P2(1000, 2000), 90.0)], 65);
        Assert.True(quiet.BelowRequired > 0);
        Assert.NotEmpty(quiet.QuietRegions);
        Assert.True(quiet.QuietRegions[0].Center.X > 15000);

        var loud = FireAlarmRules.SoundCoverage(room, [(new P2(1000, 2000), 125.0)], 65);
        Assert.True(loud.AboveMaximum > 0);
        Assert.Equal(0, loud.BelowRequired);
    }

    [Fact]
    public void Types_NormalizeCaseInsensitively()
    {
        Assert.Equal("pointSmoke", FireDeviceTypes.Normalize("POINTSMOKE"));
        Assert.Null(FireDeviceTypes.Normalize("laser"));
    }
}
