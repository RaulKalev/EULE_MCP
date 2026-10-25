using RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>M14: the IFC room validation never reported LocationDeltaMm, RoomAreaM2 or AreaDeltaPercent.</summary>
public class RoomGeometryDeltasTests
{
    [Fact]
    public void LocationDelta_IsThePlanDistanceInMm()
    {
        // 3 ft east, 4 ft north = 5 ft = 1524 mm.
        Assert.Equal(1524, RoomGeometryDeltas.LocationDeltaMm(10, 20, 13, 24));
        Assert.Null(RoomGeometryDeltas.LocationDeltaMm(null, 20, 13, 24));
        Assert.Null(RoomGeometryDeltas.LocationDeltaMm(10, 20, 13, null));
    }

    [Fact]
    public void AreaDelta_IsRelativeToTheRoom()
    {
        Assert.Equal(10.0, RoomGeometryDeltas.AreaDeltaPercent(110, 100));
        Assert.Equal(10.0, RoomGeometryDeltas.AreaDeltaPercent(90, 100));
        Assert.Null(RoomGeometryDeltas.AreaDeltaPercent(null, 100));
        Assert.Null(RoomGeometryDeltas.AreaDeltaPercent(50, 0));
    }

    [Fact]
    public void RoomArea_InSquareMetres()
    {
        Assert.Equal(54.16, RoomGeometryDeltas.RoomAreaM2(583));
    }
}
