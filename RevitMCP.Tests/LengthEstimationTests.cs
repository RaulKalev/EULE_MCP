using RevitMCP.Addin.Electrical;
using Xunit;

namespace RevitMCP.Tests;

public class LengthEstimationTests
{
    // ── LengthUnitHelper ──────────────────────────────────────────────────────

    [Fact]
    public void ToMeters_OneFootIsApprox0p3048()
    {
        var meters = LengthUnitHelper.ToMeters(1.0);
        Assert.Equal(0.3048, meters, 6);
    }

    [Fact]
    public void ToFeet_OneMetreIsApprox3p28084()
    {
        var feet = LengthUnitHelper.ToFeet(1.0);
        Assert.Equal(3.28084, feet, 4);
    }

    [Fact]
    public void RoundMeters_RoundsToTwoDecimalsDefault()
    {
        var result = LengthUnitHelper.RoundMeters(1.23456789);
        Assert.Equal(1.23, result);
    }

    [Fact]
    public void RoundMeters_RespectsPrecisionArg()
    {
        var result = LengthUnitHelper.RoundMeters(1.23456789, 4);
        Assert.Equal(1.2346, result);
    }

    // ── CircuitLengthEstimator ────────────────────────────────────────────────

    [Fact]
    public void StraightLine_PythagoreanTriangle()
    {
        var dist = CircuitLengthEstimator.StraightLine(0, 0, 0, 3, 4, 0);
        Assert.Equal(5.0, dist, 6);
    }

    [Fact]
    public void StraightLine_SamePoint_IsZero()
    {
        var dist = CircuitLengthEstimator.StraightLine(1, 2, 3, 1, 2, 3);
        Assert.Equal(0.0, dist, 6);
    }

    [Fact]
    public void Manhattan_SimpleCase()
    {
        var dist = CircuitLengthEstimator.Manhattan(0, 0, 0, 3, 4, 0);
        Assert.Equal(7.0, dist, 6);
    }

    [Fact]
    public void Estimate_EmptyElements_ReturnsZero()
    {
        var panel = (1.0, 2.0, 3.0);
        var (raw, _) = CircuitLengthEstimator.Estimate(panel, [], "ManhattanMax");
        Assert.Equal(0.0, raw);
    }

    [Fact]
    public void Estimate_AllMissingLocations_ReturnsZero()
    {
        var panel = (0.0, 0.0, 0.0);
        var elements = new List<(long Id, (double X, double Y, double Z)? Loc)>
        {
            (1, null),
            (2, null)
        };
        var (raw, _) = CircuitLengthEstimator.Estimate(panel, elements, "ManhattanMax");
        Assert.Equal(0.0, raw);
    }

    [Fact]
    public void Estimate_ManhattanMax_ReturnsFarthestElementDistance()
    {
        var panel = (0.0, 0.0, 0.0);
        var elements = new List<(long Id, (double X, double Y, double Z)? Loc)>
        {
            (1, (3.0, 4.0, 0.0)),  // Manhattan = 7
            (2, (1.0, 1.0, 0.0))   // Manhattan = 2
        };
        var (raw, farthestId) = CircuitLengthEstimator.Estimate(panel, elements, "ManhattanMax");
        Assert.Equal(7.0, raw, 6);
        Assert.Equal(1L, farthestId);
    }

    [Fact]
    public void Estimate_StraightLineMax_ReturnsFarthestElementDistance()
    {
        var panel = (0.0, 0.0, 0.0);
        var elements = new List<(long Id, (double X, double Y, double Z)? Loc)>
        {
            (1, (3.0, 4.0, 0.0)),  // Euclidean = 5
            (2, (1.0, 1.0, 0.0))   // Euclidean ≈ 1.414
        };
        var (raw, _) = CircuitLengthEstimator.Estimate(panel, elements, "StraightLineMax");
        Assert.Equal(5.0, raw, 6);
    }

    [Fact]
    public void Estimate_ManhattanSum_SumsAllDistances()
    {
        var panel = (0.0, 0.0, 0.0);
        var elements = new List<(long Id, (double X, double Y, double Z)? Loc)>
        {
            (1, (1.0, 0.0, 0.0)),  // Manhattan = 1
            (2, (2.0, 0.0, 0.0))   // Manhattan = 2
        };
        var (raw, _) = CircuitLengthEstimator.Estimate(panel, elements, "ManhattanSum");
        Assert.Equal(3.0, raw, 6);
    }

    [Fact]
    public void Estimate_StraightLineSum_SumsAllDistances()
    {
        var panel = (0.0, 0.0, 0.0);
        var elements = new List<(long Id, (double X, double Y, double Z)? Loc)>
        {
            (1, (3.0, 4.0, 0.0)),  // 5
            (2, (0.0, 1.0, 0.0))   // 1
        };
        var (raw, _) = CircuitLengthEstimator.Estimate(panel, elements, "StraightLineSum");
        Assert.Equal(6.0, raw, 6);
    }

    [Fact]
    public void Estimate_NearestNeighborPath_SingleElement_PanelToDist()
    {
        var panel = (0.0, 0.0, 0.0);
        var elements = new List<(long Id, (double X, double Y, double Z)? Loc)>
        {
            (1, (3.0, 4.0, 0.0))   // Euclidean 5 from panel
        };
        var (raw, _) = CircuitLengthEstimator.Estimate(panel, elements, "NearestNeighborPath");
        Assert.Equal(5.0, raw, 5);
    }

    [Fact]
    public void Estimate_NearestNeighborPath_TwoElements_IsDeterministic()
    {
        var panel = (0.0, 0.0, 0.0);
        var elements = new List<(long Id, (double X, double Y, double Z)? Loc)>
        {
            (1, (1.0, 0.0, 0.0)),
            (2, (2.0, 0.0, 0.0))
        };
        var (raw1, _) = CircuitLengthEstimator.Estimate(panel, elements, "NearestNeighborPath");
        var (raw2, _) = CircuitLengthEstimator.Estimate(panel, elements, "NearestNeighborPath");
        Assert.Equal(raw1, raw2);
        // path: 0→1 (dist=1) then 1→2 (dist=1) = total 2
        Assert.Equal(2.0, raw1, 6);
    }

    [Fact]
    public void Estimate_SkipsMissingLocations()
    {
        var panel = (0.0, 0.0, 0.0);
        var elements = new List<(long Id, (double X, double Y, double Z)? Loc)>
        {
            (1, null),
            (2, (3.0, 4.0, 0.0))
        };
        var (raw, _) = CircuitLengthEstimator.Estimate(panel, elements, "ManhattanMax");
        Assert.Equal(7.0, raw, 6);
    }
}
