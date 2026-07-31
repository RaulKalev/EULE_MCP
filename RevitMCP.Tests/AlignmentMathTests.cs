using RevitMCP.Addin.Placement;
using Xunit;

namespace RevitMCP.Tests;

public class AlignmentMathTests
{
    private static readonly Vec3 Up = new(0, 0, 1);
    private static readonly Vec3 Down = new(0, 0, -1);
    private static readonly Vec3 East = new(1, 0, 0);

    // ── Search directions ────────────────────────────────────────────────────

    [Fact]
    public void SearchDirections_Ceiling_LooksStraightUp()
    {
        var directions = AlignmentMath.SearchDirections(AlignmentMath.SurfaceCeiling, 8, null);
        Assert.Single(directions);
        Assert.Equal(1.0, directions[0].Z, 6);
    }

    [Fact]
    public void SearchDirections_Floor_LooksStraightDown()
    {
        var directions = AlignmentMath.SearchDirections(AlignmentMath.SurfaceFloor, 8, null);
        Assert.Single(directions);
        Assert.Equal(-1.0, directions[0].Z, 6);
    }

    [Fact]
    public void SearchDirections_Wall_IsHorizontalOnly()
    {
        var directions = AlignmentMath.SearchDirections(AlignmentMath.SurfaceWall, 8, null);
        Assert.Equal(8, directions.Count);
        Assert.All(directions, d => Assert.Equal(0.0, d.Z, 6));
    }

    [Fact]
    public void SearchDirections_Wall_LooksBehindAPreferredFacingFirst()
    {
        // A wall-mounted device faces away from its wall, so the first ray must go backwards.
        var directions = AlignmentMath.SearchDirections(AlignmentMath.SurfaceWall, 8, East);
        Assert.Equal(-1.0, directions[0].X, 6);
        Assert.Equal(1.0, directions[1].X, 6);
        Assert.Equal(10, directions.Count);
    }

    [Fact]
    public void SearchDirections_Wall_IgnoresAVerticalPreferredFacing()
    {
        // Straight up has no horizontal component to prefer, so only the ring is searched.
        var directions = AlignmentMath.SearchDirections(AlignmentMath.SurfaceWall, 8, Up);
        Assert.Equal(8, directions.Count);
    }

    [Fact]
    public void SearchDirections_Nearest_CoversHorizontalAndBothVerticals()
    {
        var directions = AlignmentMath.SearchDirections(AlignmentMath.SurfaceNearest, 8, null);
        Assert.Equal(10, directions.Count);
        Assert.Contains(directions, d => d.Z > 0.99);
        Assert.Contains(directions, d => d.Z < -0.99);
    }

    [Fact]
    public void SearchDirections_UnknownSurface_ReturnsNothing()
    {
        Assert.Empty(AlignmentMath.SearchDirections("banana", 8, null));
    }

    [Fact]
    public void HorizontalRing_IsEvenlySpacedUnitVectors()
    {
        var ring = AlignmentMath.HorizontalRing(4);
        Assert.Equal(4, ring.Count);
        Assert.All(ring, d => Assert.Equal(1.0, d.Length, 6));
        Assert.Equal(1.0, ring[0].X, 6);
        Assert.Equal(1.0, ring[1].Y, 6);
        Assert.Equal(-1.0, ring[2].X, 6);
        Assert.Equal(-1.0, ring[3].Y, 6);
    }

    // ── Reach ────────────────────────────────────────────────────────────────

    [Fact]
    public void SupportDistance_AlongAnAxis_IsThatHalfExtent()
    {
        var half = new Vec3(1, 2, 3);
        Assert.Equal(1.0, AlignmentMath.SupportDistance(half, East), 6);
        Assert.Equal(3.0, AlignmentMath.SupportDistance(half, Up), 6);
    }

    [Fact]
    public void SupportDistance_IsTheSameInBothDirectionsAlongAnAxis()
    {
        var half = new Vec3(1, 2, 3);
        Assert.Equal(
            AlignmentMath.SupportDistance(half, Up),
            AlignmentMath.SupportDistance(half, Down),
            6);
    }

    [Fact]
    public void SupportDistance_Diagonally_ReachesTheCorner()
    {
        var half = new Vec3(1, 1, 0);
        var diagonal = new Vec3(1, 1, 0);
        Assert.Equal(Math.Sqrt(2), AlignmentMath.SupportDistance(half, diagonal), 6);
    }

    [Fact]
    public void SupportDistance_NormalizesTheDirection()
    {
        var half = new Vec3(1, 2, 3);
        Assert.Equal(3.0, AlignmentMath.SupportDistance(half, new Vec3(0, 0, 17)), 6);
    }

    // ── Plane normals ────────────────────────────────────────────────────────

    [Fact]
    public void PlaneNormal_HorizontalFace_PointsBackAtTheCaster()
    {
        // Three points on a horizontal slab, probed from below: the normal must point down.
        var normal = AlignmentMath.PlaneNormal(
            new Vec3(0, 0, 3), new Vec3(1, 0, 3), new Vec3(0, 1, 3), Down);

        Assert.NotNull(normal);
        Assert.Equal(-1.0, normal!.Value.Z, 6);
    }

    [Fact]
    public void PlaneNormal_VerticalFace_IsHorizontal()
    {
        var normal = AlignmentMath.PlaneNormal(
            new Vec3(2, 0, 0), new Vec3(2, 1, 0), new Vec3(2, 0, 1), new Vec3(-1, 0, 0));

        Assert.NotNull(normal);
        Assert.Equal(-1.0, normal!.Value.X, 6);
        Assert.Equal(0.0, normal.Value.Z, 6);
    }

    [Fact]
    public void PlaneNormal_CollinearProbes_ReturnsNull()
    {
        var normal = AlignmentMath.PlaneNormal(
            new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(2, 0, 0), Up);

        Assert.Null(normal);
    }

    [Fact]
    public void PlaneNormal_CoincidentProbes_ReturnsNull()
    {
        var point = new Vec3(1, 1, 1);
        Assert.Null(AlignmentMath.PlaneNormal(point, point, point, Up));
    }

    // ── Classification ───────────────────────────────────────────────────────

    [Fact]
    public void ClassifySurface_NormalUp_IsAFloor()
    {
        Assert.Equal(AlignmentMath.SurfaceFloor, AlignmentMath.ClassifySurface(Up, 30));
    }

    [Fact]
    public void ClassifySurface_NormalDown_IsACeiling()
    {
        Assert.Equal(AlignmentMath.SurfaceCeiling, AlignmentMath.ClassifySurface(Down, 30));
    }

    [Fact]
    public void ClassifySurface_HorizontalNormal_IsAWall()
    {
        Assert.Equal(AlignmentMath.SurfaceWall, AlignmentMath.ClassifySurface(East, 30));
    }

    [Fact]
    public void ClassifySurface_SlightlySlopedSlab_IsStillACeiling()
    {
        var tilted = new Vec3(0.2, 0, -1);
        Assert.Equal(AlignmentMath.SurfaceCeiling, AlignmentMath.ClassifySurface(tilted, 30));
    }

    [Fact]
    public void ClassifySurface_LeaningWall_IsStillAWall()
    {
        var leaning = new Vec3(1, 0, 0.3);
        Assert.Equal(AlignmentMath.SurfaceWall, AlignmentMath.ClassifySurface(leaning, 30));
    }

    [Fact]
    public void ClassifySurface_FortyFiveDegreeRamp_IsNeither()
    {
        var ramp = new Vec3(1, 0, 1);
        Assert.Equal(AlignmentMath.SurfaceOther, AlignmentMath.ClassifySurface(ramp, 30));
    }

    [Fact]
    public void ClassifySurface_TightTolerance_RejectsATiltedSlab()
    {
        var tilted = new Vec3(0.2, 0, -1);
        Assert.Equal(AlignmentMath.SurfaceOther, AlignmentMath.ClassifySurface(tilted, 5));
    }

    [Fact]
    public void ClassifySurface_ZeroLengthNormal_IsOther()
    {
        Assert.Equal(AlignmentMath.SurfaceOther, AlignmentMath.ClassifySurface(new Vec3(0, 0, 0), 30));
    }

    [Fact]
    public void ClampAngleTolerance_KeepsTheBandsFromOverlapping()
    {
        Assert.Equal(AlignmentMath.MaxAngleToleranceDegrees, AlignmentMath.ClampAngleTolerance(80));
        Assert.Equal(AlignmentMath.MinAngleToleranceDegrees, AlignmentMath.ClampAngleTolerance(0));
        Assert.Equal(30.0, AlignmentMath.ClampAngleTolerance(30));
    }

    [Fact]
    public void SurfaceSatisfies_NearestAcceptsAnyRecognisedSurface()
    {
        Assert.True(AlignmentMath.SurfaceSatisfies(AlignmentMath.SurfaceNearest, AlignmentMath.SurfaceWall));
        Assert.True(AlignmentMath.SurfaceSatisfies(AlignmentMath.SurfaceNearest, AlignmentMath.SurfaceFloor));
        Assert.False(AlignmentMath.SurfaceSatisfies(AlignmentMath.SurfaceNearest, AlignmentMath.SurfaceOther));
    }

    [Fact]
    public void SurfaceSatisfies_ARequestedWallRejectsACeiling()
    {
        Assert.True(AlignmentMath.SurfaceSatisfies(AlignmentMath.SurfaceWall, AlignmentMath.SurfaceWall));
        Assert.False(AlignmentMath.SurfaceSatisfies(AlignmentMath.SurfaceWall, AlignmentMath.SurfaceCeiling));
    }

    [Theory]
    [InlineData("wall")]
    [InlineData("ceiling")]
    [InlineData("floor")]
    [InlineData("nearest")]
    public void IsKnownSurface_AcceptsTheDocumentedSurfaces(string surface)
    {
        Assert.True(AlignmentMath.IsKnownSurface(surface));
    }

    [Theory]
    [InlineData("other")]
    [InlineData("Wall")]
    [InlineData("")]
    public void IsKnownSurface_RejectsAnythingElse(string surface)
    {
        Assert.False(AlignmentMath.IsKnownSurface(surface));
    }

    // ── Travel ───────────────────────────────────────────────────────────────

    [Fact]
    public void TravelDistance_MovesTheGapMinusTheElementsOwnReach()
    {
        // Surface 10 away, element reaches 2 that way: it has to travel 8 to touch.
        Assert.Equal(8.0, AlignmentMath.TravelDistance(10, 2, 0), 6);
    }

    [Fact]
    public void TravelDistance_LeavesTheRequestedGap()
    {
        Assert.Equal(7.5, AlignmentMath.TravelDistance(10, 2, 0.5), 6);
    }

    [Fact]
    public void TravelDistance_NegativeGapEmbedsTheElement()
    {
        Assert.Equal(8.5, AlignmentMath.TravelDistance(10, 2, -0.5), 6);
    }

    [Fact]
    public void TravelDistance_IsNegativeWhenTheElementAlreadyOvershot()
    {
        // Element reaches 5 but the surface is only 3 away: it has to come back 2.
        Assert.Equal(-2.0, AlignmentMath.TravelDistance(3, 5, 0), 6);
    }

    [Fact]
    public void CurrentGap_IsTheClearanceBeforeAnyMove()
    {
        Assert.Equal(8.0, AlignmentMath.CurrentGap(10, 2), 6);
        Assert.Equal(-2.0, AlignmentMath.CurrentGap(3, 5), 6);
    }

    // ── Rotation ─────────────────────────────────────────────────────────────

    [Fact]
    public void RotationAboutZ_AlreadySquare_IsZero()
    {
        var rotation = AlignmentMath.RotationAboutZ(East, East);
        Assert.NotNull(rotation);
        Assert.Equal(0.0, rotation!.Value, 6);
    }

    [Fact]
    public void RotationAboutZ_QuarterTurn_IsNinetyDegrees()
    {
        var rotation = AlignmentMath.RotationAboutZ(East, new Vec3(0, 1, 0));
        Assert.NotNull(rotation);
        Assert.Equal(Math.PI / 2, rotation!.Value, 6);
    }

    [Fact]
    public void RotationAboutZ_TurnsTheShortWayRound()
    {
        var rotation = AlignmentMath.RotationAboutZ(East, new Vec3(0, -1, 0));
        Assert.NotNull(rotation);
        Assert.Equal(-Math.PI / 2, rotation!.Value, 6);
    }

    [Fact]
    public void RotationAboutZ_IgnoresTheVerticalComponent()
    {
        var rotation = AlignmentMath.RotationAboutZ(new Vec3(1, 0, 5), new Vec3(0, 1, -3));
        Assert.NotNull(rotation);
        Assert.Equal(Math.PI / 2, rotation!.Value, 6);
    }

    [Fact]
    public void RotationAboutZ_VerticalFacing_CannotBeSquaredBySpinning()
    {
        Assert.Null(AlignmentMath.RotationAboutZ(Up, East));
    }

    [Fact]
    public void RotationAboutZ_HorizontalSurfaceNormal_ReturnsNull()
    {
        Assert.Null(AlignmentMath.RotationAboutZ(East, Up));
    }

    // ── Vector primitives ────────────────────────────────────────────────────

    [Fact]
    public void Normalized_ZeroVector_StaysZeroInsteadOfBlowingUp()
    {
        var normalized = new Vec3(0, 0, 0).Normalized();
        Assert.Equal(0.0, normalized.Length, 9);
    }

    [Fact]
    public void CrossProduct_FollowsTheRightHandRule()
    {
        var cross = East.Cross(new Vec3(0, 1, 0));
        Assert.Equal(1.0, cross.Z, 6);
    }
}
