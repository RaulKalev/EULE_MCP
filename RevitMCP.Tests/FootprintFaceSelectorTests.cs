using RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;
using Xunit;

namespace RevitMCP.Tests;

public class FootprintFaceSelectorTests
{
    private const double M2 = 1 / 0.09290304;          // ft² per m²
    private const double MinNormalZ = 0.9994;            // cos 2°

    private static FaceCandidate Face(int i, double areaM2, double normalZ, double elevationFt) =>
        new(i, areaM2 * M2, normalZ, elevationFt);

    [Fact]
    public void Fuajee_SliverBelowTheFloor_IsNotChosen()
    {
        // #68: a 74 m² floor at 0 ft with a 1 m² threshold strip 0.1 ft lower, plus the ceiling.
        var faces = new[]
        {
            Face(0, 1.05, -1, -0.1),   // 250 mm x 4200 mm strip, lowest
            Face(1, 73.0, -1, 0.0),    // the real floor
            Face(2, 74.0, +1, 10.0)    // ceiling (upward)
        };
        var choice = FootprintFaceSelector.Choose(faces, MinNormalZ, 0.1 * M2)!;

        Assert.Equal(1, choice.Index);
        Assert.True(choice.FromDownwardFaces);
        Assert.False(choice.IsStepped);
    }

    [Fact]
    public void UpwardFaces_AreIgnored_WhenFloorFacesExist()
    {
        var faces = new[] { Face(0, 20, -1, 0), Face(1, 50, +1, -1) };   // a larger upward face lower down
        Assert.Equal(0, FootprintFaceSelector.Choose(faces, MinNormalZ, 0)!.Index);
    }

    [Fact]
    public void InvertedNormals_FallBackToTheLargestHorizontalFace()
    {
        var faces = new[] { Face(0, 30, +1, 0), Face(1, 5, +1, -0.5) };
        var choice = FootprintFaceSelector.Choose(faces, MinNormalZ, 0)!;
        Assert.Equal(0, choice.Index);
        Assert.False(choice.FromDownwardFaces);
    }

    [Fact]
    public void EqualAreas_TieBreakOnLowestElevation()
    {
        var faces = new[] { Face(0, 40, -1, 1.0), Face(1, 40, -1, 0.0) };
        Assert.Equal(1, FootprintFaceSelector.Choose(faces, MinNormalZ, 0)!.Index);
    }

    [Fact]
    public void SteppedFloor_IsDetected()
    {
        // Aula: 120 m² at 0 ft and a 58 m² stage at +2.6 ft.
        var faces = new[] { Face(0, 120, -1, 0), Face(1, 58, -1, 2.6), Face(2, 178, +1, 16) };
        var choice = FootprintFaceSelector.Choose(faces, MinNormalZ, 0)!;

        Assert.Equal(0, choice.Index);
        Assert.True(choice.IsStepped);
        Assert.Equal(2, choice.FloorLevels);
        Assert.Equal(178 * M2, choice.TotalFloorAreaFt2, 6);
    }

    [Fact]
    public void FloorSplitIntoCoplanarFaces_CountsAsOneLevel()
    {
        var faces = new[] { Face(0, 60, -1, 0), Face(1, 30, -1, 0.002) };
        var choice = FootprintFaceSelector.Choose(faces, MinNormalZ, 0)!;
        Assert.Equal(1, choice.FloorLevels);
        Assert.True(choice.IsStepped);   // still only part of the floor, so try the plan outline
    }

    [Fact]
    public void NoHorizontalFace_ReturnsNull()
    {
        Assert.Null(FootprintFaceSelector.Choose([Face(0, 20, 0.2, 0)], MinNormalZ, 0));
        Assert.Null(FootprintFaceSelector.Choose([Face(0, 0.01, -1, 0)], MinNormalZ, 0.1 * M2));
    }

    [Theory]
    [InlineData(74.0, 74.0, 0.0)]
    [InlineData(1.05, 74.0, 98.6)]
    [InlineData(90.0, 75.0, 20.0)]
    public void AreaMismatchPercent(double footprint, double declared, double expected) =>
        Assert.Equal(expected, FootprintFaceSelector.AreaMismatchPercent(footprint, declared)!.Value, 1);

    [Fact]
    public void AreaMismatchPercent_UnknownDeclaredArea_IsNull()
    {
        Assert.Null(FootprintFaceSelector.AreaMismatchPercent(10, null));
        Assert.Null(FootprintFaceSelector.AreaMismatchPercent(10, 0));
    }
}
