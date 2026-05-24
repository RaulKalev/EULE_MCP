using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>
/// Compares an IFC Space's location and area to a native Revit Room's location and area.
/// All comparison is based on built-in Room data only — no shared parameters are read.
/// Phase 4: read-only, no document modifications.
/// </summary>
public static class RoomGeometryComparisonService
{
    /// <summary>
    /// Computes the 2D distance (mm) between <paramref name="ifcPlacementPointFeet"/> and
    /// the Room's location point, and the relative area difference (%).
    /// </summary>
    /// <param name="room">The native Revit Room.</param>
    /// <param name="ifcPlacementPointFeet">
    /// IFC Space placement point in host coordinates (feet, XYZ).
    /// May be null when geometry extraction was not run.
    /// </param>
    /// <param name="ifcAreaSqFt">
    /// IFC Space approximate area in square feet.
    /// May be null when geometry extraction was not run.
    /// </param>
    /// <param name="locationDeltaMm">
    /// Output: 2D distance between IFC point and Room location in mm.
    /// Null when either point is unavailable.
    /// </param>
    /// <param name="areaDeltaPercent">
    /// Output: abs((ifcArea - roomArea) / roomArea) * 100.
    /// Null when either area is unavailable or Room area is zero.
    /// </param>
    public static void Compare(
        Room room,
        XYZ? ifcPlacementPointFeet,
        double? ifcAreaSqFt,
        out double? locationDeltaMm,
        out double? areaDeltaPercent)
    {
        locationDeltaMm   = null;
        areaDeltaPercent  = null;

        // ── Location comparison ───────────────────────────────────────────────
        if (ifcPlacementPointFeet != null)
        {
            var roomLocationPt = GetRoomLocationPoint(room);
            if (roomLocationPt != null)
            {
                double dxFeet = ifcPlacementPointFeet.X - roomLocationPt.X;
                double dyFeet = ifcPlacementPointFeet.Y - roomLocationPt.Y;
                double distFeet = Math.Sqrt(dxFeet * dxFeet + dyFeet * dyFeet);
                locationDeltaMm = Math.Round(distFeet * 304.8, 1);
            }
        }

        // ── Area comparison ───────────────────────────────────────────────────
        if (ifcAreaSqFt.HasValue)
        {
            double roomAreaSqFt = room.Area; // 0 for unplaced rooms
            if (roomAreaSqFt > 1e-9)
            {
                double delta = Math.Abs(ifcAreaSqFt.Value - roomAreaSqFt) / roomAreaSqFt * 100.0;
                areaDeltaPercent = Math.Round(delta, 2);
            }
        }
    }

    /// <summary>
    /// Returns the XY location point of a Room in host coordinates (feet).
    /// Returns null for unplaced Rooms or when the location cannot be determined.
    /// </summary>
    public static XYZ? GetRoomLocationPoint(Room room)
    {
        try
        {
            return (room.Location as LocationPoint)?.Point;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the Room's area in square metres (converted from Revit internal sq ft).
    /// Returns null for unplaced Rooms (Area == 0).
    /// </summary>
    public static double? GetRoomAreaM2(Room room)
    {
        double area = room.Area;
        return area > 1e-9 ? area * 0.092903 : null;
    }
}
