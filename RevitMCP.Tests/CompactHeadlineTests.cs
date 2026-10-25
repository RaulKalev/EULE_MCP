using RevitMCP.Addin.UI;
using RevitMCP.Addin.UI.ViewModels;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>The collapsed connector window's headline and geometry.</summary>
public class CompactHeadlineTests
{
    private static (string Text, HeadlineSeverity Severity, int Count) Compute(
        bool isRunning = true,
        int pendingCount = 0,
        string? jobTitle = null,
        int jobDone = 0,
        int jobTotal = 0,
        bool agentActive = false,
        string? modelTitle = "73226_PP_EN",
        int selectedCount = 0)
        => CompactHeadline.Compute(isRunning, pendingCount, jobTitle, jobDone, jobTotal, agentActive, modelTitle, selectedCount);

    [Fact]
    public void NotRunning_WinsOverEverything()
    {
        var h = Compute(isRunning: false, pendingCount: 3, jobTitle: "Tag sockets", jobTotal: 5, agentActive: true, selectedCount: 2);
        Assert.Equal(("Disconnected", HeadlineSeverity.Error, 0), h);
    }

    [Theory]
    [InlineData(1, "1 waiting for approval")]
    [InlineData(4, "4 waiting for approval")]
    public void Pending_ShowsTheCountAndBeatsJobsAndActivity(int pending, string text)
    {
        var h = Compute(pendingCount: pending, jobTitle: "Tag sockets", jobTotal: 5, agentActive: true);
        Assert.Equal((text, HeadlineSeverity.Warning, pending), h);
    }

    [Fact]
    public void RunningJob_ShowsProgress()
    {
        Assert.Equal(("Tag sockets · 3/14", HeadlineSeverity.Info, 0), Compute(jobTitle: "Tag sockets", jobDone: 3, jobTotal: 14, agentActive: true));
    }

    [Fact]
    public void RunningJob_WithoutStepsOmitsTheFraction()
    {
        Assert.Equal(("Tag sockets", HeadlineSeverity.Info, 0), Compute(jobTitle: " Tag sockets ", jobTotal: 0));
        Assert.Equal("Job", Compute(jobTitle: "").Text);
    }

    [Fact]
    public void AgentActive_WhenNothingMoreUrgent()
    {
        Assert.Equal(("Agent working…", HeadlineSeverity.Info, 0), Compute(agentActive: true, selectedCount: 5));
    }

    [Fact]
    public void Idle_ShowsModelAndSelection()
    {
        Assert.Equal(("73226_PP_EN · 2 selected", HeadlineSeverity.Ok, 0), Compute(selectedCount: 2));
        Assert.Equal(("73226_PP_EN", HeadlineSeverity.Ok, 0), Compute(selectedCount: 0));
        Assert.Equal("No document open", Compute(modelTitle: null).Text);
    }

    private static readonly WindowPlacement Area = new(0, 0, 1920, 1040);

    [Fact]
    public void Compact_KeepsTheBottomLeftCorner()
    {
        var strip = WindowPlacementMath.Compact(new WindowPlacement(300, 200, 360, 620), Area, 280, 40);
        Assert.Equal((300d, 780d, 280d, 40d), (strip.Left, strip.Top, strip.Width, strip.Height));
    }

    [Fact]
    public void Compact_StaysInsideTheWorkingArea()
    {
        var rightEdge = WindowPlacementMath.Compact(new WindowPlacement(1800, 1030, 360, 620), Area, 280, 40);
        Assert.Equal((1640d, 1000d), (rightEdge.Left, rightEdge.Top));

        var offScreen = WindowPlacementMath.Compact(new WindowPlacement(-500, -50, 360, 620), Area, 280, 40);
        Assert.Equal((0d, 530d), (offScreen.Left, offScreen.Top));
    }

    [Fact]
    public void Expanded_StandsOnTheStrip()
    {
        var full = WindowPlacementMath.Expanded(new WindowPlacement(300, 780, 280, 40), Area, 360, 620, 320, 400);
        Assert.Equal((300d, 200d, 360d, 620d), (full.Left, full.Top, full.Width, full.Height));

        // A strip dragged to the top edge cannot hold the window above it: it opens downward instead.
        var atTop = WindowPlacementMath.Expanded(new WindowPlacement(300, 0, 280, 40), Area, 360, 620, 320, 400);
        Assert.Equal((300d, 0d), (atTop.Left, atTop.Top));
    }

    [Fact]
    public void Compact_PeekAtTheBottomEdgeGrowsUpward()
    {
        var peek = WindowPlacementMath.Compact(new WindowPlacement(100, 1000, 280, 40), Area, 280, 96);
        Assert.Equal((100d, 944d, 96d), (peek.Left, peek.Top, peek.Height));
    }

    [Fact]
    public void Compact_InvalidAreaKeepsTheCorner()
    {
        var strip = WindowPlacementMath.Compact(new WindowPlacement(10, 20, 360, 620), new WindowPlacement(0, 0, 0, 0), 280, 40);
        Assert.Equal((10d, 600d, 280d, 40d), (strip.Left, strip.Top, strip.Width, strip.Height));
    }
}
