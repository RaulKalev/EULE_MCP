using RevitMCP.Addin.Electrical;
using Xunit;

namespace RevitMCP.Tests;

public class CableResistanceProfileTests
{
    // ── FindMatch with built-in defaults ──────────────────────────────────────

    [Fact]
    public void FindMatch_ExactCcaNotation_ReturnsProfile()
    {
        var match = CableResistanceProfileService.FindMatch("2×0.8 CCA");
        Assert.NotNull(match);
        Assert.Equal(0.068, match.RoundTripOhmPerMeter, 4);
    }

    [Fact]
    public void FindMatch_AltCcaNotation_ReturnsProfile()
    {
        var match = CableResistanceProfileService.FindMatch("2x0.8 CCA");
        Assert.NotNull(match);
        Assert.Equal(0.068, match.RoundTripOhmPerMeter, 4);
    }

    [Fact]
    public void FindMatch_CaseInsensitive_Matches()
    {
        var match = CableResistanceProfileService.FindMatch("FE180 2x1.0 red");
        Assert.NotNull(match);
        Assert.Equal(0.035, match.RoundTripOhmPerMeter, 4);
    }

    [Fact]
    public void FindMatch_SubstringInLongerName_Matches()
    {
        var match = CableResistanceProfileService.FindMatch("XX-FE180-PH90-2x1.0");
        Assert.NotNull(match); // FE180 or PH90 will match
    }

    [Fact]
    public void FindMatch_UnknownCableType_ReturnsNull()
    {
        var match = CableResistanceProfileService.FindMatch("CAT6A UTP");
        Assert.Null(match);
    }

    [Fact]
    public void FindMatch_EmptyString_ReturnsNull()
    {
        Assert.Null(CableResistanceProfileService.FindMatch(""));
    }

    [Fact]
    public void FindMatch_WhitespaceOnly_ReturnsNull()
    {
        Assert.Null(CableResistanceProfileService.FindMatch("   "));
    }

    [Fact]
    public void FindMatch_CustomCollection_OverridesDefaults()
    {
        var custom = new CableResistanceProfileCollection
        {
            Profiles =
            [
                new() { MatchNameContains = "CUSTOM", Description = "Test", RoundTripOhmPerMeter = 0.099 }
            ]
        };
        var match = CableResistanceProfileService.FindMatch("CUSTOM-Type-X", custom);
        Assert.NotNull(match);
        Assert.Equal(0.099, match.RoundTripOhmPerMeter, 4);
    }

    [Fact]
    public void FindMatch_CustomCollection_NoMatchInCustom_ReturnsNull()
    {
        var custom = new CableResistanceProfileCollection
        {
            Profiles =
            [
                new() { MatchNameContains = "SPECIAL", Description = "Only special", RoundTripOhmPerMeter = 0.05 }
            ]
        };
        // FE180 won't match because we passed a custom collection that has no FE180
        var match = CableResistanceProfileService.FindMatch("FE180", custom);
        Assert.Null(match);
    }

    [Fact]
    public void FindMatch_EmptyCollection_ReturnsNull()
    {
        var empty = new CableResistanceProfileCollection { Profiles = [] };
        Assert.Null(CableResistanceProfileService.FindMatch("FE180", empty));
    }

    // ── Load (defaults path) ──────────────────────────────────────────────────

    [Fact]
    public void Load_ReturnsNonEmptyCollection()
    {
        // Load will either return the file or the built-in defaults.
        var collection = CableResistanceProfileService.Load();
        Assert.NotNull(collection);
        Assert.NotEmpty(collection.Profiles);
    }

    [Fact]
    public void Load_AllProfilesHavePositiveResistance()
    {
        var collection = CableResistanceProfileService.Load();
        foreach (var p in collection.Profiles)
            Assert.True(p.RoundTripOhmPerMeter > 0,
                $"Profile '{p.MatchNameContains}' must have resistance > 0");
    }

    [Fact]
    public void Load_AllProfilesHaveMatchKey()
    {
        var collection = CableResistanceProfileService.Load();
        foreach (var p in collection.Profiles)
            Assert.False(string.IsNullOrWhiteSpace(p.MatchNameContains),
                "Every profile must have a non-empty MatchNameContains key");
    }
}
