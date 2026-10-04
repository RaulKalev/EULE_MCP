using RevitMCP.Addin.Tagging;
using Xunit;

namespace RevitMCP.Tests;

public class TagTargetVisibilityMathTests
{
    private static readonly ViewRangePlaneElevation Top = new("Top", 7500);
    private static readonly ViewRangePlaneElevation Depth = new("View Depth", 3600);

    [Fact]
    public void ElementAboveTop_ReportsZAgainstTop()
    {
        var reason = TagTargetVisibilityMath.DescribeOutsideVerticalRange(7900, 8100, Top, Depth);

        Assert.Equal("outside view range (Z=7900 mm > Top 7500 mm)", reason);
    }

    [Fact]
    public void ElementBelowViewDepth_ReportsTopOfElementAgainstDepth()
    {
        var reason = TagTargetVisibilityMath.DescribeOutsideVerticalRange(2000, 2400.4, Top, Depth);

        Assert.Equal("outside view range (Z=2400 mm < View Depth 3600 mm)", reason);
    }

    [Theory]
    [InlineData(4000, 4200)]   // fully inside
    [InlineData(7000, 7900)]   // straddles Top
    [InlineData(3000, 3600.5)] // touches View Depth
    [InlineData(7500.5, 7600)] // within tolerance of Top
    public void ElementOverlappingRange_IsNotReported(double minZ, double maxZ)
    {
        Assert.Null(TagTargetVisibilityMath.DescribeOutsideVerticalRange(minZ, maxZ, Top, Depth));
    }

    [Fact]
    public void UnlimitedSide_IsNeverReported()
    {
        Assert.Null(TagTargetVisibilityMath.DescribeOutsideVerticalRange(99000, 99100, upper: null, lower: Depth));
        Assert.Null(TagTargetVisibilityMath.DescribeOutsideVerticalRange(-5000, -4000, upper: Top, lower: null));
    }

    [Fact]
    public void PlaneList_UsesHighestAndLowestPlanes()
    {
        var planes = new[]
        {
            new ViewRangePlaneElevation("Cut Plane", 2300),
            new ViewRangePlaneElevation("Top", 3000),
        };

        Assert.Equal(
            "outside view range (Z=3500 mm > Top 3000 mm)",
            TagTargetVisibilityMath.DescribeOutsideVerticalRange(3500, 3600, planes));
        Assert.Null(TagTargetVisibilityMath.DescribeOutsideVerticalRange(2500, 2600, planes));
        Assert.Null(TagTargetVisibilityMath.DescribeOutsideVerticalRange(
            9000, 9100, new[] { new ViewRangePlaneElevation("Top", 3000) }));
    }

    [Theory]
    [InlineData(0, 0, 1, 1, false)]   // inside
    [InlineData(9, 9, 11, 11, false)] // overlapping a corner
    [InlineData(11, 0, 12, 1, true)]  // right of crop
    [InlineData(0, -5, 1, -1, true)]  // below crop
    public void Crop_ChecksFootprintOverlap(double minX, double minY, double maxX, double maxY, bool outside)
    {
        Assert.Equal(outside, TagTargetVisibilityMath.IsOutsideCrop(minX, minY, maxX, maxY, -10, -0.5, 10, 10));
    }

    [Fact]
    public void ComposeReason_ListsSpecificCauses_OrFallsBackToGenericMessage()
    {
        Assert.Equal(
            "Element is not visible or taggable in the source view.",
            TagTargetVisibilityMath.ComposeNotVisibleReason(new string[0], false, 11, "Level 1"));

        Assert.Equal(
            "Element is not visible or taggable in the target view 'Level 2' (ID:22): outside view range (Z=7900 mm > Top 7500 mm); outside the view crop region.",
            TagTargetVisibilityMath.ComposeNotVisibleReason(
                new[] { "outside view range (Z=7900 mm > Top 7500 mm)", "", "outside the view crop region", "outside the view crop region" },
                true,
                22,
                "Level 2"));
    }

    [Theory]
    [InlineData("FloorPlan", true)]
    [InlineData("CeilingPlan", true)]
    [InlineData("Section", true)]
    [InlineData("ThreeD", true)]
    [InlineData("DraftingView", false)]
    [InlineData("Legend", false)]
    [InlineData("Schedule", false)]
    [InlineData("DrawingSheet", false)]
    [InlineData("", false)]
    public void TaggableViewTypes(string viewType, bool expected)
    {
        Assert.Equal(expected, TagTargetVisibilityMath.IsTaggableViewType(viewType));
    }

    [Fact]
    public void ValidateTargetView_ExplainsEachRejection()
    {
        Assert.Null(TagTargetVisibilityMath.ValidateTargetView(5, true, "Level 2", "FloorPlan", false, false));

        Assert.Contains("does not identify a view",
            TagTargetVisibilityMath.ValidateTargetView(5, false, null, null, false, false));
        Assert.Contains("view template",
            TagTargetVisibilityMath.ValidateTargetView(5, true, "Plan template", "FloorPlan", true, false));
        Assert.Contains("DrawingSheet view, which cannot host tags",
            TagTargetVisibilityMath.ValidateTargetView(5, true, "A101", "DrawingSheet", false, false));
        Assert.Contains("unlocked 3D view",
            TagTargetVisibilityMath.ValidateTargetView(5, true, "{3D}", "ThreeD", false, true));
    }
}
