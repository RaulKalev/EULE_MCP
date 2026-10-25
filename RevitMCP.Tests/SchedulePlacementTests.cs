using RevitMCP.Addin.Schedules;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#103: where a schedule goes on a sheet, and how a placed table is moved onto that point.</summary>
public class SchedulePlacementTests
{
    // A plan viewport on an A1 sheet: 400 × 300 mm, top-left at (50, 550).
    private static readonly SheetRect Viewport = new(50, 250, 450, 550);

    [Fact]
    public void Right_AlignsTopsWithTheGap()
    {
        var topLeft = SchedulePlacement.NextTo(Viewport, SchedulePlacement.Right, 10, 120, 80);
        Assert.Equal((460.0, 550.0), topLeft);
        var box = SchedulePlacement.BoxAt(topLeft, 120, 80);
        Assert.False(box.Overlaps(Viewport));
        Assert.Equal(10, box.MinX - Viewport.MaxX, 6);
        Assert.Equal(Viewport.MaxY, box.MaxY, 6);
    }

    [Fact]
    public void Below_AlignsLeftEdges()
    {
        var topLeft = SchedulePlacement.NextTo(Viewport, SchedulePlacement.Below, 15, 120, 80);
        Assert.Equal((50.0, 235.0), topLeft);
        var box = SchedulePlacement.BoxAt(topLeft, 120, 80);
        Assert.False(box.Overlaps(Viewport));
        Assert.Equal(Viewport.MinX, box.MinX, 6);
    }

    [Fact]
    public void LeftAndAbove_UseTheScheduleSize()
    {
        var left = SchedulePlacement.NextTo(Viewport, SchedulePlacement.Left, 10, 120, 80);
        Assert.Equal((-80.0, 550.0), left);
        Assert.Equal(40, SchedulePlacement.BoxAt(left, 120, 80).MaxX, 6);

        var above = SchedulePlacement.NextTo(Viewport, SchedulePlacement.Above, 10, 120, 80);
        Assert.Equal((50.0, 640.0), above);
        Assert.Equal(560, SchedulePlacement.BoxAt(above, 120, 80).MinY, 6);
    }

    [Fact]
    public void NegativeGap_IsZero()
    {
        var topLeft = SchedulePlacement.NextTo(Viewport, SchedulePlacement.Right, -5, 10, 10);
        Assert.Equal(450, topLeft.X, 6);
    }

    [Fact]
    public void Correction_MovesTheMeasuredBoxOntoTheTarget()
    {
        // Revit put the table somewhere else than planned (e.g. anchored at another corner).
        var actual = new SheetRect(470, 480, 590, 560);
        var (dx, dy) = SchedulePlacement.Correction((460, 550), actual);
        Assert.Equal((-10.0, -10.0), (dx, dy));
        var moved = actual.Offset(dx, dy);
        Assert.Equal(460, moved.MinX, 6);
        Assert.Equal(550, moved.MaxY, 6);
    }

    [Fact]
    public void Centered_TopLeft()
    {
        var topLeft = SchedulePlacement.CenteredTopLeft((420, 297), 100, 60);
        Assert.Equal((370.0, 327.0), topLeft);
    }

    [Fact]
    public void Overlaps_AndInside()
    {
        var a = new SheetRect(0, 0, 100, 100);
        Assert.True(a.Overlaps(new SheetRect(50, 50, 150, 150)));
        Assert.False(a.Overlaps(new SheetRect(100, 0, 200, 100)));          // touching edges do not overlap
        Assert.False(a.Overlaps(new SheetRect(100.3, 0, 200, 100)));
        Assert.Equal(new[] { 0, 2 }, SchedulePlacement.Overlapping(a, new[] { new SheetRect(90, 90, 120, 120), new SheetRect(200, 200, 300, 300), new SheetRect(-10, -10, 10, 10) }));
        Assert.True(new SheetRect(10, 10, 20, 20).Inside(a));
        Assert.False(new SheetRect(90, 90, 120, 120).Inside(a));
        Assert.Equal(100, new SheetRect(100, 0, 0, 50).Width, 6);   // corners in any order
    }

    [Theory]
    [InlineData(null, "right")]
    [InlineData("", "right")]
    [InlineData("Below", "below")]
    [InlineData("under", "below")]
    [InlineData("LEFT", "left")]
    [InlineData("top", "above")]
    public void Sides_Parse(string? raw, string expected)
    {
        Assert.True(SchedulePlacement.TryParseSide(raw, out var side));
        Assert.Equal(expected, side);
    }

    [Fact]
    public void UnknownSide_IsRefused() => Assert.False(SchedulePlacement.TryParseSide("diagonal", out _));
}
