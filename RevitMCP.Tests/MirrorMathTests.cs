using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Placement;
using Xunit;

namespace RevitMCP.Tests;

public class MirrorMathTests
{
    private static readonly ViewFrame Plan = new(new PointMm(1, 0, 0), new PointMm(0, 1, 0), new PointMm(0, 0, 1));

    /// <summary>A section looking north: right = +X, up = +Z, towards the viewer = -Y.</summary>
    private static readonly ViewFrame SectionNorth = new(new PointMm(1, 0, 0), new PointMm(0, 0, 1), new PointMm(0, -1, 0));

    private static MirrorPlane Resolve(string json, ViewFrame? frame = null, string? viewName = null)
    {
        var request = MirrorMath.ParsePlane(JObject.Parse(json), 0, out var error);
        Assert.Null(error);
        var plane = MirrorMath.Resolve(request!, frame, viewName, out error);
        Assert.Null(error);
        return plane!.Value;
    }

    private static void AssertPoint(PointMm expected, PointMm actual, int precision = 6)
    {
        Assert.Equal(expected.X, actual.X, precision);
        Assert.Equal(expected.Y, actual.Y, precision);
        Assert.Equal(expected.Z, actual.Z, precision);
    }

    [Fact]
    public void LineInAPlan_GivesTheVerticalPlaneThroughTheLine()
    {
        var plane = Resolve("{fromMm: {x: 5000, y: 0}, toMm: {x: 5000, y: 8000}}", Plan, "L2");
        Assert.True(plane.IsVertical);
        Assert.Equal(1.0, Math.Abs(plane.Normal.X), 9);
        Assert.Equal(0.0, plane.Normal.Y, 9);
        Assert.Contains("vertical plane through (5000, 0)–(5000, 8000) in view 'L2'", plane.Description);

        // A device 1 m west of the corridor axis lands 1 m east of it, same Y and Z.
        AssertPoint(new PointMm(6000, 3000, 1200), MirrorMath.Reflect(new PointMm(4000, 3000, 1200), plane));
    }

    [Fact]
    public void LineWithoutAView_IsTakenAsDrawnInPlan()
    {
        var plane = Resolve("{fromMm: {x: 0, y: 0}, toMm: {x: 1000, y: 1000}}");
        Assert.True(plane.IsVertical);
        // A diagonal mirror swaps x and y.
        AssertPoint(new PointMm(300, 100, 50), MirrorMath.Reflect(new PointMm(100, 300, 50), plane));
    }

    [Fact]
    public void LineInASection_ContainsTheViewDirection()
    {
        // A vertical line drawn in a north-facing section at x = 2000: the plane holds Z and the view direction (Y).
        var plane = Resolve("{fromMm: {x: 2000, y: 0, z: 0}, toMm: {x: 2000, y: 0, z: 3000}}", SectionNorth, "S1");
        Assert.True(plane.IsVertical);
        AssertPoint(new PointMm(3000, 750, 900), MirrorMath.Reflect(new PointMm(1000, 750, 900), plane));

        // A horizontal line in the section is a horizontal mirror plane.
        var horizontal = Resolve("{fromMm: {x: 0, y: 0, z: 1500}, toMm: {x: 4000, y: 0, z: 1500}}", SectionNorth, "S1");
        Assert.False(horizontal.IsVertical);
    }

    [Fact]
    public void Line_IsProjectedOntoTheViewPlane()
    {
        // The z of the points does not matter in a plan: the line is what is seen.
        var plane = Resolve("{fromMm: {x: 0, y: 0, z: 0}, toMm: {x: 0, y: 1000, z: 5000}}", Plan);
        Assert.True(plane.IsVertical);
        AssertPoint(new PointMm(-200, 10, 0), MirrorMath.Reflect(new PointMm(200, 10, 0), plane));
    }

    [Fact]
    public void AxisAndPoint_FlipTheNamedCoordinate()
    {
        var x = Resolve("{axis: 'x', throughMm: {x: 1000, y: 0, z: 0}}");
        Assert.True(x.IsVertical);
        AssertPoint(new PointMm(1500, 7, 9), MirrorMath.Reflect(new PointMm(500, 7, 9), x));

        var y = Resolve("{axis: 'Y', throughMm: {x: 0, y: -500, z: 0}}");
        AssertPoint(new PointMm(3, -1500, 9), MirrorMath.Reflect(new PointMm(3, 500, 9), y));

        var z = Resolve("{axis: 'z', throughMm: {x: 0, y: 0, z: 3000}}");
        Assert.False(z.IsVertical);
        AssertPoint(new PointMm(1, 2, 3500), MirrorMath.Reflect(new PointMm(1, 2, 2500), z));
    }

    [Fact]
    public void ExplicitNormal_IsNormalised()
    {
        var plane = Resolve("{pointMm: {x: 0, y: 0, z: 0}, normal: {x: 0, y: 10, z: 0}}");
        Assert.Equal(1.0, plane.Normal.Y, 12);
        AssertPoint(new PointMm(4, -6, 0), MirrorMath.Reflect(new PointMm(4, 6, 0), plane));
    }

    [Fact]
    public void Reflect_TwiceIsTheIdentity()
    {
        var plane = Resolve("{pointMm: {x: 120, y: -40, z: 7}, normal: {x: 1, y: 2, z: 0.5}}");
        var p = new PointMm(1234.5, -678.9, 42);
        AssertPoint(p, MirrorMath.Reflect(MirrorMath.Reflect(p, plane), plane), 6);
    }

    [Fact]
    public void HorizontalPlane_NeedsTheExplicitFlag()
    {
        var horizontal = Resolve("{axis: 'z', throughMm: {x: 0, y: 0, z: 0}}");
        var refusal = MirrorMath.HorizontalPlaneRefusal(horizontal, allowHorizontalPlane: false);
        Assert.NotNull(refusal);
        Assert.Contains("allowHorizontalPlane=true", refusal);
        Assert.Null(MirrorMath.HorizontalPlaneRefusal(horizontal, allowHorizontalPlane: true));

        var tilted = Resolve("{pointMm: {x: 0, y: 0, z: 0}, normal: {x: 1, y: 0, z: 0.2}}");
        Assert.NotNull(MirrorMath.HorizontalPlaneRefusal(tilted, false));

        var vertical = Resolve("{axis: 'x', throughMm: {x: 0, y: 0, z: 0}}");
        Assert.Null(MirrorMath.HorizontalPlaneRefusal(vertical, false));
    }

    [Theory]
    [InlineData("{}", "names no plane")]
    [InlineData("{fromMm: {x: 0, y: 0}}", "both fromMm and toMm")]
    [InlineData("{axis: 'w', throughMm: {x: 0, y: 0}}", "x, y or z")]
    [InlineData("{axis: 'x'}", "throughMm")]
    [InlineData("{pointMm: {x: 0, y: 0}}", "both pointMm and normal")]
    [InlineData("{fromMm: {x: 0, y: 0}, toMm: {x: 1, y: 0}, axis: 'x', throughMm: {x: 0, y: 0}}", "mixes")]
    public void MalformedPlanes_AreRefusedWithAReason(string json, string expected)
    {
        Assert.Null(MirrorMath.ParsePlane(JObject.Parse(json), 0, out var error));
        Assert.Contains(expected, error);
    }

    [Fact]
    public void MissingPlane_ExplainsTheThreeForms()
    {
        Assert.Null(MirrorMath.ParsePlane(null, 0, out var error));
        Assert.Contains("fromMm", error);
        Assert.Contains("axis", error);
        Assert.Contains("normal", error);
    }

    [Fact]
    public void ZeroLengthLine_IsRefused()
    {
        var request = MirrorMath.ParsePlane(JObject.Parse("{fromMm: {x: 5, y: 5}, toMm: {x: 5, y: 5.5}}"), 0, out _);
        Assert.Null(MirrorMath.Resolve(request!, Plan, "L1", out var error));
        Assert.Contains("same point", error);
    }

    [Fact]
    public void ViewId_ComesFromThePlaneOrTheRequest()
    {
        Assert.Equal(77, MirrorMath.ParsePlane(JObject.Parse("{viewId: 77, fromMm: [0, 0], toMm: [1000, 0]}"), 5, out _)!.ViewId);
        Assert.Equal(5, MirrorMath.ParsePlane(JObject.Parse("{fromMm: [0, 0], toMm: [1000, 0]}"), 5, out _)!.ViewId);
        // The plane may also arrive as a JSON string.
        Assert.NotNull(MirrorMath.ParsePlane("{\"fromMm\": {\"x\": 0, \"y\": 0}, \"toMm\": {\"x\": 0, \"y\": 1}}", 0, out _));
    }

    [Fact]
    public void ViewSpecificElements_NeedAPlanePerpendicularToTheirView()
    {
        var vertical = Resolve("{axis: 'x', throughMm: {x: 0, y: 0, z: 0}}");
        Assert.True(MirrorMath.KeepsViewPlane(vertical, Plan));
        var horizontal = Resolve("{axis: 'z', throughMm: {x: 0, y: 0, z: 0}}");
        Assert.False(MirrorMath.KeepsViewPlane(horizontal, Plan));
        Assert.True(MirrorMath.KeepsViewPlane(horizontal, SectionNorth));
    }

    [Fact]
    public void MatchMirrored_PairsByReflectedPosition_ThenByOrder()
    {
        var plane = Resolve("{axis: 'x', throughMm: {x: 0, y: 0, z: 0}}");
        ElementSnapshot Snap(long id, double x, long type = 9) =>
            new() { Id = id, ClassName = "FamilyInstance", Category = "Lighting Fixtures", TypeId = type, Anchor = new PointMm(x, 0, 0) };

        var sources = new[] { Snap(1, -1000), Snap(2, -2000) };
        var created = new[] { Snap(11, 2000), Snap(10, 1000), new ElementSnapshot { Id = 12, ClassName = "IndependentTag", Category = "Tags" } };
        var matches = MirrorMath.MatchMirrored(sources, created, plane);
        Assert.Equal(10, matches[1].Copy.Id);
        Assert.Equal(11, matches[2].Copy.Id);
        Assert.Equal(CopyMapping.Position, matches[1].Mapping);
        Assert.Single(CopyElementsMath.Dependencies(created, matches));

        // No anchors: paired by kind and id order.
        var bare = new[] { new ElementSnapshot { Id = 5, ClassName = "TextNote", TypeId = 3 } };
        var bareCopies = new[] { new ElementSnapshot { Id = 50, ClassName = "TextNote", TypeId = 3 } };
        Assert.Equal(CopyMapping.Single, MirrorMath.MatchMirrored(bare, bareCopies, plane)[5].Mapping);
    }
}
