using RevitMCP.Addin;
using Xunit;

namespace RevitMCP.Tests;

public class SwallowedTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(10, true)]
    [InlineData(1000, true)]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(11, false)]
    [InlineData(200, false)]
    public void Logs_only_at_powers_of_ten(int count, bool expected)
        => Assert.Equal(expected, Swallowed.IsPowerOfTen(count));

    [Fact]
    public void Counts_each_call_site_and_never_throws()
    {
        for (var i = 0; i < 3; i++) Swallowed.Note(new InvalidOperationException("x"), "SwallowedTests-site.cs", 7);
        Swallowed.Note(null!, "SwallowedTests-null.cs", 1);

        var site = Assert.Single(Swallowed.Snapshot(), entry => entry.File == "SwallowedTests-site.cs");
        Assert.Equal(7, site.Line);
        Assert.Equal(3, site.Count);
    }
}
