using RevitMCP.Addin.Documentation.Views;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>
/// #104: the section box convention, as measured in Revit 2026: Revit looks along the box's +BasisZ from the Min.Z face
/// (cut plane) and reports ViewDirection = −BasisZ. BasisZ is the look direction (left of from→to), BasisY up,
/// BasisX = BasisY × BasisZ.
/// </summary>
public class SectionBoxMathTests
{
    private static SectionBoxMath.Result Box(Vec3 from, Vec3 to, bool flip = false, double bottom = 0, double top = 3000, double depth = 3000)
    {
        var box = SectionBoxMath.Compute(from, to, bottom, top, depth, flip, out var error);
        Assert.True(box != null, error);
        return box!;
    }

    private static void Near(double expected, double actual) => Assert.Equal(expected, actual, 6);

    [Fact]
    public void Transform_IsOrthonormal_ForAnyPlanDirection()
    {
        foreach (var (dx, dy) in new[] { (1.0, 0.0), (0.0, 1.0), (3.0, 4.0), (-2.0, 7.0), (-5.0, -5.0) })
        {
            var box = Box(new Vec3(100, 200, 0), new Vec3(100 + dx * 1000, 200 + dy * 1000, 0));
            Assert.True(SectionBoxMath.IsOrthonormal(box));
            // Right-handed: X × Y = Z.
            var cross = box.BasisX.Cross(box.BasisY);
            Near(box.BasisZ.X, cross.X);
            Near(box.BasisZ.Y, cross.Y);
            Near(box.BasisZ.Z, cross.Z);
        }
    }

    [Fact]
    public void LineAlongX_LooksLeft_ToPlusY()
    {
        // West → east: the section looks north, the case checked in Revit 2026.
        var box = Box(new Vec3(0, 0, 0), new Vec3(10000, 0, 0));
        Near(1, box.BasisY.Z);
        // BasisZ is the look direction: left of from→to.
        Near(1, box.BasisZ.Y);
        Near(1, box.LookDirection.Y);
        // Revit reports ViewDirection towards the viewer, who stands south of the far clip looking north.
        Near(-1, box.ExpectedViewDirection.Y);
        // BasisX = Y × Z runs against the line: the view's right is west, so from is on the right.
        Near(-1, box.BasisX.X);
    }

    [Fact]
    public void Flip_ReversesTheViewDirection_AndKeepsTheFrameRightHanded()
    {
        var plain = Box(new Vec3(0, 0, 0), new Vec3(10000, 0, 0));
        var flipped = Box(new Vec3(0, 0, 0), new Vec3(10000, 0, 0), flip: true);
        Near(-plain.BasisZ.Y, flipped.BasisZ.Y);
        Near(-plain.BasisX.X, flipped.BasisX.X);
        Near(-1, flipped.LookDirection.Y);
        Assert.True(SectionBoxMath.IsOrthonormal(flipped));
        Assert.True(flipped.Flip);
    }

    [Fact]
    public void MinMax_SpanTheLine_TheElevations_AndTheDepthBeyondTheCutPlane()
    {
        var box = Box(new Vec3(1000, 2000, 500), new Vec3(1000, 8000, 900), bottom: -200, top: 3600, depth: 2500);
        Near(-3000, box.Min.X);
        Near(3000, box.Max.X);
        Near(-200, box.Min.Y);
        Near(3600, box.Max.Y);
        Near(0, box.Min.Z);
        Near(2500, box.Max.Z);
        Near(6000, box.LengthMm);
        // Origin: the midpoint of the line, at elevation 0 so local y is the model elevation.
        Near(1000, box.Origin.X);
        Near(5000, box.Origin.Y);
        Near(0, box.Origin.Z);
    }

    [Fact]
    public void CutPlane_PassesThroughTheLine_AndTheFarClipLiesInTheLookDirection()
    {
        var from = new Vec3(0, 0, 0);
        var to = new Vec3(4000, 3000, 0);
        var box = Box(from, to, depth: 2000);
        // Both end points of the line lie on the cut plane (local z = 0, the Min.Z face).
        Near(0, box.Min.Z);
        Near(0, from.Minus(box.Origin).Dot(box.BasisZ));
        Near(0, to.Minus(box.Origin).Dot(box.BasisZ));
        // The far face is depthMm away along the look direction.
        var farCentre = box.ToModel(new Vec3(0, 0, box.Max.Z));
        Near(2000, farCentre.Minus(box.Origin).Dot(box.LookDirection));
        Assert.Equal(8, box.ModelCorners().Count());
    }

    [Fact]
    public void VerticalLine_IsRejected()
    {
        Assert.Null(SectionBoxMath.Compute(new Vec3(5, 5, 0), new Vec3(5, 5.5, 3000), 0, 3000, 3000, false, out var error));
        Assert.Contains("vertical", error);
    }

    [Fact]
    public void ZeroDepthOrInvertedHeight_IsRejected()
    {
        Assert.Null(SectionBoxMath.Compute(new Vec3(0, 0, 0), new Vec3(1000, 0, 0), 0, 3000, 0, false, out var depthError));
        Assert.Contains("depthMm", depthError);
        Assert.Null(SectionBoxMath.Compute(new Vec3(0, 0, 0), new Vec3(1000, 0, 0), 3000, 1000, 1000, false, out var heightError));
        Assert.Contains("above", heightError);
    }

    [Fact]
    public void VerticalRange_DefaultsToTheLevelBelowAndTheNextLevel()
    {
        var levels = new[] { 0.0, 3600.0, 7200.0 };
        var (bottom, top) = SectionBoxMath.VerticalRange(new Vec3(0, 0, 4000), new Vec3(10, 0, 5000), levels, null, null);
        Assert.Equal(3600, bottom);
        Assert.Equal(7200, top);

        var (topBottom, topTop) = SectionBoxMath.VerticalRange(new Vec3(0, 0, 7300), new Vec3(10, 0, 7300), levels, null, null);
        Assert.Equal(7200, topBottom);
        Assert.Equal(7200 + SectionBoxMath.DefaultHeightMm, topTop);

        var (overrideBottom, overrideTop) = SectionBoxMath.VerticalRange(new Vec3(0, 0, 0), new Vec3(10, 0, 0), levels, -500, 2000);
        Assert.Equal(-500, overrideBottom);
        Assert.Equal(1500, overrideTop);

        // Points just under a level (within 150 mm) count as on it; below the lowest level the lowest is used.
        Assert.Equal(3600, SectionBoxMath.VerticalRange(new Vec3(0, 0, 3500), new Vec3(1, 0, 3500), levels, null, null).Bottom);
        Assert.Equal(0, SectionBoxMath.VerticalRange(new Vec3(0, 0, -2000), new Vec3(1, 0, -2000), levels, null, null).Bottom);
    }
}
