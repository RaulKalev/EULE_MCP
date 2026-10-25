using RevitMCP.Addin.Electrical;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>Batch B of the 2026-10-25 live test: M11, M12, M6, M31 (pure parts).</summary>
public class CircuitCableCheckTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("  ", true)]
    [InlineData("2x2x0,8", false)]
    public void IsMissing_WhenNoCableOrWireTypeResolves(string? name, bool missing)
        => Assert.Equal(missing, CircuitCableCheck.IsMissing(name));

    [Theory]
    [InlineData("MissingWireType", "MissingCableType")]
    [InlineData(" missingwiretype ", "MissingCableType")]
    [InlineData("MissingCableType", "MissingCableType")]
    [InlineData("MissingLoadName", "MissingLoadName")]
    public void NormalizeCheckName_MapsTheLegacyNameOntoOneCheck(string requested, string expected)
        => Assert.Equal(expected, CircuitCableCheck.NormalizeCheckName(requested), ignoreCase: true);

    [Fact]
    public void BothLegacyAndNewNamesAreOneCheck()
    {
        // 464 circuits without a cable used to give 928 issues: one per name.
        var checks = new HashSet<string>(new[] { "MissingCableType", "MissingWireType" }.Select(CircuitCableCheck.NormalizeCheckName),
            StringComparer.OrdinalIgnoreCase);
        Assert.Single(checks);
    }
}

public class LoadNameTemplateRulesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ConnectedElements")]
    [InlineData("connectedelements")]
    public void ConnectedElements_ReadsTheElementBeforeTheCircuit(string? source)
        => Assert.Equal(new[] { LoadNameValueSource.ConnectedElement, LoadNameValueSource.Circuit },
            LoadNameTemplateRules.ReadOrder(source));

    [Fact]
    public void CircuitParameters_ReadsOnlyTheCircuit()
        => Assert.Equal(new[] { LoadNameValueSource.Circuit }, LoadNameTemplateRules.ReadOrder("CircuitParameters"));

    [Theory]
    [InlineData("Category", "Category")]
    [InlineData("category", "Category")]
    [InlineData("Family Name", "Family")]
    [InlineData("Type", "Type")]
    [InlineData("Type Name", "Type")]
    [InlineData("Room Number", null)]
    public void PseudoFields_NameTheElementItself(string placeholder, string? expected)
        => Assert.Equal(expected, LoadNameTemplateRules.PseudoField(placeholder));
}

public class CircuitLengthMethodTests
{
    private static readonly (double X, double Y, double Z) Panel = (0, 0, 0);

    private static readonly List<(long Id, (double X, double Y, double Z)? Loc)> Elements = new()
    {
        (1, (3.0, 4.0, 0.0)),   // straight 5, Manhattan 7
        (2, (1.0, 0.0, 0.0)),   // straight 1, Manhattan 1
        (3, null)
    };

    [Theory]
    [InlineData(null, "ManhattanMax")]
    [InlineData("", "ManhattanMax")]
    [InlineData("nearestneighborpath", "NearestNeighborPath")]
    [InlineData(" StraightLineSum ", "StraightLineSum")]
    public void TryNormalizeMethod_AcceptsKnownNamesInAnyCase(string? input, string expected)
    {
        Assert.True(CircuitLengthEstimator.TryNormalizeMethod(input, out var method));
        Assert.Equal(expected, method);
    }

    [Fact]
    public void UnknownMethod_IsRejectedNotEstimatedAsManhattanMax()
    {
        Assert.False(CircuitLengthEstimator.TryNormalizeMethod("Bogus", out _));
        Assert.Contains("NearestNeighborPath", CircuitLengthEstimator.UnknownMethodMessage("Bogus"));
        Assert.Throws<ArgumentException>(() => CircuitLengthEstimator.Estimate(Panel, Elements, "Bogus"));
    }

    [Theory]
    [InlineData("StraightLineMax", 5.0, 1L)]
    [InlineData("StraightLineSum", 6.0, 1L)]
    [InlineData("ManhattanMax", 7.0, 1L)]
    [InlineData("ManhattanSum", 8.0, 1L)]
    [InlineData("NearestNeighborPath", 1.0 + 4.4721359549995796, 1L)]   // panel → 2 (1), 2 → 1 (√20)
    public void Breakdown_FollowsTheMethodAndAddsUpToTheEstimate(string method, double expected, long farthest)
    {
        var (raw, farthestId) = CircuitLengthEstimator.Estimate(Panel, Elements, method);
        var distances = CircuitLengthEstimator.ElementDistances(Panel, Elements, method);

        Assert.Equal(expected, raw, 6);
        Assert.Equal(farthest, farthestId);
        Assert.Equal(2, distances.Count);   // the element without a location is left out
        var fromBreakdown = method.EndsWith("Sum") ? distances.Sum(d => d.Distance) : distances.Max(d => d.Distance);
        Assert.Equal(raw, fromBreakdown, 9);
    }

    [Fact]
    public void NearestNeighborPath_DistancesAreCumulativeInVisitOrder()
    {
        var distances = CircuitLengthEstimator.ElementDistances(Panel, Elements, "NearestNeighborPath");
        Assert.Equal(new[] { 2L, 1L }, distances.Select(d => d.Id));
        Assert.Equal(1.0, distances[0].Distance, 9);
        Assert.Equal(1.0 + Math.Sqrt(20), distances[1].Distance, 9);
    }
}

public class CableNameMatchingTests
{
    private static readonly CableResistanceProfileCollection Profiles = new()
    {
        Profiles =
        [
            new() { MatchNameContains = "2×2×0.8", RoundTripOhmPerMeter = 0.072 },
            new() { MatchNameContains = "2x0.8 CCA", RoundTripOhmPerMeter = 0.068 }
        ]
    };

    [Theory]
    [InlineData("2x2x0,8", 0.072)]          // decimal comma, plain x (the live-test name)
    [InlineData("JY(St)Y 2x2x0,8 red", 0.072)]
    [InlineData("2 × 2 × 0.8", 0.072)]
    [InlineData("2X0,8 cca", 0.068)]
    public void DecimalCommaAndMultiplicationSign_Match(string cableType, double ohm)
    {
        var match = CableResistanceProfileService.FindMatch(cableType, Profiles);
        Assert.NotNull(match);
        Assert.Equal(ohm, match!.RoundTripOhmPerMeter, 4);
    }

    [Fact]
    public void NormalizeName_LowerCasePointsXAndNoSpaces()
        => Assert.Equal("jy(st)y2x2x0.8", CableResistanceProfileService.NormalizeName("JY(St)Y 2×2×0,8"));

    [Fact]
    public void FallbackWarning_NamesEachUnmatchedTypeOnce()
    {
        var warning = CableResistanceProfileService.FallbackWarning(new[] { "NYM 3x1,5", "", "nym 3x1,5", "Cat6" }, 0.035);
        Assert.NotNull(warning);
        Assert.Contains("'Cat6', 'NYM 3x1,5'", warning);
        Assert.Contains("0.035", warning);
        Assert.Null(CableResistanceProfileService.FallbackWarning(new[] { "", " " }, 0.035));
    }
}
