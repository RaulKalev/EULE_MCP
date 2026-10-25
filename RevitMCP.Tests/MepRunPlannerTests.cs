using RevitMCP.Addin.Mep;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#106: a polyline becomes straight segments and the bends where elbows go.</summary>
public class MepRunPlannerTests
{
    private static MepPoint P(double x, double y, double z = 3000) => new(x, y, z);

    [Fact]
    public void RightAngledRun_HasOneSegmentPerLegAndAnElbowAtEveryBend()
    {
        var plan = MepRunPlanner.Plan(new[] { P(0, 0), P(10000, 0), P(10000, 5000), P(0, 5000) }, 100, autoFittings: true);

        Assert.True(plan.IsValid);
        Assert.Equal(3, plan.Segments.Count);
        Assert.Equal(2, plan.Bends.Count);
        Assert.All(plan.Bends, b => Assert.Equal(90, b.AngleDeg, 3));
        Assert.All(plan.Bends, b => Assert.Equal("elbow", b.Fitting));
        Assert.All(plan.Bends, b => Assert.Equal("horizontal", b.Plane));
        Assert.Equal(2, plan.ElbowCount);
        Assert.Equal(25000, plan.TotalLengthMm, 6);
        Assert.Equal(P(10000, 0), plan.Bends[0].At);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void FittingsNone_MeasuresBendsButPlacesNoElbows()
    {
        var plan = MepRunPlanner.Plan(new[] { P(0, 0), P(1000, 0), P(1000, 1000) }, 100, autoFittings: false);
        Assert.Single(plan.Bends);
        Assert.Null(plan.Bends[0].Fitting);
        Assert.Equal(0, plan.ElbowCount);
    }

    [Fact]
    public void CollinearPoints_AreMerged()
    {
        var plan = MepRunPlanner.Plan(new[] { P(0, 0), P(2000, 0), P(5000, 0), P(5000, 3000) }, 100, true);

        Assert.Equal(2, plan.Segments.Count);
        Assert.Equal(new[] { P(0, 0), P(5000, 0), P(5000, 3000) }, plan.Points);
        Assert.Contains(plan.Changes, c => c.Contains("collinear"));
    }

    [Fact]
    public void BendBelowOneDegree_IsTreatedAsStraight()
    {
        // 10 mm sideways over 2000 mm: about 0.29°.
        var plan = MepRunPlanner.Plan(new[] { P(0, 0), P(2000, 0), P(4000, 10) }, 100, true);

        Assert.Single(plan.Segments);
        Assert.Empty(plan.Bends);
        Assert.Contains(plan.Changes, c => c.Contains("below 1°"));
    }

    [Fact]
    public void ZeroLengthSegments_AreDropped()
    {
        var plan = MepRunPlanner.Plan(new[] { P(0, 0), P(0, 0), P(3000, 0), P(3000, 0.2), P(3000, 3000) }, 100, true);

        Assert.Equal(2, plan.Segments.Count);
        Assert.Equal(P(0, 0), plan.Points[0]);
        Assert.Equal(P(3000, 3000), plan.Points[^1]);
        Assert.Contains(plan.Changes, c => c.Contains("zero-length"));
    }

    [Fact]
    public void AllPointsTheSame_IsInvalid()
    {
        var plan = MepRunPlanner.Plan(new[] { P(5, 5), P(5, 5) }, 100, true);
        Assert.False(plan.IsValid);
        Assert.Contains(plan.Errors, e => e.Contains("no length"));
    }

    [Fact]
    public void ShortInnerSegment_CollapsesToItsMidpoint()
    {
        // A 50 mm cut corner between two long legs.
        var plan = MepRunPlanner.Plan(new[] { P(0, 0), P(4000, 0), P(4030, 40), P(4030, 4000) }, 100, true);

        Assert.True(plan.IsValid);
        Assert.Equal(2, plan.Segments.Count);
        Assert.Equal(P(4015, 20), plan.Points[1]);
        Assert.Equal(P(0, 0), plan.Points[0]);
        Assert.Equal(P(4030, 4000), plan.Points[^1]);
        Assert.Contains(plan.Changes, c => c.Contains("shorter than minSegmentMm"));
    }

    [Fact]
    public void SmallJog_DisappearsWhenItsLegsLineUp()
    {
        // A 50 mm sideways jog: collapsed to its midpoint, the two legs are one straight line.
        var plan = MepRunPlanner.Plan(new[] { P(0, 0), P(4000, 0), P(4000, 50), P(8000, 50) }, 100, true);

        Assert.Single(plan.Segments);
        Assert.Empty(plan.Bends);
        Assert.Equal(P(8000, 50), plan.Points[^1]);
    }

    [Fact]
    public void ShortFirstAndLastSegments_KeepTheRunEnds()
    {
        var plan = MepRunPlanner.Plan(new[] { P(0, 0), P(60, 0), P(60, 4000), P(4000, 4000), P(4000, 4040) }, 100, true);

        Assert.Equal(P(0, 0), plan.Points[0]);
        Assert.Equal(P(4000, 4040), plan.Points[^1]);
        Assert.All(plan.Segments, s => Assert.True(s.LengthMm >= 100));
    }

    [Fact]
    public void MinSegmentZero_KeepsShortSegments()
    {
        var plan = MepRunPlanner.Plan(new[] { P(0, 0), P(4000, 0), P(4000, 50), P(8000, 50) }, 0, true);
        Assert.Equal(3, plan.Segments.Count);
    }

    [Fact]
    public void SingleShortSegment_IsKeptWithAWarning()
    {
        var plan = MepRunPlanner.Plan(new[] { P(0, 0), P(60, 0) }, 100, true);
        Assert.True(plan.IsValid);
        Assert.Single(plan.Segments);
        Assert.Contains(plan.Warnings, w => w.Contains("single segment"));
    }

    [Fact]
    public void BendAngles_AreReported()
    {
        var plan = MepRunPlanner.Plan(new[] { P(0, 0), P(5000, 0), P(10000, 5000) }, 100, true);
        Assert.Equal(45, plan.Bends[0].AngleDeg, 2);
    }

    [Fact]
    public void BendOverNinetyDegrees_Warns()
    {
        var plan = MepRunPlanner.Plan(new[] { P(0, 0), P(5000, 0), P(0, 3000) }, 100, true);
        Assert.True(plan.Bends[0].AngleDeg > 90);
        Assert.Contains(plan.Warnings, w => w.Contains("more than a right angle"));
    }

    [Fact]
    public void DoublingBack_IsInvalid()
    {
        var plan = MepRunPlanner.Plan(new[] { P(0, 0), P(5000, 0), P(1000, 0) }, 100, true);
        Assert.False(plan.IsValid);
        Assert.Contains(plan.Errors, e => e.Contains("doubles back"));
    }

    [Fact]
    public void Riser_IsVerticalAndItsBendsAreVerticalElbows()
    {
        var plan = MepRunPlanner.Plan(new[] { P(0, 0, 3000), P(4000, 0, 3000), P(4000, 0, 6000), P(8000, 0, 6000) }, 100, true);

        Assert.Equal("vertical", plan.Segments[1].Orientation);
        Assert.All(plan.Bends, b => Assert.Equal("vertical", b.Plane));
        Assert.All(plan.Bends, b => Assert.Equal("elbow", b.Fitting));
        Assert.True(plan.HasVerticalBends);
        Assert.Contains(plan.Warnings, w => w.Contains("risers"));
    }

    [Fact]
    public void SlopedSegment_Warns()
    {
        var plan = MepRunPlanner.Plan(new[] { P(0, 0, 3000), P(4000, 0, 3500) }, 100, true);
        Assert.Equal("sloped", plan.Segments[0].Orientation);
        Assert.Contains(plan.Warnings, w => w.Contains("sloped"));
    }

    [Fact]
    public void RunOverFiveHundredMetres_WarnsAboutUnits()
    {
        // 600 "mm" meant as 600 m.
        var plan = MepRunPlanner.Plan(new[] { P(0, 0), P(600_000, 0) }, 100, true);
        Assert.Contains(plan.Warnings, w => w.Contains("500 m") && w.Contains("units"));
        var normal = MepRunPlanner.Plan(new[] { P(0, 0), P(60_000, 0) }, 100, true);
        Assert.DoesNotContain(normal.Warnings, w => w.Contains("units"));
    }

    [Fact]
    public void LevelBasedZ_FillsOnlyMissingHeights()
    {
        var spec = new MepRunSpec
        {
            Kind = MepKind.CableTray,
            Type = "KL-300x60",
            OffsetMm = 2800,
            Points = new()
            {
                new MepPointInput { X = 0, Y = 0 },
                new MepPointInput { X = 1000, Y = 0 },
                new MepPointInput { X = 1000, Y = 0, Z = 9000 }
            }
        };

        var points = spec.ResolvePoints(levelElevationMm: 4000);

        Assert.True(spec.NeedsLevelElevation);
        Assert.Equal(6800, points[0].Z);
        Assert.Equal(6800, points[1].Z);
        Assert.Equal(9000, points[2].Z);
    }

    [Fact]
    public void StandardSizes_AreCheckedAndTheNearestOffered()
    {
        var standard = new[] { 16.0, 20, 25, 32, 40, 50 };
        Assert.True(MepSizeMath.IsStandard(25, standard));
        Assert.True(MepSizeMath.IsStandard(25.03, standard));
        Assert.False(MepSizeMath.IsStandard(27, standard));
        Assert.Equal(new[] { 25.0, 32, 20 }, MepSizeMath.Nearest(27, standard));
    }
}
