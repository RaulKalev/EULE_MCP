using RevitMCP.Addin.Electrical;
using Xunit;

namespace RevitMCP.Tests;

public class CircuitNumberComparerTests
{
    private static List<string?> Sort(params string?[] numbers) =>
        numbers.OrderBy(n => n, CircuitNumberComparer.Instance).ToList();

    [Fact]
    public void NumericNumbers_SortByValueNotText()
    {
        Assert.Equal(new[] { "1", "2", "3", "10", "11", "20" }, Sort("1", "10", "11", "2", "20", "3"));
    }

    [Fact]
    public void MultiPoleNumbers_SortAfterTheirFirstPole()
    {
        Assert.Equal(new[] { "1", "1,3", "2", "2,4,6", "10" }, Sort("10", "2,4,6", "1,3", "2", "1"));
    }

    [Fact]
    public void Names_SortAfterNumbers_CaseInsensitively()
    {
        Assert.Equal(new[] { "2", "10", "reserv", "Spare", "Spare 2", "Spare 10" },
            Sort("Spare 10", "reserv", "10", "Spare", "2", "Spare 2"));
    }

    [Fact]
    public void PrefixedNumbers_CompareTheirNumericPart()
    {
        Assert.Equal(new[] { "L1-2", "L1-10", "L2-1" }, Sort("L1-10", "L2-1", "L1-2"));
    }

    [Fact]
    public void EmptyAndNull_SortLast()
    {
        var sorted = Sort("", "2", null, "Spare", "1");
        Assert.Equal(new[] { "1", "2", "Spare" }, sorted.Take(3));
        Assert.All(sorted.Skip(3), n => Assert.True(string.IsNullOrEmpty(n)));
    }

    [Fact]
    public void LeadingZeros_CompareByValue_ThenFewerZerosFirst()
    {
        Assert.Equal(new[] { "1", "01", "2", "010" }, Sort("010", "2", "01", "1"));
    }

    [Fact]
    public void VeryLongNumbers_DoNotOverflow()
    {
        Assert.Equal(new[] { "9", "123456789012345678901234567890" }, Sort("123456789012345678901234567890", "9"));
    }

    [Fact]
    public void Comparer_IsConsistent()
    {
        var c = CircuitNumberComparer.Instance;
        Assert.Equal(0, c.Compare("5", "5"));
        Assert.True(c.Compare("2", "10") < 0);
        Assert.True(c.Compare("10", "2") > 0);
        Assert.True(c.Compare(" 2 ", "10") < 0);
    }
}
