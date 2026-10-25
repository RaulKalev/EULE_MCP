using RevitMCP.Addin.Parameters;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#102: friendly data type and parameter group names and their SpecTypeId / GroupTypeId identities.</summary>
public class ParameterSpecMapTests
{
    [Theory]
    [InlineData("text", "String.Text")]
    [InlineData("Text", "String.Text")]
    [InlineData("string", "String.Text")]
    [InlineData("integer", "Int.Integer")]
    [InlineData("number", "Number")]
    [InlineData("length", "Length")]
    [InlineData("area", "Area")]
    [InlineData("yesNo", "Boolean.YesNo")]
    [InlineData("yes/no", "Boolean.YesNo")]
    [InlineData("material", "Reference.Material")]
    [InlineData("String.Text", "String.Text")]
    [InlineData("voltage", "ElectricalPotential")]
    public void ResolvesFriendlyNames(string value, string key)
    {
        var resolved = ParameterSpecMap.ResolveSpec(value);
        Assert.NotNull(resolved);
        Assert.Equal(key, resolved!.Key);
        Assert.Null(resolved.ForgeId);
    }

    [Fact]
    public void EveryFriendlyNameRoundTrips()
    {
        foreach (var friendly in ParameterSpecMap.FriendlySpecNames)
        {
            var key = ParameterSpecMap.ResolveSpec(friendly)!.Key;
            Assert.Equal(friendly, ParameterSpecMap.FriendlyOfKey(key));
        }
        Assert.Equal(ParameterSpecMap.SpecKeys.Count, ParameterSpecMap.SpecKeys.Distinct().Count());
    }

    [Fact]
    public void RawForgeIdsPassThrough()
    {
        var resolved = ParameterSpecMap.ResolveSpec("autodesk.spec.aec:length-2.0.0");
        Assert.NotNull(resolved);
        Assert.Null(resolved!.Key);
        Assert.Equal("autodesk.spec.aec:length-2.0.0", resolved.ForgeId);
        Assert.Equal("length", resolved.Friendly);
        Assert.Equal("text", ParameterSpecMap.FriendlyOfForgeId("autodesk.spec:spec.string-2.0.0"));
        Assert.Equal("yesNo", ParameterSpecMap.FriendlyOfForgeId("autodesk.spec:spec.bool-1.0.0"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("colour")]
    [InlineData("autodesk spec")]
    public void UnknownNames_AreNull(string value) => Assert.Null(ParameterSpecMap.ResolveSpec(value));

    [Fact]
    public void SameType_IgnoresTheVersion()
    {
        Assert.True(ParameterSpecMap.SameType("autodesk.spec.aec:length-2.0.0", "autodesk.spec.aec:length-1.0.0"));
        Assert.False(ParameterSpecMap.SameType("autodesk.spec.aec:length-2.0.0", "autodesk.spec.aec:area-2.0.0"));
        Assert.False(ParameterSpecMap.SameType(null, "autodesk.spec.aec:area-2.0.0"));
        Assert.Equal("autodesk.spec.aec:length", ParameterSpecMap.StripVersion("autodesk.spec.aec:length-2.0.0"));
        Assert.Equal("length", ParameterSpecMap.TypeNameOf("autodesk.spec.aec:length-2.0.0"));
    }

    [Theory]
    [InlineData("Data", "Data")]
    [InlineData("Identity Data", "IdentityData")]
    [InlineData("identity_data", "IdentityData")]
    [InlineData("IdentityData", "IdentityData")]
    [InlineData("Electrical", "Electrical")]
    [InlineData("Electrical - Loads", "ElectricalLoads")]
    [InlineData("Text", "Text")]
    [InlineData("Dimensions", "Geometry")]
    [InlineData("Other", "")]
    public void ResolvesGroupLabels(string value, string key) => Assert.Equal(key, ParameterSpecMap.ResolveGroupKey(value));

    [Fact]
    public void GroupLabelsRoundTrip()
    {
        foreach (var label in ParameterSpecMap.GroupLabels)
            Assert.Equal(label, ParameterSpecMap.LabelOfGroupKey(ParameterSpecMap.ResolveGroupKey(label)!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Electrics")]
    public void UnknownGroups_AreNull(string value) => Assert.Null(ParameterSpecMap.ResolveGroupKey(value));

    [Fact]
    public void Suggestions_NameCloseSpecs() => Assert.Contains("length", ParameterSpecMap.SuggestSpecs("len"));
}
