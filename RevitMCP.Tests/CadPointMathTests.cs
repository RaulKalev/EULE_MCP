using RevitMCP.Addin.CadManagement;
using Xunit;

namespace RevitMCP.Tests;

public class CadPointMathTests
{
    private static CadPoint Point(
        double x, double y, double z = 0,
        string layer = "E-SOCKET",
        string source = CadPointMath.SourceCircle,
        double rotation = 0) =>
        new() { X = x, Y = y, Z = z, Layer = layer, Source = source, RotationDegrees = rotation };

    // ── Merging duplicate marks ──────────────────────────────────────────────

    [Fact]
    public void Merge_LeavesDistinctLocationsAlone()
    {
        var merged = CadPointMath.Merge(new[] { Point(0, 0), Point(1000, 0), Point(0, 1000) }, 1.0);
        Assert.Equal(3, merged.Count);
    }

    [Fact]
    public void Merge_CollapsesMarksOnTopOfEachOther()
    {
        // A block drawn around a circle otherwise yields two points at one location.
        var merged = CadPointMath.Merge(new[] { Point(500, 500), Point(500.4, 500.2) }, 1.0);

        Assert.Single(merged);
        Assert.Equal(2, merged[0].MergedCount);
    }

    [Fact]
    public void Merge_KeepsMarksJustOutsideTheTolerance()
    {
        var merged = CadPointMath.Merge(new[] { Point(0, 0), Point(0, 1.5) }, 1.0);
        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Merge_NeverCombinesAcrossLayers()
    {
        var merged = CadPointMath.Merge(
            new[] { Point(0, 0, layer: "E-SOCKET"), Point(0, 0, layer: "E-SWITCH") }, 10.0);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Merge_KeepsTheBlockSourceAndItsRotation()
    {
        // The circle comes first, but the block is the richer source: it carries the rotation.
        var merged = CadPointMath.Merge(
            new[]
            {
                Point(100, 100, source: CadPointMath.SourceCircle),
                Point(100.2, 100, source: CadPointMath.SourceBlock, rotation: 90)
            },
            1.0);

        Assert.Single(merged);
        Assert.Equal(CadPointMath.SourceBlock, merged[0].Source);
        Assert.Equal(90, merged[0].RotationDegrees);
    }

    [Fact]
    public void Merge_DoesNotDowngradeABlockToACircle()
    {
        var merged = CadPointMath.Merge(
            new[]
            {
                Point(100, 100, source: CadPointMath.SourceBlock, rotation: 45),
                Point(100.2, 100, source: CadPointMath.SourceCircle)
            },
            1.0);

        Assert.Single(merged);
        Assert.Equal(CadPointMath.SourceBlock, merged[0].Source);
        Assert.Equal(45, merged[0].RotationDegrees);
    }

    [Fact]
    public void Merge_ZeroTolerance_KeepsEverything()
    {
        var merged = CadPointMath.Merge(new[] { Point(0, 0), Point(0, 0) }, 0);
        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Merge_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(CadPointMath.Merge(Array.Empty<CadPoint>(), 1.0));
    }

    [Fact]
    public void Merge_ManyCoincidentMarks_CollapseToOne()
    {
        var points = Enumerable.Range(0, 20).Select(i => Point(200 + i * 0.01, 200)).ToList();
        var merged = CadPointMath.Merge(points, 1.0);

        Assert.Single(merged);
        Assert.Equal(20, merged[0].MergedCount);
    }

    // ── Detecting what is already placed ─────────────────────────────────────

    [Fact]
    public void MarkExisting_FlagsLocationsThatAlreadyHaveAnInstance()
    {
        var candidates = new[] { Point(0, 0), Point(5000, 0) };
        var existing = new[] { Point(10, 10) };

        var flags = CadPointMath.MarkExisting(candidates, existing, 50);

        Assert.True(flags[0]);
        Assert.False(flags[1]);
    }

    [Fact]
    public void MarkExisting_IgnoresTheHeightDifference()
    {
        // The DWG marks sit at 0; the placed sockets are at 1100. Same location.
        var candidates = new[] { Point(0, 0, z: 0) };
        var existing = new[] { Point(0, 0, z: 1100) };

        Assert.True(CadPointMath.MarkExisting(candidates, existing, 50)[0]);
    }

    [Fact]
    public void MarkExisting_RespectsTheTolerance()
    {
        var candidates = new[] { Point(0, 0) };
        var existing = new[] { Point(60, 0) };

        Assert.False(CadPointMath.MarkExisting(candidates, existing, 50)[0]);
        Assert.True(CadPointMath.MarkExisting(candidates, existing, 100)[0]);
    }

    [Fact]
    public void MarkExisting_NothingPlacedYet_FlagsNothing()
    {
        var flags = CadPointMath.MarkExisting(new[] { Point(0, 0), Point(1, 1) }, Array.Empty<CadPoint>(), 50);
        Assert.All(flags, f => Assert.False(f));
    }

    [Fact]
    public void MarkExisting_ZeroTolerance_FlagsNothing()
    {
        var flags = CadPointMath.MarkExisting(new[] { Point(0, 0) }, new[] { Point(0, 0) }, 0);
        Assert.False(flags[0]);
    }

    [Fact]
    public void MarkExisting_ReturnsOneFlagPerCandidate()
    {
        var candidates = Enumerable.Range(0, 7).Select(i => Point(i * 1000, 0)).ToList();
        Assert.Equal(7, CadPointMath.MarkExisting(candidates, new[] { Point(0, 0) }, 50).Count);
    }

    // ── Block rotation ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 90)]
    [InlineData(-1, 0, 180)]
    [InlineData(0, -1, 270)]
    public void RotationDegreesFromBasis_ReadsTheBlocksXAxis(double x, double y, double expected)
    {
        Assert.Equal(expected, CadPointMath.RotationDegreesFromBasis(x, y), 6);
    }

    [Fact]
    public void RotationDegreesFromBasis_DegenerateAxis_IsZero()
    {
        Assert.Equal(0.0, CadPointMath.RotationDegreesFromBasis(0, 0));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(360, 0)]
    [InlineData(-90, 270)]
    [InlineData(450, 90)]
    public void Normalize_WrapsIntoASingleTurn(double input, double expected)
    {
        Assert.Equal(expected, CadPointMath.Normalize(input), 6);
    }

    // ── Elevation ────────────────────────────────────────────────────────────

    [Fact]
    public void TryResolveElevation_DwgMode_KeepsTheDrawingHeight()
    {
        Assert.True(CadPointMath.TryResolveElevation(
            CadPointMath.ElevationFromDwg, 2500, null, 0, 0, out var elevation, out _));
        Assert.Equal(2500, elevation);
    }

    [Fact]
    public void TryResolveElevation_LevelMode_AddsTheOffset()
    {
        Assert.True(CadPointMath.TryResolveElevation(
            CadPointMath.ElevationFromLevel, 0, 3000, 1100, 0, out var elevation, out _));
        Assert.Equal(4100, elevation);
    }

    [Fact]
    public void TryResolveElevation_LevelMode_WithoutALevel_Fails()
    {
        Assert.False(CadPointMath.TryResolveElevation(
            CadPointMath.ElevationFromLevel, 0, null, 1100, 0, out _, out var error));
        Assert.Contains("levelName", error);
    }

    [Fact]
    public void TryResolveElevation_ExplicitMode_UsesTheGivenHeight()
    {
        Assert.True(CadPointMath.TryResolveElevation(
            CadPointMath.ElevationExplicit, 0, 3000, 500, 1200, out var elevation, out _));
        Assert.Equal(1200, elevation);
    }

    [Fact]
    public void TryResolveElevation_UnknownMode_Fails()
    {
        Assert.False(CadPointMath.TryResolveElevation(
            "guess", 0, null, 0, 0, out _, out var error));
        Assert.Contains("dwg, level, explicit", error);
    }

    [Theory]
    [InlineData("dwg")]
    [InlineData("level")]
    [InlineData("explicit")]
    public void IsKnownElevationMode_AcceptsTheDocumentedModes(string mode)
    {
        Assert.True(CadPointMath.IsKnownElevationMode(mode));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Level")]
    [InlineData("auto")]
    public void IsKnownElevationMode_RejectsAnythingElse(string mode)
    {
        Assert.False(CadPointMath.IsKnownElevationMode(mode));
    }

    // ── Flat drawings ────────────────────────────────────────────────────────

    [Fact]
    public void IsFlat_AllMarksAtOneHeight_IsFlat()
    {
        // The signal that the drawing carries no mounting height and the user has to be asked.
        Assert.True(CadPointMath.IsFlat(new[] { Point(0, 0, 0), Point(1000, 0, 0) }));
    }

    [Fact]
    public void IsFlat_DifferingHeights_IsNotFlat()
    {
        Assert.False(CadPointMath.IsFlat(new[] { Point(0, 0, 0), Point(1000, 0, 2500) }));
    }

    [Fact]
    public void IsFlat_NoPoints_IsFlat()
    {
        Assert.True(CadPointMath.IsFlat(Array.Empty<CadPoint>()));
    }

    [Fact]
    public void IsFlat_SubMillimetreScatter_IsStillFlat()
    {
        Assert.True(CadPointMath.IsFlat(new[] { Point(0, 0, 0), Point(0, 0, 0.4) }));
    }

    [Theory]
    [InlineData("block")]
    [InlineData("point")]
    [InlineData("circle")]
    public void IsKnownSource_AcceptsTheDocumentedSources(string source)
    {
        Assert.True(CadPointMath.IsKnownSource(source));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("Block")]
    [InlineData("")]
    public void IsKnownSource_RejectsAnythingElse(string source)
    {
        Assert.False(CadPointMath.IsKnownSource(source));
    }
}
