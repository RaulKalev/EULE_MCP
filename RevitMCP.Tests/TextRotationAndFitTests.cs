using RevitMCP.Addin.Annotation;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>Rotating text notes (absolute angle in the view) and fitting a note's box to its text.</summary>
public class TextRotationAndFitTests
{
    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 90)]
    [InlineData(-1, 0, 180)]
    [InlineData(0, -1, 270)]
    [InlineData(0.7071, 0.7071, 45)]
    [InlineData(0, 0, 0)]
    public void AngleInView(double right, double up, double expected) =>
        Assert.Equal(expected, TextRotationMath.AngleInViewDeg(right, up), 2);

    [Theory]
    [InlineData(0, 90, 90)]
    [InlineData(90, 0, -90)]
    [InlineData(350, 10, 20)]
    [InlineData(10, 350, -20)]
    [InlineData(0, 180, 180)]
    [InlineData(45, 45, 0)]
    public void TurnIsTheShortestWay(double current, double target, double expected) =>
        Assert.Equal(expected, TextRotationMath.TurnDeg(current, target), 6);

    [Theory]
    [InlineData(-90, 270)]
    [InlineData(360, 0)]
    [InlineData(725, 5)]
    public void NormalizesDegrees(double input, double expected) =>
        Assert.Equal(expected, TextRotationMath.Normalize(input), 6);

    /// <summary>A fake Revit layout: a note whose two paragraphs are 42 and 30 units long, wrapping per word of 6 units.</summary>
    private static double FakeHeight(double width)
    {
        var lines = 0;
        foreach (var paragraph in new[] { 42.0, 30.0 })
            lines += width >= paragraph ? 1 : (int)Math.Ceiling(paragraph / Math.Max(6, Math.Floor(width / 6) * 6));
        return lines * 4.0;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    [InlineData(50)]
    [InlineData(500)]
    public void FitFindsTheNarrowestWidthWithoutExtraLines(double hint)
    {
        var (width, layouts) = TextFitWidthSearch.Find(2, 1000, hint, FakeHeight, 0.25);
        Assert.InRange(width, 42, 42.25);
        Assert.Equal(FakeHeight(1000), FakeHeight(width));
        Assert.True(layouts < 30, $"{layouts} layouts");
    }

    [Fact]
    public void FitShrinksAnOverWideBoxToTheMinimumWhenTextIsShort()
    {
        var (width, _) = TextFitWidthSearch.Find(5, 1000, 0, _ => 4.0, 0.25);
        Assert.Equal(5, width);
    }

    [Fact]
    public void AGoodHintNeedsFewLayouts()
    {
        var (_, withHint) = TextFitWidthSearch.Find(2, 1000, 42, FakeHeight, 0.25);
        var (_, withoutHint) = TextFitWidthSearch.Find(2, 1000, 0, FakeHeight, 0.25);
        Assert.True(withHint < withoutHint, $"{withHint} vs {withoutHint}");
    }

    [Theory]
    [InlineData(2000, 100, true)]
    [InlineData(5000, 50, true)]
    [InlineData(60, 100, false)]
    [InlineData(250, 100, false)]
    [InlineData(2000, 1, false)]
    public void ModelWidthsPassedAsPaperWidthsAreFlagged(double widthMm, int scale, bool flagged) =>
        Assert.Equal(flagged, TextWidthUnits.LooksLikeModelWidth(widthMm, scale));

    [Fact]
    public void RotationOrFitAloneIsAValidEdit()
    {
        var rotation = TextNoteEditPlanner.ParseOptions(null, null, null, null, null, null, false, false, null, 0, false,
            fitWidth: false, rotationDeg: -90, out var error);
        Assert.Null(error);
        Assert.Equal(270, rotation!.RotationDeg);
        Assert.True(rotation.ChangesRotation);
        Assert.False(rotation.ChangesWidth);

        var fit = TextNoteEditPlanner.ParseOptions(null, null, null, null, null, null, false, false, null, 0, false,
            fitWidth: true, rotationDeg: null, out error);
        Assert.Null(error);
        Assert.True(fit!.ChangesWidth);
        Assert.True(fit.FitWidth);
    }

    [Theory]
    [InlineData(30, false, true)]
    [InlineData(0, true, true)]
    public void OnlyOneWidthRule(double widthMm, bool autoWidth, bool fitWidth)
    {
        var options = TextNoteEditPlanner.ParseOptions(null, null, null, null, null, null, false, false, null, widthMm, autoWidth,
            fitWidth, null, out var error);
        Assert.Null(options);
        Assert.Contains("only one of", error);
    }

    [Fact]
    public void NothingToDoIsStillAnError()
    {
        var options = TextNoteEditPlanner.ParseOptions(null, null, null, null, null, null, false, false, null, 0, false,
            false, null, out var error);
        Assert.Null(options);
        Assert.Contains("rotationDeg", error);
    }
}
