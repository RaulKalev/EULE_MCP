using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>
/// Scores possible Room matches for an IFC Space and assigns a confidence level.
/// Matching uses built-in Room fields only (Level, Number, Name, Location, Area).
/// No shared parameters, IFC GUIDs, Comments, or Extensible Storage are read.
/// Phase 4: read-only, no document modifications.
/// </summary>
public class RoomMatchConfidenceService
{
    // ── Cached room snapshot ──────────────────────────────────────────────────

    /// <summary>
    /// Internal snapshot of a placed Room for confidence scoring.
    /// All Revit-API-dependent data is captured once at load time.
    /// </summary>
    private sealed record RoomSnapshot(
        long   RoomId,
        long   LevelId,
        string Number,
        string Name,
        double? LocationXFeet,
        double? LocationYFeet,
        double  AreaSqFt);

    private List<RoomSnapshot>? _rooms;

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pre-loads all placed Rooms from the host document for fast repeated scoring.
    /// Must be called once per validation run before any <see cref="Score"/> call.
    /// </summary>
    public void LoadRooms(Document hostDoc)
    {
        _rooms = new FilteredElementCollector(hostDoc)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .Cast<Room>()
            .Where(r => r.Area > 0) // placed rooms only
            .Select(r =>
            {
                var loc = RoomGeometryComparisonService.GetRoomLocationPoint(r);
                return new RoomSnapshot(
                    RoomId:        r.Id.Value,
                    LevelId:       GetLevelId(r),
                    Number:        (r.Number ?? string.Empty).Trim(),
                    Name:          (r.Name   ?? string.Empty).Trim(),
                    LocationXFeet: loc?.X,
                    LocationYFeet: loc?.Y,
                    AreaSqFt:      r.Area);
            })
            .ToList();
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Scores all Rooms on <paramref name="targetLevelId"/> against the given IFC Space
    /// attributes and returns the best confidence match.
    /// </summary>
    /// <param name="targetLevelId">Matched host Level id.</param>
    /// <param name="ifcNumber">IFC Space Room number. May be null/empty.</param>
    /// <param name="ifcName">IFC Space Room name. May be null/empty.</param>
    /// <param name="ifcLocationXFeet">IFC placement point X in host feet. Null if not available.</param>
    /// <param name="ifcLocationYFeet">IFC placement point Y in host feet. Null if not available.</param>
    /// <param name="ifcAreaSqFt">IFC approximate area in sq ft. Null if not available.</param>
    /// <param name="locationToleranceMm">Max acceptable 2D distance in mm.</param>
    /// <param name="areaTolerancePercent">Max acceptable area difference percent.</param>
    /// <returns>
    /// A <see cref="ScoreResult"/> with the best match's confidence, Room id, and
    /// the flag indicating whether multiple candidates were nearly equal (Ambiguous).
    /// </returns>
    public ScoreResult Score(
        long   targetLevelId,
        string? ifcNumber,
        string? ifcName,
        double? ifcLocationXFeet,
        double? ifcLocationYFeet,
        double? ifcAreaSqFt,
        double  locationToleranceMm,
        double  areaTolerancePercent)
    {
        if (_rooms == null)
            throw new InvalidOperationException("Call LoadRooms() before Score().");

        var num = (ifcNumber ?? string.Empty).Trim();
        var nm  = (ifcName   ?? string.Empty).Trim();
        double locationToleranceFeet = locationToleranceMm / 304.8;

        // Collect all candidate rooms on this level with their raw score
        var scored = new List<(RoomSnapshot Room, int Score, string SubType)>();

        foreach (var room in _rooms)
        {
            if (room.LevelId != targetLevelId) continue;

            bool numberMatch = !string.IsNullOrEmpty(num) &&
                string.Equals(room.Number, num, StringComparison.OrdinalIgnoreCase);

            bool nameMatch = !string.IsNullOrEmpty(nm) &&
                string.Equals(room.Name, nm, StringComparison.OrdinalIgnoreCase);

            bool locationWithinTol = IsLocationWithin(
                ifcLocationXFeet, ifcLocationYFeet,
                room.LocationXFeet, room.LocationYFeet,
                locationToleranceFeet);

            bool areaWithinTol = IsAreaWithin(ifcAreaSqFt, room.AreaSqFt, areaTolerancePercent);

            // ── Assign a priority score (higher = better) ────────────────────
            int score;
            string subType;

            if (numberMatch && nameMatch)
            {
                score   = 100; // High base
                subType = "NumberAndName";
            }
            else if (numberMatch && locationWithinTol)
            {
                score   = 60;  // Medium: number + location
                subType = "NumberLocation";
            }
            else if (nameMatch && locationWithinTol)
            {
                score   = 55;  // Medium: name + location
                subType = "NameLocation";
            }
            else if (locationWithinTol && areaWithinTol)
            {
                score   = 30;  // Low: geometry only
                subType = "Geometry";
            }
            else
            {
                continue; // no meaningful match
            }

            scored.Add((room, score, subType));
        }

        if (scored.Count == 0)
            return new ScoreResult { Confidence = RoomMatchConfidence.None };

        // Sort by descending score
        scored.Sort((a, b) => b.Score.CompareTo(a.Score));

        var best = scored[0];
        bool hasSecond = scored.Count > 1 && scored[1].Score >= best.Score - 10;

        // ── Determine Ambiguous ───────────────────────────────────────────────
        // If two or more rooms are within 10 score points → Ambiguous
        if (hasSecond)
        {
            return new ScoreResult
            {
                Confidence         = RoomMatchConfidence.Ambiguous,
                BestRoomId         = best.Room.RoomId,
                SecondBestRoomId   = scored[1].Room.RoomId,
                BestRoomNumber     = best.Room.Number,
                BestRoomName       = best.Room.Name
            };
        }

        // ── Map score to confidence ───────────────────────────────────────────
        string confidence = best.Score switch
        {
            >= 100 => RoomMatchConfidence.High,
            >= 50  => RoomMatchConfidence.Medium,
            _      => RoomMatchConfidence.Low
        };

        // ── Geometry validation for High confidence matches ───────────────────
        // Even a High identity match should flag geometry anomalies
        bool geometryAnomaly = false;
        if (confidence == RoomMatchConfidence.High)
        {
            bool locOk  = !ifcLocationXFeet.HasValue || IsLocationWithin(
                ifcLocationXFeet, ifcLocationYFeet,
                best.Room.LocationXFeet, best.Room.LocationYFeet,
                locationToleranceFeet);
            bool areaOk = !ifcAreaSqFt.HasValue || IsAreaWithin(
                ifcAreaSqFt, best.Room.AreaSqFt, areaTolerancePercent);
            geometryAnomaly = !locOk || !areaOk;
        }

        return new ScoreResult
        {
            Confidence       = confidence,
            BestRoomId       = best.Room.RoomId,
            BestRoomNumber   = best.Room.Number,
            BestRoomName     = best.Room.Name,
            ScoreSubType     = best.SubType,
            GeometryAnomaly  = geometryAnomaly
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsLocationWithin(
        double? ix, double? iy,
        double? rx, double? ry,
        double toleranceFeet)
    {
        if (!ix.HasValue || !rx.HasValue || !iy.HasValue || !ry.HasValue)
            return false;

        double dx = ix.Value - rx.Value;
        double dy = iy.Value - ry.Value;
        return Math.Sqrt(dx * dx + dy * dy) <= toleranceFeet;
    }

    private static bool IsAreaWithin(double? ifcArea, double roomArea, double tolerancePct)
    {
        if (!ifcArea.HasValue || roomArea < 1e-9) return false;
        double delta = Math.Abs(ifcArea.Value - roomArea) / roomArea * 100.0;
        return delta <= tolerancePct;
    }

    private static long GetLevelId(Room room)
    {
        try { return room.LevelId.Value; }
        catch { return -1L; }
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Result returned by <see cref="Score"/>.</summary>
    public sealed class ScoreResult
    {
        public string  Confidence       { get; init; } = RoomMatchConfidence.None;
        public long?   BestRoomId       { get; init; }
        public long?   SecondBestRoomId { get; init; }
        public string? BestRoomNumber   { get; init; }
        public string? BestRoomName     { get; init; }
        public string? ScoreSubType     { get; init; }
        public bool    GeometryAnomaly  { get; init; }
    }
}
