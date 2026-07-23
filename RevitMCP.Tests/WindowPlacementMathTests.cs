using RevitMCP.Addin.UI;
using Xunit;

namespace RevitMCP.Tests;

public class WindowPlacementMathTests
{
    private static readonly WindowPlacement AvailableArea = new(-1920, 0, 3840, 1080);

    [Fact]
    public void Normalize_PreservesValidBounds()
    {
        var saved = new WindowPlacement(-1200, 100, 500, 600);

        var result = Normalize(saved);

        Assert.Equal(-1200, result.Left);
        Assert.Equal(100, result.Top);
        Assert.Equal(500, result.Width);
        Assert.Equal(600, result.Height);
    }

    [Fact]
    public void Normalize_ClampsWindowInsideAvailableArea()
    {
        var saved = new WindowPlacement(4000, -500, 500, 600);

        var result = Normalize(saved);

        Assert.Equal(1420, result.Left);
        Assert.Equal(0, result.Top);
    }

    [Fact]
    public void Normalize_EnforcesMinimumSize()
    {
        var saved = new WindowPlacement(100, 100, 50, 75);

        var result = Normalize(saved);

        Assert.Equal(300, result.Width);
        Assert.Equal(350, result.Height);
    }

    [Fact]
    public void Normalize_CapsOversizedWindowToAvailableArea()
    {
        var saved = new WindowPlacement(-5000, -5000, 6000, 2000);

        var result = Normalize(saved);

        Assert.Equal(-1920, result.Left);
        Assert.Equal(0, result.Top);
        Assert.Equal(3840, result.Width);
        Assert.Equal(1080, result.Height);
    }

    [Fact]
    public void Normalize_ReplacesNonFiniteValues()
    {
        var saved = new WindowPlacement(double.NaN, double.PositiveInfinity, double.NaN, double.NegativeInfinity);

        var result = Normalize(saved);

        Assert.Equal(-190, result.Left);
        Assert.Equal(310, result.Top);
        Assert.Equal(380, result.Width);
        Assert.Equal(460, result.Height);
    }

    private static WindowPlacement Normalize(WindowPlacement saved) =>
        WindowPlacementMath.Normalize(saved, AvailableArea, 300, 350, 380, 460);
}
