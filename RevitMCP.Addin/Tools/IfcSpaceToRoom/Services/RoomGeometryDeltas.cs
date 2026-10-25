namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>
/// How far an IFC space is from its matched room: 2D distance of the placement points and relative area difference.
/// The validation compared both against tolerances but never reported the values, so LocationDeltaMm, RoomAreaM2 and
/// AreaDeltaPercent were always null (M14). Inputs in Revit internal units (feet, square feet). Pure.
/// </summary>
public static class RoomGeometryDeltas
{
    private const double MmPerFoot = 304.8;
    private const double SquareMetresPerSquareFoot = 0.092903;

    /// <summary>Distance in mm, rounded to 1 mm; null when either point is unknown.</summary>
    public static double? LocationDeltaMm(double? ifcX, double? ifcY, double? roomX, double? roomY)
    {
        if (ifcX is not { } ix || ifcY is not { } iy || roomX is not { } rx || roomY is not { } ry) return null;
        var dx = ix - rx;
        var dy = iy - ry;
        return Math.Round(Math.Sqrt(dx * dx + dy * dy) * MmPerFoot, 0);
    }

    /// <summary>|IFC − room| / room in percent, one decimal; null when either area is unknown or the room has none.</summary>
    public static double? AreaDeltaPercent(double? ifcAreaSqFt, double roomAreaSqFt)
    {
        if (ifcAreaSqFt is not { } ifc || roomAreaSqFt < 1e-9) return null;
        return Math.Round(Math.Abs(ifc - roomAreaSqFt) / roomAreaSqFt * 100.0, 1);
    }

    public static double RoomAreaM2(double roomAreaSqFt) => Math.Round(roomAreaSqFt * SquareMetresPerSquareFoot, 2);
}
