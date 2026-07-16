using RevitMCP.Addin.Documentation.Placement;
using Xunit;

namespace RevitMCP.Tests;

public class PlaceViewsNameMatcherTests
{
    [Theory]
    [InlineData("1. Korruse", "01_1.korrus")]
    [InlineData("Helindus 2", "02_2. korrus Helindussüsteem")]
    [InlineData("Katus", "EL KATUSE PLAAN")]
    public void CalculateMatchScore_AcceptsNumberAndWordPrefixMatches(string sheetName, string viewName)
    {
        Assert.True(PlaceViewsNameMatcher.CalculateMatchScore(sheetName, viewName) > 0);
    }

    [Theory]
    [InlineData("1. Korruse", "10_10.korrus")]
    [InlineData("1. Korruse", "11_Katus")]
    [InlineData("-1. Korruse", "1. korrus")]
    public void CalculateMatchScore_RejectsConflictingNumbers(string sheetName, string viewName)
    {
        Assert.Equal(0, PlaceViewsNameMatcher.CalculateMatchScore(sheetName, viewName));
    }

    [Fact]
    public void CalculateMatchScore_RejectsNamesWithoutSharedEvidence()
    {
        Assert.Equal(0, PlaceViewsNameMatcher.CalculateMatchScore("Ventilatsioon", "Elektri peakilp"));
    }

    [Theory]
    [InlineData("Esimese korruse plaan", "01_1.korrus plaan")]
    [InlineData("Teine korrus", "2. korruse plaan")]
    [InlineData("Kolmanda korruse plaan", "03_3.korrus plaan")]
    [InlineData("Neljanda korruse plaan", "4. korrus plaan")]
    [InlineData("Kümnenda korruse plaan", "10. korrus plaan")]
    [InlineData("Üheteistkümnenda korruse plaan", "11. korrus plaan")]
    [InlineData("Kahekümne esimese korruse plaan", "21. korrus plaan")]
    [InlineData("Üheksakümne üheksas korrus", "99. korruse plaan")]
    public void CalculateMatchScore_MapsEstonianFloorOrdinalsToDigits(string sheetName, string viewName)
    {
        Assert.True(PlaceViewsNameMatcher.CalculateMatchScore(sheetName, viewName) > 0);
    }

    [Theory]
    [InlineData("Teise korruse plaan", "3. korrus plaan")]
    [InlineData("Kolmanda korruse plaan", "13. korrus plaan")]
    [InlineData("Kahekümne esimese korruse plaan", "22. korrus plaan")]
    public void CalculateMatchScore_RejectsConflictingEstonianFloorOrdinals(string sheetName, string viewName)
    {
        Assert.Equal(0, PlaceViewsNameMatcher.CalculateMatchScore(sheetName, viewName));
    }
}
