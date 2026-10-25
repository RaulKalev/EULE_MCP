using RevitMCP.Addin.Documentation.Views;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#104: the pure part of ViewExtentService — box union, padding, boxes in a view frame, 3D orientations.</summary>
public class ViewExtentServiceTests
{
    private static BoxMm B(double x0, double y0, double z0, double x1, double y1, double z1) => new(new Vec3(x0, y0, z0), new Vec3(x1, y1, z1));

    [Fact]
    public void Union_HoldsEveryBox_AndIgnoresMissingOnes()
    {
        var union = ViewExtentMath.Union(new BoxMm?[] { B(0, 0, 0, 1000, 1000, 1000), null, B(-500, 200, 300, 400, 3000, 600) });
        Assert.NotNull(union);
        Assert.Equal(-500, union!.Min.X);
        Assert.Equal(0, union.Min.Y);
        Assert.Equal(0, union.Min.Z);
        Assert.Equal(1000, union.Max.X);
        Assert.Equal(3000, union.Max.Y);
        Assert.Equal(1000, union.Max.Z);
        Assert.Null(ViewExtentMath.Union(new BoxMm?[] { null }));
    }

    [Fact]
    public void Pad_GrowsEverySide()
    {
        var padded = ViewExtentMath.Pad(B(0, 0, 0, 1000, 2000, 3000), 500);
        Assert.Equal(-500, padded.Min.X);
        Assert.Equal(-500, padded.Min.Y);
        Assert.Equal(-500, padded.Min.Z);
        Assert.Equal(1500, padded.Max.X);
        Assert.Equal(2500, padded.Max.Y);
        Assert.Equal(3500, padded.Max.Z);
    }

    [Fact]
    public void NegativePadding_ShrinksButNeverPastTheCentre()
    {
        var shrunk = ViewExtentMath.Pad(B(0, 0, 0, 1000, 4000, 1000), -1000);
        Assert.Equal(500, shrunk.Min.X);
        Assert.Equal(500, shrunk.Max.X);
        Assert.Equal(1000, shrunk.Min.Y);
        Assert.Equal(3000, shrunk.Max.Y);
    }

    [Fact]
    public void InFrame_OfTheIdentity_IsTheBox_AndOfARotatedFrame_TheRotatedExtent()
    {
        var box = B(0, 0, 0, 2000, 1000, 500);
        var same = ViewExtentMath.InFrame(box, FrameMm.Identity);
        Assert.Equal(2000, same.Max.X, 6);
        Assert.Equal(1000, same.Max.Y, 6);

        // A frame turned 90° in plan (right = +Y, up = −X): x extent becomes the y extent.
        var turned = new FrameMm(Vec3.Zero, new Vec3(0, 1, 0), new Vec3(-1, 0, 0), Vec3.UnitZ);
        var local = ViewExtentMath.InFrame(box, turned);
        Assert.Equal(0, local.Min.X, 6);
        Assert.Equal(1000, local.Max.X, 6);
        Assert.Equal(-2000, local.Min.Y, 6);
        Assert.Equal(0, local.Max.Y, 6);
    }

    [Fact]
    public void CalloutCorners_SpanTheBoxInTheParentsRightUpPlane()
    {
        var box = B(1000, 2000, 0, 3000, 5000, 2700);
        var (first, second) = ViewExtentMath.CalloutCorners(box, FrameMm.Identity);
        Assert.Equal(1000, first.X, 6);
        Assert.Equal(2000, first.Y, 6);
        Assert.Equal(3000, second.X, 6);
        Assert.Equal(5000, second.Y, 6);
        Assert.Equal(0, first.Z, 6);

        // A section looking north (right = +X, up = +Z, view direction −Y): the corners span x and z.
        var section = new FrameMm(Vec3.Zero, new Vec3(1, 0, 0), Vec3.UnitZ, new Vec3(0, -1, 0));
        var (a, b) = ViewExtentMath.CalloutCorners(box, section);
        Assert.Equal(1000, a.X, 6);
        Assert.Equal(0, a.Z, 6);
        Assert.Equal(3000, b.X, 6);
        Assert.Equal(2700, b.Z, 6);
    }

    [Fact]
    public void AreaAndVolumeChecks()
    {
        Assert.True(ViewExtentMath.HasPlanArea(B(0, 0, 0, 10, 10, 0)));
        Assert.False(ViewExtentMath.HasVolume(B(0, 0, 0, 10, 10, 0)));
        Assert.False(ViewExtentMath.HasPlanArea(B(0, 0, 0, 0, 10, 10)));
        Assert.True(ViewExtentMath.HasVolume(B(0, 0, 0, 10, 10, 10)));
    }

    [Fact]
    public void Bounds_OfPoints()
    {
        var box = ViewExtentMath.Bounds(new[] { new Vec3(3, -1, 2), new Vec3(-4, 5, 0) });
        Assert.Equal(-4, box!.Min.X);
        Assert.Equal(5, box.Max.Y);
        Assert.Null(ViewExtentMath.Bounds(Array.Empty<Vec3>()));
    }

    [Theory]
    [InlineData("iso-se", -1, 1)]
    [InlineData("iso-ne", -1, -1)]
    [InlineData("iso-nw", 1, -1)]
    [InlineData("iso-sw", 1, 1)]
    public void Presets_LookDownTowardsTheTarget_WithUpPerpendicular(string preset, int sx, int sy)
    {
        var target = new Vec3(1000, 2000, 300);
        var orientation = ViewOrientationMath.Resolve(new OrientationSpec { Preset = preset }, target, 10000, out var error);
        Assert.True(orientation != null, error);
        Assert.Equal(Math.Sign(sx), Math.Sign(orientation!.Forward.X));
        Assert.Equal(Math.Sign(sy), Math.Sign(orientation.Forward.Y));
        Assert.True(orientation.Forward.Z < 0);
        Assert.Equal(0, orientation.Forward.Dot(orientation.Up), 9);
        Assert.Equal(1, orientation.Up.Length, 9);
        Assert.True(orientation.Up.Z > 0);
        // The eye sits back along forward, so looking forward from it reaches the target.
        var toTarget = target.Minus(orientation.EyeMm).Normalized();
        Assert.Equal(1, toTarget.Dot(orientation.Forward), 6);
    }

    [Fact]
    public void Top_LooksStraightDown_WithPlusYUp()
    {
        var orientation = ViewOrientationMath.Resolve(new OrientationSpec { Preset = "top" }, Vec3.Zero, 5000, out _);
        Assert.Equal(-1, orientation!.Forward.Z, 9);
        Assert.Equal(1, orientation.Up.Y, 9);
    }

    [Fact]
    public void CustomOrientation_MakesUpPerpendicular_AndRejectsParallelUp()
    {
        var orientation = ViewOrientationMath.Resolve(
            new OrientationSpec { Forward = new Vec3(1, 0, 0), Up = new Vec3(0.5, 0, 1), EyeMm = new Vec3(-5000, 0, 1000) },
            Vec3.Zero, 1000, out _);
        Assert.Equal(0, orientation!.Up.X, 9);
        Assert.Equal(1, orientation.Up.Z, 9);
        Assert.Equal(-5000, orientation.EyeMm.X);

        Assert.Null(ViewOrientationMath.Resolve(new OrientationSpec { Forward = new Vec3(0, 0, -1), Up = new Vec3(0, 0, 2) }, Vec3.Zero, 1000, out var error));
        Assert.Contains("parallel", error);
    }
}
