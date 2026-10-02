using RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;
using Xunit;

namespace RevitMCP.Tests;

public class FootprintCurveChainerTests
{
    private const double Tol = 0.01;

    private static PlanSegment S(double x1, double y1, double x2, double y2) => new(x1, y1, x2, y2);

    [Fact]
    public void Rectangle_InAnyOrderAndDirection_ChainsIntoOneLoop()
    {
        var loops = FootprintCurveChainer.ClosedLoops(new[]
        {
            S(10, 0, 10, 5),
            S(0, 0, 10, 0),
            S(0, 5, 0, 0),
            S(0, 5, 10, 5)      // reversed relative to the others
        }, Tol);

        var loop = Assert.Single(loops);
        Assert.Equal(4, loop.Count);
        Assert.Equal(50, FootprintCurveChainer.Area(loop), 6);
    }

    [Fact]
    public void SmallGaps_WithinTolerance_StillClose()
    {
        var loops = FootprintCurveChainer.ClosedLoops(new[]
        {
            S(0, 0, 10, 0), S(10.004, 0, 10, 5), S(10, 5, 0, 5), S(0, 5.003, 0, 0)
        }, Tol);
        Assert.Single(loops);
    }

    [Fact]
    public void OpenChain_IsDropped()
    {
        Assert.Empty(FootprintCurveChainer.ClosedLoops(new[] { S(0, 0, 10, 0), S(10, 0, 10, 5), S(10, 5, 0, 5) }, Tol));
    }

    [Fact]
    public void DuplicateAndZeroLengthSegments_AreIgnored()
    {
        var loops = FootprintCurveChainer.ClosedLoops(new[]
        {
            S(0, 0, 4, 0), S(4, 0, 0, 0), S(4, 0, 4, 4), S(4, 4, 0, 4), S(0, 4, 0, 0), S(2, 2, 2, 2)
        }, Tol);
        Assert.Equal(16, FootprintCurveChainer.Area(Assert.Single(loops)), 6);
    }

    [Fact]
    public void TwoLoops_LargestIsPicked()
    {
        // Fuajee-like: the real footprint plus a small sliver drawn separately.
        var loops = FootprintCurveChainer.ClosedLoops(new[]
        {
            S(0, 0, 27, 0), S(27, 0, 27, 36), S(27, 36, 0, 36), S(0, 36, 0, 0),
            S(50, 0, 50.8, 0), S(50.8, 0, 50.8, 13.8), S(50.8, 13.8, 50, 13.8), S(50, 13.8, 50, 0)
        }, Tol);
        Assert.Equal(2, loops.Count);
        Assert.Equal(27 * 36, FootprintCurveChainer.Area(FootprintCurveChainer.Largest(loops)!), 6);
    }

    [Fact]
    public void LShape_KeepsAllCorners()
    {
        var loops = FootprintCurveChainer.ClosedLoops(new[]
        {
            S(0, 0, 10, 0), S(10, 0, 10, 4), S(10, 4, 4, 4), S(4, 4, 4, 10), S(4, 10, 0, 10), S(0, 10, 0, 0)
        }, Tol);
        var loop = Assert.Single(loops);
        Assert.Equal(6, loop.Count);
        Assert.Equal(10 * 4 + 4 * 6, FootprintCurveChainer.Area(loop), 6);
    }
}
