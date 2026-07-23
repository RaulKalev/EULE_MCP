using RevitMCP.Addin.Tagging;
using Xunit;

namespace RevitMCP.Tests;

public class TagCollisionMathTests
{
    [Fact]
    public void RectanglesOverlap_RespectsGapBuffer()
    {
        Assert.False(TagCollisionMath.RectanglesOverlap(
            0, 1, 0, 1,
            1.2, 2.2, 0, 1,
            0.05));

        Assert.True(TagCollisionMath.RectanglesOverlap(
            0, 1, 0, 1,
            1.08, 2.08, 0, 1,
            0.05));
    }

    [Fact]
    public void OverlapArea_ReturnsExactIntersection()
    {
        var area = TagCollisionMath.OverlapArea(
            0, 3, 0, 2,
            2, 4, 1, 3);

        Assert.Equal(1.0, area, 8);
    }

    [Fact]
    public void RadialOffsets_AreDeterministicAndViewPlaneAligned()
    {
        var right = TagCollisionMath.RadialOffset(2.0, 0, 4);
        var up = TagCollisionMath.RadialOffset(2.0, 1, 4);

        Assert.Equal(2.0, right.X, 8);
        Assert.Equal(0.0, right.Y, 8);
        Assert.Equal(0.0, up.X, 8);
        Assert.Equal(2.0, up.Y, 8);
    }
}
