using Autodesk.Revit.DB;
using RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>
/// Matches an IFC Space's approximate bottom elevation to the nearest host Level.
/// Phase 1: read-only, no document modifications.
/// </summary>
public class LevelMatcher
{
    // ── Cached host levels (populated once per preview operation) ─────────────

    private List<(long Id, string Name, double ElevationFeet)>? _hostLevels;

    /// <summary>
    /// Pre-loads all Levels from <paramref name="hostDoc"/> for efficient repeated matching.
    /// Call this once before calling <see cref="Match"/> for each candidate.
    /// </summary>
    public void LoadLevels(Document hostDoc)
    {
        _hostLevels = new FilteredElementCollector(hostDoc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .Select(l => (Id: l.Id.Value, Name: l.Name, ElevationFeet: l.Elevation))
            .OrderBy(l => l.ElevationFeet)
            .ToList();
    }

    /// <summary>
    /// Matches <paramref name="spaceBottomElevationFeet"/> (in host coordinates, Revit internal feet)
    /// to the nearest host Level.
    /// </summary>
    /// <param name="spaceBottomElevationFeet">
    /// Bottom Z-coordinate of the IFC space in host-document coordinates (feet).
    /// Pass <c>null</c> when the elevation could not be determined.
    /// </param>
    /// <param name="toleranceMm">Acceptable vertical offset in millimetres. Default 300 mm.</param>
    public LevelMatchResult Match(double? spaceBottomElevationFeet, double toleranceMm = 300.0)
    {
        if (_hostLevels == null)
            throw new InvalidOperationException("Call LoadLevels() before Match().");

        if (_hostLevels.Count == 0)
            return new LevelMatchResult { Status = "Failed", IsWithinTolerance = false };

        if (spaceBottomElevationFeet == null)
        {
            return new LevelMatchResult
            {
                Status = "Failed",
                IsWithinTolerance = false
            };
        }

        double toleranceFeet = MmToFeet(toleranceMm);

        // Find the nearest level by absolute elevation offset
        var nearest = _hostLevels
            .MinBy(l => Math.Abs(l.ElevationFeet - spaceBottomElevationFeet.Value));

        double offsetFeet = spaceBottomElevationFeet.Value - nearest.ElevationFeet;
        bool withinTolerance = Math.Abs(offsetFeet) <= toleranceFeet;

        return new LevelMatchResult
        {
            LevelId              = nearest.Id,
            LevelName            = nearest.Name,
            LevelElevationFeet   = nearest.ElevationFeet,
            OffsetFeet           = offsetFeet,
            IsWithinTolerance    = withinTolerance,
            Status               = withinTolerance ? "Ready" : "Warning"
        };
    }

    // ── Unit helpers ──────────────────────────────────────────────────────────

    /// <summary>Converts millimetres to Revit internal feet.</summary>
    public static double MmToFeet(double mm) => mm / 304.8;

    /// <summary>Converts Revit internal feet to millimetres.</summary>
    public static double FeetToMm(double feet) => feet * 304.8;
}
