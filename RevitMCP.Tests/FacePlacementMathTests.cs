using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Placement;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>Placing face-based families: reading the request and orienting the family on the face.</summary>
public class FacePlacementMathTests
{
    private static readonly Vec3 Up = new(0, 0, 1);

    private static void AssertVector(Vec3 expected, Vec3 actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
        Assert.Equal(expected.Z, actual.Z, 6);
    }

    // ── Reading placements ───────────────────────────────────────────────────

    [Fact]
    public void Parse_ReadsPointSurfaceAndRotation()
    {
        var placements = FacePlacementMath.Parse(
            JArray.Parse("[{\"x\": 1000, \"y\": 2000, \"z\": 1200, \"mountOn\": \"Wall\", \"rotationDegrees\": 90}]"),
            "", out var error);

        Assert.Null(error);
        var placement = Assert.Single(placements!);
        Assert.Equal(1200, placement.PointMm.Z);
        Assert.Equal("wall", placement.MountOn);
        Assert.Null(placement.Direction);
        Assert.Equal(90, placement.RotationDegrees);
    }

    [Fact]
    public void Parse_UsesTheRequestSurface_WhenAnEntryNamesNone()
    {
        var placements = FacePlacementMath.Parse(
            JArray.Parse("[{\"x\": 0, \"y\": 0, \"z\": 2500}, {\"x\": 0, \"y\": 0, \"z\": 100, \"mountOn\": \"floor\"}]"),
            "ceiling", out var error);

        Assert.Null(error);
        Assert.Equal("ceiling", placements![0].MountOn);
        Assert.Equal("floor", placements[1].MountOn);
    }

    [Fact]
    public void Parse_NormalisesAnExplicitDirection()
    {
        var placements = FacePlacementMath.Parse(
            JArray.Parse("[{\"x\": 0, \"y\": 0, \"z\": 0, \"dx\": 0, \"dy\": -5, \"dz\": 0}]"), "wall", out var error);

        Assert.Null(error);
        AssertVector(new Vec3(0, -1, 0), placements![0].Direction!.Value);
        // A direction replaces the request-level surface rather than combining with it.
        Assert.Equal(string.Empty, placements[0].MountOn);
    }

    [Theory]
    [InlineData("[{\"x\": 1, \"y\": 2, \"mountOn\": \"wall\"}]", "", "x, y and z")]
    [InlineData("[{\"x\": 1, \"y\": 2, \"z\": 3}]", "", "does not say what to mount on")]
    [InlineData("[{\"x\": 1, \"y\": 2, \"z\": 3, \"mountOn\": \"roof\"}]", "", "not one of")]
    [InlineData("[{\"x\": 1, \"y\": 2, \"z\": 3, \"mountOn\": \"wall\", \"dx\": 1}]", "", "both mountOn and a direction")]
    [InlineData("[{\"x\": 1, \"y\": 2, \"z\": 3, \"dx\": 0, \"dy\": 0, \"dz\": 0}]", "", "zero direction")]
    [InlineData("[{\"x\": 1, \"y\": 2, \"z\": 3}]", "sideways", "not one of")]
    [InlineData("[]", "wall", "empty")]
    [InlineData("[7]", "wall", "not a JSON object")]
    public void Parse_RejectsWhatItCannotActOn(string json, string defaultMountOn, string expected)
    {
        var placements = FacePlacementMath.Parse(JArray.Parse(json), defaultMountOn, out var error);

        Assert.Null(placements);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void Parse_AcceptsAJsonString_AndRejectsNothingUseful()
    {
        Assert.NotNull(FacePlacementMath.Parse("[{\"x\":0,\"y\":0,\"z\":0}]", "floor", out _));
        Assert.Null(FacePlacementMath.Parse(null, "floor", out var error));
        Assert.Contains("placements", error);
    }

    [Fact]
    public void Parse_AZeroCoordinateIsARealCoordinate()
    {
        var placements = FacePlacementMath.Parse(JArray.Parse("[{\"x\": 0, \"y\": 0, \"z\": 0}]"), "floor", out var error);

        Assert.Null(error);
        Assert.Equal(0, placements![0].PointMm.Z);
    }

    [Theory]
    [InlineData(double.NaN, FacePlacementMath.DefaultMaxDistanceMm)]
    [InlineData(-5, FacePlacementMath.DefaultMaxDistanceMm)]
    [InlineData(0.01, FacePlacementMath.MinMaxDistanceMm)]
    [InlineData(1e9, FacePlacementMath.MaxMaxDistanceMm)]
    [InlineData(800, 800)]
    public void MaxDistance_IsClamped(double requested, double expected) =>
        Assert.Equal(expected, FacePlacementMath.ClampMaxDistance(requested));

    // ── Orientation on the face ──────────────────────────────────────────────

    [Theory]
    [InlineData(0, -1, 0)]   // a wall facing south
    [InlineData(1, 0, 0)]    // a wall facing east
    [InlineData(-0.6, 0.8, 0)]
    public void OnAWall_TheFamilyStandsUpright(double nx, double ny, double nz)
    {
        var normal = new Vec3(nx, ny, nz);
        var x = FacePlacementMath.ReferenceDirection(normal, 0);

        // Local X lies in the face, horizontally; local Y = normal × X is model up.
        Assert.Equal(0, x.Dot(normal), 9);
        Assert.Equal(0, x.Z, 9);
        AssertVector(Up, normal.Cross(x));
    }

    [Fact]
    public void OnACeilingOrFloor_TheFamilyFollowsModelX()
    {
        AssertVector(new Vec3(1, 0, 0), FacePlacementMath.ReferenceDirection(new Vec3(0, 0, -1), 0));
        AssertVector(new Vec3(1, 0, 0), FacePlacementMath.ReferenceDirection(new Vec3(0, 0, 1), 0));
    }

    [Fact]
    public void Rotation_TurnsAboutTheNormal_CounterClockwiseSeenFromInFront()
    {
        // On a floor (normal up) seen from above, 90° counter-clockwise takes +X to +Y.
        AssertVector(new Vec3(0, 1, 0), FacePlacementMath.ReferenceDirection(new Vec3(0, 0, 1), 90));
        // On a ceiling (normal down) the same turn, seen from below, takes +X to -Y.
        AssertVector(new Vec3(0, -1, 0), FacePlacementMath.ReferenceDirection(new Vec3(0, 0, -1), 90));
        // 180° on a wall turns the family upside down: local X reverses.
        var wall = new Vec3(0, -1, 0);
        AssertVector(FacePlacementMath.ReferenceDirection(wall, 0).Negated(), FacePlacementMath.ReferenceDirection(wall, 180));
    }

    [Fact]
    public void ReferenceDirection_IsAlwaysAUnitVectorInTheFace()
    {
        foreach (var normal in new[] { new Vec3(0, 0, 1), new Vec3(1, 1, 0), new Vec3(0.2, -0.3, 0.9), new Vec3(0, -1, 0.05) })
        foreach (var rotation in new[] { 0.0, 33.0, 90.0, 215.0 })
        {
            var direction = FacePlacementMath.ReferenceDirection(normal, rotation);
            Assert.Equal(1, direction.Length, 9);
            Assert.Equal(0, direction.Dot(normal.Normalized()), 9);
        }
    }

    // ── Which way the face points ────────────────────────────────────────────

    [Fact]
    public void Normal_PrefersTheMeasuredPlane()
    {
        var measured = new Vec3(0, -1, 0);
        AssertVector(measured, FacePlacementMath.ChooseNormal(measured, null, new Vec3(0.3, -0.95, 0)));
    }

    [Fact]
    public void Normal_TakesItsSideFromTheGeometry_WhenTheyAgreeOnThePlane()
    {
        // A point exactly on the face: "towards the caster" cannot tell the sides apart, the face can.
        var measured = new Vec3(0, 1, 0);
        var geometric = new Vec3(0, -1, 0);
        AssertVector(geometric, FacePlacementMath.ChooseNormal(measured, geometric, new Vec3(0, 1, 0)));
    }

    [Fact]
    public void Normal_IgnoresGeometryThatDescribesAnotherPlane()
    {
        // Geometry read in a family's own coordinates can point anywhere; the measured plane wins.
        var measured = new Vec3(0, -1, 0);
        AssertVector(measured, FacePlacementMath.ChooseNormal(measured, new Vec3(1, 0, 0), new Vec3(0, -1, 0)));
    }

    [Fact]
    public void Normal_FallsBackToGeometry_ThenToTheRay()
    {
        var toward = new Vec3(0, -1, 0);
        AssertVector(new Vec3(0, -1, 0), FacePlacementMath.ChooseNormal(null, new Vec3(0, -2, 0), toward));
        // Geometry that is edge-on to the ray is not the face the ray met.
        AssertVector(toward, FacePlacementMath.ChooseNormal(null, new Vec3(1, 0, 0), toward));
        AssertVector(toward, FacePlacementMath.ChooseNormal(null, null, toward));
    }

    // ── Where the family lands ───────────────────────────────────────────────

    [Fact]
    public void ThePointLandsAtTheFootOfThePerpendicular()
    {
        // A wall face in the plane y = 5000, facing south; a point 300 mm in front of it.
        var normal = new Vec3(0, -1, 0);
        var onFace = new Vec3(9999, 5000, 0);
        var point = new Vec3(1200, 4700, 1100);

        AssertVector(new Vec3(1200, 5000, 1100), FacePlacementMath.ProjectOntoPlane(point, onFace, normal));
        Assert.Equal(300, FacePlacementMath.DistanceToPlane(point, onFace, normal), 9);
    }

    [Fact]
    public void APointOnTheFace_StaysWhereItIs()
    {
        var normal = new Vec3(0, 0, -1);
        var point = new Vec3(100, 200, 2700);

        AssertVector(point, FacePlacementMath.ProjectOntoPlane(point, new Vec3(0, 0, 2700), normal));
        Assert.Equal(0, FacePlacementMath.DistanceToPlane(point, new Vec3(0, 0, 2700), normal), 9);
    }

    [Theory]
    [InlineData(true, 1, true)]
    [InlineData(true, 0, false)]
    [InlineData(false, 3, false)]
    public void OnlyAnAtomicBatchWithFailuresIsUndone(bool atomic, int failures, bool expected) =>
        Assert.Equal(expected, FacePlacementMath.ShouldRollBack(atomic, failures));
}
