using RevitMCP.Addin.Placement;
using Xunit;

namespace RevitMCP.Tests;

public class ViewAlignmentMathTests
{
    // ── Mode parsing ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("left", ViewAlignmentMath.ModeLeft)]
    [InlineData("LEFT", ViewAlignmentMath.ModeLeft)]
    [InlineData("align left", ViewAlignmentMath.ModeLeft)]
    [InlineData("align-left", ViewAlignmentMath.ModeLeft)]
    [InlineData("centerVertical", ViewAlignmentMath.ModeCenterVertical)]
    [InlineData("vertical_center", ViewAlignmentMath.ModeCenterVertical)]
    [InlineData("centre vertically", ViewAlignmentMath.ModeCenterVertical)]
    [InlineData("middle", ViewAlignmentMath.ModeCenterHorizontal)]
    [InlineData("distribute horizontally", ViewAlignmentMath.ModeDistributeHorizontal)]
    [InlineData("spaceVertical", ViewAlignmentMath.ModeDistributeVertical)]
    public void NormalizeMode_AcceptsTheCommonSpellings(string raw, string expected)
    {
        Assert.Equal(expected, ViewAlignmentMath.NormalizeMode(raw));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("sideways")]
    [InlineData("center")]
    public void NormalizeMode_RejectsAnythingAmbiguous(string? raw)
    {
        // "center" alone never says which axis, so it has to be rejected rather than guessed.
        Assert.Null(ViewAlignmentMath.NormalizeMode(raw));
    }

    [Fact]
    public void NormalizeReference_KeepsFirstAndLastForTheCallerToIndex()
    {
        Assert.Equal("first", ViewAlignmentMath.NormalizeReference("First"));
        Assert.Equal("last", ViewAlignmentMath.NormalizeReference("LAST"));
        Assert.Equal(ViewAlignmentMath.ReferenceAverage, ViewAlignmentMath.NormalizeReference("mean"));
        Assert.Null(ViewAlignmentMath.NormalizeReference("whichever"));
    }

    [Theory]
    [InlineData("gaps", ViewAlignmentMath.SpreadGaps)]
    [InlineData("edges", ViewAlignmentMath.SpreadGaps)]
    [InlineData("centres", ViewAlignmentMath.SpreadCenters)]
    [InlineData("centers", ViewAlignmentMath.SpreadCenters)]
    public void NormalizeSpread_AcceptsBothSpellings(string raw, string expected)
    {
        Assert.Equal(expected, ViewAlignmentMath.NormalizeSpread(raw));
    }

    // ── Axes ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ViewAlignmentMath.ModeLeft, true)]
    [InlineData(ViewAlignmentMath.ModeRight, true)]
    [InlineData(ViewAlignmentMath.ModeCenterVertical, true)]
    [InlineData(ViewAlignmentMath.ModeDistributeHorizontal, true)]
    [InlineData(ViewAlignmentMath.ModeTop, false)]
    [InlineData(ViewAlignmentMath.ModeBottom, false)]
    [InlineData(ViewAlignmentMath.ModeCenterHorizontal, false)]
    [InlineData(ViewAlignmentMath.ModeDistributeVertical, false)]
    public void UsesHorizontalAxis_MapsEachModeToOneAxis(string mode, bool horizontal)
    {
        // Aligning centres onto a vertical line is a horizontal move — the axis is the one that
        // changes, not the one the line runs along.
        Assert.Equal(horizontal, ViewAlignmentMath.UsesHorizontalAxis(mode));
    }

    [Fact]
    public void EveryModeIsRoutedToAnAxisAndClassifiedAsAlignOrDistribute()
    {
        foreach (var mode in ViewAlignmentMath.AllModes)
        {
            Assert.Equal(mode, ViewAlignmentMath.NormalizeMode(mode));
            Assert.Equal(
                mode is ViewAlignmentMath.ModeDistributeHorizontal or ViewAlignmentMath.ModeDistributeVertical,
                ViewAlignmentMath.IsDistribute(mode));
        }
    }

    // ── Which coordinate a mode aligns ───────────────────────────────────────

    [Fact]
    public void AlignedValue_PicksTheEdgeOrTheCentre()
    {
        var extent = new Extent(10, 20);
        Assert.Equal(10, ViewAlignmentMath.AlignedValue(extent, ViewAlignmentMath.ModeLeft));
        Assert.Equal(10, ViewAlignmentMath.AlignedValue(extent, ViewAlignmentMath.ModeBottom));
        Assert.Equal(20, ViewAlignmentMath.AlignedValue(extent, ViewAlignmentMath.ModeRight));
        Assert.Equal(20, ViewAlignmentMath.AlignedValue(extent, ViewAlignmentMath.ModeTop));
        Assert.Equal(15, ViewAlignmentMath.AlignedValue(extent, ViewAlignmentMath.ModeCenterVertical));
        Assert.Equal(15, ViewAlignmentMath.AlignedValue(extent, ViewAlignmentMath.ModeCenterHorizontal));
    }

    [Fact]
    public void Extent_NormalisesAReversedPairAndCollapsesToAPoint()
    {
        var reversed = new Extent(20, 10);
        Assert.Equal(10, reversed.Min);
        Assert.Equal(20, reversed.Max);
        Assert.Equal(10, reversed.Size);

        var point = Extent.Point(7);
        Assert.Equal(0, point.Size);
        Assert.Equal(7, point.Center);
        // A point anchor gives every mode the same answer, which is what "align the tag heads" means.
        Assert.Equal(7, ViewAlignmentMath.AlignedValue(point, ViewAlignmentMath.ModeLeft));
        Assert.Equal(7, ViewAlignmentMath.AlignedValue(point, ViewAlignmentMath.ModeRight));
    }

    // ── Where the common line lands ──────────────────────────────────────────

    private static readonly Extent[] Three =
    {
        new(0, 10),   // left 0,  centre 5,   right 10
        new(4, 6),    // left 4,  centre 5,   right 6
        new(-2, 2)    // left -2, centre 0,   right 2
    };

    [Fact]
    public void ResolveTarget_Extreme_UsesTheOutermostElementInThatDirection()
    {
        Assert.Equal(-2, ViewAlignmentMath.ResolveTarget(Three, ViewAlignmentMath.ModeLeft, ViewAlignmentMath.ReferenceExtreme));
        Assert.Equal(10, ViewAlignmentMath.ResolveTarget(Three, ViewAlignmentMath.ModeRight, ViewAlignmentMath.ReferenceExtreme));
        Assert.Equal(-2, ViewAlignmentMath.ResolveTarget(Three, ViewAlignmentMath.ModeBottom, ViewAlignmentMath.ReferenceExtreme));
        Assert.Equal(10, ViewAlignmentMath.ResolveTarget(Three, ViewAlignmentMath.ModeTop, ViewAlignmentMath.ReferenceExtreme));
    }

    [Fact]
    public void ResolveTarget_Extreme_AveragesForTheCentreModes()
    {
        // There is no "outermost centre", so extreme falls back to the average of the centres.
        var target = ViewAlignmentMath.ResolveTarget(
            Three, ViewAlignmentMath.ModeCenterVertical, ViewAlignmentMath.ReferenceExtreme);
        Assert.Equal((5 + 5 + 0) / 3.0, target, 9);
    }

    [Fact]
    public void ResolveTarget_MinAndMax_IgnoreTheModesDirection()
    {
        // "right, to min" is the leftmost right edge — a legitimate ask, and the opposite of extreme.
        Assert.Equal(2, ViewAlignmentMath.ResolveTarget(Three, ViewAlignmentMath.ModeRight, ViewAlignmentMath.ReferenceMin));
        Assert.Equal(4, ViewAlignmentMath.ResolveTarget(Three, ViewAlignmentMath.ModeLeft, ViewAlignmentMath.ReferenceMax));
    }

    [Fact]
    public void ResolveTarget_Average_MeansTheAverageOfTheAlignedEdge()
    {
        Assert.Equal((0 + 4 - 2) / 3.0, ViewAlignmentMath.ResolveTarget(
            Three, ViewAlignmentMath.ModeLeft, ViewAlignmentMath.ReferenceAverage), 9);
    }

    [Fact]
    public void ResolveTarget_Element_UsesThatElementsEdge()
    {
        Assert.Equal(4, ViewAlignmentMath.ResolveTarget(
            Three, ViewAlignmentMath.ModeLeft, ViewAlignmentMath.ReferenceElement, 1));
        Assert.Equal(2, ViewAlignmentMath.ResolveTarget(
            Three, ViewAlignmentMath.ModeRight, ViewAlignmentMath.ReferenceElement, 2));
    }

    [Fact]
    public void ResolveTarget_Element_ClampsAnOutOfRangeIndex()
    {
        Assert.Equal(-2, ViewAlignmentMath.ResolveTarget(
            Three, ViewAlignmentMath.ModeLeft, ViewAlignmentMath.ReferenceElement, 99));
        Assert.Equal(0, ViewAlignmentMath.ResolveTarget(
            Three, ViewAlignmentMath.ModeLeft, ViewAlignmentMath.ReferenceElement, -5));
    }

    [Fact]
    public void ResolveTarget_RejectsAnEmptySet()
    {
        Assert.Throws<ArgumentException>(() => ViewAlignmentMath.ResolveTarget(
            new Extent[0], ViewAlignmentMath.ModeLeft, ViewAlignmentMath.ReferenceExtreme));
    }

    // ── The slide each element makes ─────────────────────────────────────────

    [Fact]
    public void OffsetToTarget_IsSignedTowardTheTarget()
    {
        Assert.Equal(-2, ViewAlignmentMath.OffsetToTarget(new Extent(0, 10), ViewAlignmentMath.ModeLeft, -2));
        Assert.Equal(6, ViewAlignmentMath.OffsetToTarget(new Extent(-2, 2), ViewAlignmentMath.ModeLeft, 4));
        Assert.Equal(0, ViewAlignmentMath.OffsetToTarget(new Extent(4, 6), ViewAlignmentMath.ModeLeft, 4));
    }

    [Fact]
    public void OffsetToTarget_LeftAlignmentDoesNotResizeAnything()
    {
        // The offset moves the whole extent: the right edge follows the left one by the same amount.
        var extent = new Extent(0, 10);
        var offset = ViewAlignmentMath.OffsetToTarget(extent, ViewAlignmentMath.ModeLeft, -2);
        var moved = new Extent(extent.Min + offset, extent.Max + offset);
        Assert.Equal(-2, moved.Min, 9);
        Assert.Equal(8, moved.Max, 9);
        Assert.Equal(extent.Size, moved.Size, 9);
    }

    // ── Distribute ───────────────────────────────────────────────────────────

    [Fact]
    public void DistributeOffsets_Centers_LeavesTheOutermostTwoWhereTheyAre()
    {
        var extents = new[]
        {
            new Extent(0, 2),     // centre 1
            new Extent(3, 5),     // centre 4  — off the even line
            new Extent(20, 22)    // centre 21
        };

        var offsets = ViewAlignmentMath.DistributeOffsets(
            extents, ViewAlignmentMath.SpreadCenters, null, out var step);

        Assert.Equal(10, step, 9);
        Assert.Equal(0, offsets[0], 9);
        Assert.Equal(7, offsets[1], 9);   // centre 4 → 11
        Assert.Equal(0, offsets[2], 9);
    }

    [Fact]
    public void DistributeOffsets_Centers_IgnoresTheOrderTheyWerePassedIn()
    {
        // Selecting elements bottom-to-top must give the same layout as top-to-bottom.
        var forward = new[] { new Extent(0, 2), new Extent(3, 5), new Extent(20, 22) };
        var shuffled = new[] { new Extent(20, 22), new Extent(0, 2), new Extent(3, 5) };

        var a = ViewAlignmentMath.DistributeOffsets(forward, ViewAlignmentMath.SpreadCenters, null, out _);
        var b = ViewAlignmentMath.DistributeOffsets(shuffled, ViewAlignmentMath.SpreadCenters, null, out _);

        Assert.Equal(a[0], b[1], 9);
        Assert.Equal(a[1], b[2], 9);
        Assert.Equal(a[2], b[0], 9);
    }

    [Fact]
    public void DistributeOffsets_Centers_WithFixedSpacingLaysOutFromTheLowestElement()
    {
        var extents = new[] { new Extent(0, 2), new Extent(3, 5), new Extent(20, 22) };

        var offsets = ViewAlignmentMath.DistributeOffsets(
            extents, ViewAlignmentMath.SpreadCenters, 5.0, out var step);

        Assert.Equal(5.0, step, 9);
        Assert.Equal(0, offsets[0], 9);    // centre 1 stays
        Assert.Equal(2, offsets[1], 9);    // centre 4 → 6
        Assert.Equal(-10, offsets[2], 9);  // centre 21 → 11
    }

    [Fact]
    public void DistributeOffsets_Gaps_EqualisesTheClearSpaceNotTheCentres()
    {
        // Sizes 2, 6 and 2 across a span of 0..22: 12 units of free space, 6 in each of the 2 gaps.
        var extents = new[]
        {
            new Extent(0, 2),
            new Extent(9, 15),
            new Extent(20, 22)
        };

        var offsets = ViewAlignmentMath.DistributeOffsets(
            extents, ViewAlignmentMath.SpreadGaps, null, out var gap);

        Assert.Equal(6, gap, 9);
        Assert.Equal(0, offsets[0], 9);
        Assert.Equal(-1, offsets[1], 9);   // min 9 → 8
        Assert.Equal(0, offsets[2], 9);

        var second = new Extent(extents[1].Min + offsets[1], extents[1].Max + offsets[1]);
        Assert.Equal(6, second.Min - extents[0].Max, 9);
        Assert.Equal(6, extents[2].Min - second.Max, 9);
    }

    [Fact]
    public void DistributeOffsets_Gaps_ReportsANegativeGapWhenTheyDoNotFit()
    {
        // Three 10-wide boxes crammed into a 20-wide span cannot be separated; the gap goes negative
        // rather than the layout silently overflowing.
        var extents = new[]
        {
            new Extent(0, 10),
            new Extent(5, 15),
            new Extent(10, 20)
        };

        ViewAlignmentMath.DistributeOffsets(extents, ViewAlignmentMath.SpreadGaps, null, out var gap);

        Assert.True(gap < 0);
        Assert.Equal(-5, gap, 9);
    }

    [Fact]
    public void DistributeOffsets_Gaps_WithFixedSpacingStacksFromTheLowestEdge()
    {
        var extents = new[] { new Extent(0, 2), new Extent(9, 15), new Extent(20, 22) };

        var offsets = ViewAlignmentMath.DistributeOffsets(
            extents, ViewAlignmentMath.SpreadGaps, 1.0, out var gap);

        Assert.Equal(1.0, gap, 9);
        Assert.Equal(0, offsets[0], 9);
        Assert.Equal(-6, offsets[1], 9);   // min 9 → 3
        Assert.Equal(-10, offsets[2], 9);  // min 20 → 10
    }

    [Fact]
    public void DistributeOffsets_TwoElements_MoveNothingWithoutAFixedSpacing()
    {
        var extents = new[] { new Extent(0, 2), new Extent(20, 22) };

        var offsets = ViewAlignmentMath.DistributeOffsets(
            extents, ViewAlignmentMath.SpreadCenters, null, out var step);

        Assert.Equal(20, step, 9);
        Assert.All(offsets, offset => Assert.Equal(0, offset, 9));
    }

    [Fact]
    public void DistributeOffsets_HandlesDegenerateInput()
    {
        Assert.Empty(ViewAlignmentMath.DistributeOffsets(
            new Extent[0], ViewAlignmentMath.SpreadCenters, null, out _));

        var single = ViewAlignmentMath.DistributeOffsets(
            new[] { new Extent(0, 2) }, ViewAlignmentMath.SpreadCenters, null, out var step);
        Assert.Single(single);
        Assert.Equal(0, single[0]);
        Assert.Equal(0, step);
    }

    [Fact]
    public void DistributeOffsets_CoincidentCentresAreStable()
    {
        // Everything stacked on one spot has no span to spread across; nothing should fly off.
        var extents = new[] { new Extent(0, 2), new Extent(0, 2), new Extent(0, 2) };

        var offsets = ViewAlignmentMath.DistributeOffsets(
            extents, ViewAlignmentMath.SpreadCenters, null, out var step);

        Assert.Equal(0, step, 9);
        Assert.All(offsets, offset => Assert.Equal(0, offset, 9));
    }
}
