using RevitMCP.Addin.Families;
using Xunit;

namespace RevitMCP.Tests;

public class FamilyTypeNamePlannerTests
{
    [Fact]
    public void ComposeCopyName_SingleCopy_AppendsSuffixOnly()
    {
        var name = FamilyTypeNamePlanner.ComposeCopyName("600x600", "", " - Copy", 1, 1);
        Assert.Equal("600x600 - Copy", name);
    }

    [Fact]
    public void ComposeCopyName_MultipleCopies_AppendsCopyNumber()
    {
        Assert.Equal("600x600 - Copy 1", FamilyTypeNamePlanner.ComposeCopyName("600x600", "", " - Copy", 1, 3));
        Assert.Equal("600x600 - Copy 3", FamilyTypeNamePlanner.ComposeCopyName("600x600", "", " - Copy", 3, 3));
    }

    [Fact]
    public void ComposeCopyName_IndexPlaceholder_ReplacesInsteadOfAppending()
    {
        var name = FamilyTypeNamePlanner.ComposeCopyName("Door", "", " V{index}", 2, 3);
        Assert.Equal("Door V2", name);
    }

    [Fact]
    public void ComposeCopyName_IndexPlaceholderWithSingleCopy_StillReplaces()
    {
        var name = FamilyTypeNamePlanner.ComposeCopyName("Door", "T{INDEX}-", "", 1, 1);
        Assert.Equal("T1-Door", name);
    }

    [Fact]
    public void ComposeCopyName_AppliesPrefixAndSuffix()
    {
        var name = FamilyTypeNamePlanner.ComposeCopyName("Standard", "EX-", "-NEW", 1, 1);
        Assert.Equal("EX-Standard-NEW", name);
    }

    [Fact]
    public void ResolveUniqueName_FreeName_IsReturnedUnchanged()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A" };
        Assert.Equal("B", FamilyTypeNamePlanner.ResolveUniqueName("B", taken));
    }

    [Fact]
    public void ResolveUniqueName_TakenName_StartsNumberingAtTwo()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A - Copy" };
        Assert.Equal("A - Copy 2", FamilyTypeNamePlanner.ResolveUniqueName("A - Copy", taken));
    }

    [Fact]
    public void ResolveUniqueName_SkipsEveryTakenSuffix()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A", "A 2", "A 3" };
        Assert.Equal("A 4", FamilyTypeNamePlanner.ResolveUniqueName("A", taken));
    }

    [Fact]
    public void ResolveUniqueName_IsCaseInsensitiveWhenTheSetIs()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a - copy" };
        Assert.Equal("A - Copy 2", FamilyTypeNamePlanner.ResolveUniqueName("A - Copy", taken));
    }

    [Theory]
    [InlineData("600x600")]
    [InlineData("Type (large)")]
    [InlineData("M_Single-Flush 900 x 2100")]
    public void IsValidTypeName_AcceptsOrdinaryNames(string name)
    {
        Assert.True(FamilyTypeNamePlanner.IsValidTypeName(name, out var error), error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsValidTypeName_RejectsEmptyNames(string? name)
    {
        Assert.False(FamilyTypeNamePlanner.IsValidTypeName(name, out var error));
        Assert.Contains("empty", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("A:B")]
    [InlineData("A{B}")]
    [InlineData("A\\B")]
    [InlineData("A|B")]
    [InlineData("A<B>")]
    [InlineData("A?B")]
    [InlineData("A~B")]
    [InlineData("A[B]")]
    [InlineData("A;B")]
    [InlineData("A`B")]
    public void IsValidTypeName_RejectsCharactersRevitForbids(string name)
    {
        Assert.False(FamilyTypeNamePlanner.IsValidTypeName(name, out var error));
        Assert.Contains("does not allow", error);
    }

    [Fact]
    public void IsValidTypeName_RejectsSurroundingWhitespace()
    {
        Assert.False(FamilyTypeNamePlanner.IsValidTypeName(" Padded ", out var error));
        Assert.Contains("whitespace", error, StringComparison.OrdinalIgnoreCase);
    }
}
