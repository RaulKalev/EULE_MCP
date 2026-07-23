using RevitMCP.Addin.Tagging;
using Xunit;

namespace RevitMCP.Tests;

public class TagTemplateMathTests
{
    private const double Tolerance = 1e-9;

    [Theory]
    [InlineData(1, 0, PlacementSide.Right)]
    [InlineData(-1, 0, PlacementSide.Left)]
    [InlineData(0, 1, PlacementSide.Front)]
    [InlineData(0, -1, PlacementSide.Back)]
    [InlineData(1, 1, PlacementSide.FrontRight)]
    [InlineData(-1, 1, PlacementSide.FrontLeft)]
    [InlineData(1, -1, PlacementSide.BackRight)]
    [InlineData(-1, -1, PlacementSide.BackLeft)]
    public void ClassifyPlacement_RecognizesCardinalAndDiagonalSides(
        double right,
        double front,
        PlacementSide expected)
    {
        var actual = TagTemplateMath.ClassifyPlacement(
            right,
            front,
            0.001,
            2.0);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ClassifyPlacement_PreservesUnevenCustomOffset()
    {
        var actual = TagTemplateMath.ClassifyPlacement(
            250.0,
            500.0,
            1.0,
            5.0);

        Assert.Equal(PlacementSide.Custom, actual);
    }

    [Fact]
    public void ClassifyPlacement_RecognizesCenterWithinTolerance()
    {
        Assert.Equal(
            PlacementSide.Center,
            TagTemplateMath.ClassifyPlacement(
                0.4,
                -0.3,
                1.0,
                10.0));
    }

    [Theory]
    [InlineData(0, 0, 1, 1, 0, 1)]
    [InlineData(90, -1, 0, 0, 1, 1)]
    [InlineData(180, 0, -1, -1, 0, 1)]
    [InlineData(270, 1, 0, 0, -1, 1)]
    [InlineData(37, -0.6018150232, 0.7986355100, 0.7986355100, 0.6018150232, 1)]
    public void Reconstruct_RotatesFrontOffsetWithTargetHost(
        double rotationDegrees,
        double expectedX,
        double expectedY,
        double rightX,
        double rightY,
        double frontOffset)
    {
        var radians = rotationDegrees * Math.PI / 180.0;
        var computedRightX = Math.Cos(radians);
        var computedRightY = Math.Sin(radians);
        var computedFrontX = -Math.Sin(radians);
        var computedFrontY = Math.Cos(radians);

        var actual = TagTemplateMath.Reconstruct(
            0,
            frontOffset,
            computedRightX,
            computedRightY,
            computedFrontX,
            computedFrontY);

        Assert.Equal(expectedX, actual.Right, 8);
        Assert.Equal(expectedY, actual.Front, 8);
        Assert.Equal(rightX, computedRightX, 8);
        Assert.Equal(rightY, computedRightY, 8);
    }

    [Fact]
    public void Reconstruct_UsesMirroredAxesWithoutApplyingAnotherFlip()
    {
        var actual = TagTemplateMath.Reconstruct(
            200,
            500,
            -1,
            0,
            0,
            1);

        Assert.Equal(-200, actual.Right, Tolerance);
        Assert.Equal(500, actual.Front, Tolerance);
    }

    [Theory]
    [InlineData(-1, 0, 0, 1, -200, 500)] // hand-flipped
    [InlineData(1, 0, 0, -1, 200, -500)] // facing-flipped
    [InlineData(-1, 0, 0, -1, -200, -500)] // both axes transformed
    public void Reconstruct_PreservesFamilyFlipStateReportedByAxes(
        double rightX,
        double rightY,
        double frontX,
        double frontY,
        double expectedX,
        double expectedY)
    {
        var actual = TagTemplateMath.Reconstruct(
            200,
            500,
            rightX,
            rightY,
            frontX,
            frontY);

        Assert.Equal(expectedX, actual.Right, Tolerance);
        Assert.Equal(expectedY, actual.Front, Tolerance);
    }

    [Fact]
    public void ProjectAndReconstruct_PreserveArbitraryLocalOffset()
    {
        var radians = 37.0 * Math.PI / 180.0;
        var rightX = Math.Cos(radians);
        var rightY = Math.Sin(radians);
        var frontX = -Math.Sin(radians);
        var frontY = Math.Cos(radians);
        var world = TagTemplateMath.Reconstruct(
            137.5,
            -412.25,
            rightX,
            rightY,
            frontX,
            frontY);

        var local = TagTemplateMath.Project(
            world.Right,
            world.Front,
            rightX,
            rightY,
            frontX,
            frontY);

        Assert.Equal(137.5, local.Right, 8);
        Assert.Equal(-412.25, local.Front, 8);
    }

    [Fact]
    public void InferRotationMode_PrefersViewAlignedForAmbiguousZeroSource()
    {
        Assert.Equal(
            TagRotationMode.KeepViewAligned,
            TagTemplateMath.InferRotationMode(
                0,
                0,
                2 * Math.PI / 180.0));
    }

    [Fact]
    public void InferRotationMode_DetectsFollowHost()
    {
        var angle = 30 * Math.PI / 180.0;

        Assert.Equal(
            TagRotationMode.FollowHost,
            TagTemplateMath.InferRotationMode(
                angle,
                angle + 0.5 * Math.PI / 180.0,
                2 * Math.PI / 180.0));
    }

    [Fact]
    public void InferRotationMode_DetectsRelativeRotation()
    {
        Assert.Equal(
            TagRotationMode.RelativeToHost,
            TagTemplateMath.InferRotationMode(
                45 * Math.PI / 180.0,
                30 * Math.PI / 180.0,
                2 * Math.PI / 180.0));
    }

    [Theory]
    [InlineData(TagRotationMode.KeepViewAligned, 15, 90, 10, 15)]
    [InlineData(TagRotationMode.FollowHost, 15, 90, 10, 90)]
    [InlineData(TagRotationMode.RelativeToHost, 15, 90, 10, 100)]
    public void ResolveTargetRotation_AppliesRequestedMode(
        TagRotationMode mode,
        double sourceTagDegrees,
        double targetHostDegrees,
        double relativeDegrees,
        double expectedDegrees)
    {
        var actual = TagTemplateMath.ResolveTargetRotation(
            mode,
            sourceTagDegrees * Math.PI / 180.0,
            targetHostDegrees * Math.PI / 180.0,
            relativeDegrees * Math.PI / 180.0);

        Assert.Equal(
            expectedDegrees,
            actual * 180.0 / Math.PI,
            8);
    }
}
