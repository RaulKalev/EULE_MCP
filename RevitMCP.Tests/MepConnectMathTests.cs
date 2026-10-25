using RevitMCP.Addin.Mep;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#106: how two open tray or conduit ends lie, and which call joins them.</summary>
public class MepConnectMathTests
{
    private static MepEnd End(double x, double y, double z, double dx, double dy, double dz) =>
        new(new MepPoint(x, y, z), new MepPoint(dx, dy, dz));

    [Fact]
    public void TouchingEndsInLine_AreCoincident_AndConnectDirectly()
    {
        // A runs along +X and ends at 1000; B starts there and runs on (its end points back, -X).
        var g = MepConnectMath.Analyze(End(1000, 0, 0, 1, 0, 0), End(1000.4, 0, 0, -1, 0, 0));

        Assert.Equal(MepRelation.Coincident, g.Relation);
        Assert.True(g.GapMm < 1);
        Assert.Equal("connect_to", MepConnectMath.Decide(g, "auto", sameSize: true).Action);
        Assert.Equal("transition", MepConnectMath.Decide(g, "auto", sameSize: false).Action);
        Assert.Equal("union", MepConnectMath.Decide(g, "union", sameSize: true).Action);
    }

    [Fact]
    public void TouchingEndsAtAnAngle_GetAnElbow()
    {
        var g = MepConnectMath.Analyze(End(1000, 0, 0, 1, 0, 0), End(1000, 0, 0, 0, 1, 0));
        Assert.Equal(MepRelation.CoincidentAngled, g.Relation);
        Assert.Equal(90, g.AngleDeg, 2);
        Assert.Equal("elbow", MepConnectMath.Decide(g, "auto", true).Action);
    }

    [Fact]
    public void FacingEndsOnOneLine_AreCollinear_WithTheGap()
    {
        var g = MepConnectMath.Analyze(End(1000, 0, 3000, 1, 0, 0), End(1750, 0, 3000, -1, 0, 0));

        Assert.Equal(MepRelation.Collinear, g.Relation);
        Assert.Equal(750, g.GapMm, 6);
        Assert.Equal("extend", MepConnectMath.Decide(g, "auto", true).Action);
        Assert.Equal("extend", MepConnectMath.Decide(g, "extend", true).Action);
        Assert.Equal("union", MepConnectMath.Decide(g, "union", true).Action);
        Assert.Equal("transition", MepConnectMath.Decide(g, "auto", false).Action);
        Assert.Equal(MepStatus.SkewLines, MepConnectMath.Decide(g, "elbow", true).RefusedStatus);
    }

    [Fact]
    public void CrossingLines_AreAngled_WithTheCrossingAndHowFarEachEndGrows()
    {
        // A ends at (1000,0) going +X; B ends at (1500,800) going -Y. They cross at (1500,0).
        var g = MepConnectMath.Analyze(End(1000, 0, 0, 1, 0, 0), End(1500, 800, 0, 0, -1, 0));

        Assert.Equal(MepRelation.Angled, g.Relation);
        Assert.Equal(90, g.AngleDeg, 2);
        Assert.NotNull(g.Intersection);
        Assert.Equal(1500, g.Intersection!.Value.X, 3);
        Assert.Equal(0, g.Intersection!.Value.Y, 3);
        Assert.Equal(500, g.ExtendAMm, 3);
        Assert.Equal(800, g.ExtendBMm, 3);
        Assert.Equal(800, g.ReachMm, 3);
        Assert.Equal("elbow", MepConnectMath.Decide(g, "auto", true).Action);
        Assert.Equal(MepStatus.SkewLines, MepConnectMath.Decide(g, "extend", true).RefusedStatus);
        Assert.Equal(MepStatus.SkewLines, MepConnectMath.Decide(g, "union", true).RefusedStatus);
    }

    [Fact]
    public void LinesThatPassEachOther_AreSkew()
    {
        // B runs along Y one metre above A's line.
        var g = MepConnectMath.Analyze(End(1000, 0, 0, 1, 0, 0), End(1500, 800, 1000, 0, -1, 0));

        Assert.Equal(MepRelation.Skew, g.Relation);
        Assert.Equal(1000, g.LineDistanceMm, 3);
        var decision = MepConnectMath.Decide(g, "auto", true);
        Assert.Null(decision.Action);
        Assert.Equal(MepStatus.SkewLines, decision.RefusedStatus);
    }

    [Fact]
    public void ParallelSideBySide_CannotBeJoinedByOneFitting()
    {
        var g = MepConnectMath.Analyze(End(1000, 0, 0, 1, 0, 0), End(1500, 300, 0, -1, 0, 0));
        Assert.Equal(MepRelation.ParallelOffset, g.Relation);
        Assert.Equal(300, g.LineDistanceMm, 3);
        Assert.Equal(MepStatus.SkewLines, MepConnectMath.Decide(g, "auto", true).RefusedStatus);
    }

    [Fact]
    public void EndsPointingTheSameWay_AreNotCollinear()
    {
        var g = MepConnectMath.Analyze(End(1000, 0, 0, 1, 0, 0), End(1500, 0, 0, 1, 0, 0));
        Assert.Equal(MepRelation.ParallelOffset, g.Relation);
    }

    [Fact]
    public void AGapBeyondMaxGap_IsTooFar()
    {
        var g = MepConnectMath.Analyze(End(0, 0, 0, 1, 0, 0), End(9000, 0, 0, -1, 0, 0));
        Assert.Equal(MepRelation.Collinear, g.Relation);
        Assert.Equal(MepStatus.TooFar, MepConnectMath.Decide(g, "auto", true, maxGapMm: 5000).RefusedStatus);
        Assert.Equal("extend", MepConnectMath.Decide(g, "auto", true, maxGapMm: 10000).Action);
    }

    [Fact]
    public void NearestWithin_PicksTheClosestCandidateInsideTheSnap()
    {
        var candidates = new[] { new MepPoint(100, 0, 0), new MepPoint(30, 0, 0), new MepPoint(20, 40, 0) };
        Assert.Equal(1, MepConnectMath.NearestWithin(new MepPoint(0, 0, 0), candidates, 50));
        Assert.Equal(-1, MepConnectMath.NearestWithin(new MepPoint(0, 0, 0), candidates, 10));
    }

    [Fact]
    public void PerpendicularDistance_IsMeasuredFromTheLine()
    {
        Assert.Equal(5, MepConnectMath.PerpendicularDistance(new MepPoint(7, 5, 0), new MepPoint(0, 0, 0), new MepPoint(2, 0, 0)), 6);
    }
}
