using Autodesk.Revit.DB;
using RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>
/// Orchestrates the full Phase 1 preview operation:
/// resolves link → collects candidates → reads metadata →
/// transforms bounding box → matches level → checks existing rooms →
/// builds status and summary.
/// Phase 1: strictly read-only, no document modifications.
/// </summary>
public class IfcSpacePreviewService
{
    // ── Candidate status constants ─────────────────────────────────────────────

    private const string StatusReady           = "Ready";
    private const string StatusAlreadyExists   = "AlreadyExists";
    private const string StatusWarning         = "Warning";
    private const string StatusMissingMetadata = "MissingMetadata";
    private const string StatusNoLevelMatch    = "NoLevelMatch";
    private const string StatusNotIfcSpace     = "NotIfcSpace";
    private const string StatusError           = "Error";

    // ── Service instances ──────────────────────────────────────────────────────

    private readonly IfcSpaceCollector           _collector    = new();
    private readonly IfcParameterReader          _reader       = new();
    private readonly LevelMatcher                _levelMatcher = new();
    private readonly ExistingRoomPreviewMatcher  _roomMatcher  = new();

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the preview for one linked IFC model.
    /// </summary>
    /// <param name="hostDoc">Active host Revit document.</param>
    /// <param name="options">Preview parameters.</param>
    public IfcSpacePreviewResult Preview(Document hostDoc, IfcSpacePreviewOptions options)
    {
        // 1. Resolve the link instance
        var linkElement = hostDoc.GetElement(new ElementId(options.LinkInstanceId));
        if (linkElement is not RevitLinkInstance linkInstance)
        {
            return Fail(options.LinkInstanceId,
                $"Element {options.LinkInstanceId} is not a RevitLinkInstance in the active document.");
        }

        var linkedDoc = linkInstance.GetLinkDocument();
        if (linkedDoc == null)
        {
            return Fail(options.LinkInstanceId,
                $"Linked document for link {options.LinkInstanceId} ({linkInstance.Name}) is not loaded or could not be accessed.");
        }

        // 2. Get the total transform for coordinate mapping
        Transform linkTransform;
        try
        {
            linkTransform = linkInstance.GetTotalTransform();
        }
        catch (Exception ex)
        {
            return Fail(options.LinkInstanceId,
                $"Could not obtain link transform for {linkInstance.Name}: {ex.Message}");
        }

        // 3. Pre-load host levels and (optionally) existing rooms
        _levelMatcher.LoadLevels(hostDoc);

        if (options.IncludeExistingRoomCheck)
            _roomMatcher.LoadRooms(hostDoc);

        // 4. Collect IFC Space candidates from the linked document.
        //    includeProbable is driven by the option; default is false (confirmed-only).
        var detections = _collector.Collect(linkedDoc, options.IncludeProbable);

        // 5. Process each candidate
        var spaces     = new List<IfcSpaceCandidate>();
        bool truncated = false;

        foreach (var detection in detections)
        {
            if (options.MaxResults > 0 && spaces.Count >= options.MaxResults)
            {
                truncated = true;
                break;
            }

            var candidate = ProcessCandidate(
                detection, linkInstance, linkTransform, options);

            spaces.Add(candidate);
        }

        // 6. Build summary
        var summary = BuildSummary(spaces, truncated);

        return new IfcSpacePreviewResult
        {
            Success        = true,
            LinkInstanceId = options.LinkInstanceId,
            Spaces         = spaces,
            Summary        = summary
        };
    }

    // ─────────────────────────────────────────────────────────────────────────

    private IfcSpaceCandidate ProcessCandidate(
        IfcSpaceDetectionResult detection,
        RevitLinkInstance linkInstance,
        Transform linkTransform,
        IfcSpacePreviewOptions options)
    {
        var element = detection.Element;

        var candidate = new IfcSpaceCandidate
        {
            LinkInstanceId      = linkInstance.Id.Value,
            LinkedElementId     = element.Id.Value,
            DetectionConfidence = detection.Confidence,
            Warnings            = [..detection.Warnings]   // seed with per-detection warnings
        };

        // Probable elements cannot be safely converted; flag early
        bool isProbable = detection.Confidence == IfcDetectionConfidence.Probable;

        try
        {
            // — Read IFC metadata ————————————————————————————————————————————
            var meta = _reader.ReadMetadata(element);
            candidate.IfcGuid      = meta.IfcGuid;
            candidate.Number       = meta.Number;
            candidate.NumberSource = meta.NumberSource;
            candidate.Name         = meta.Name;
            candidate.NameSource   = meta.NameSource;
            candidate.StoreyName   = meta.StoreyName;

            // Probable elements: mark immediately — short-circuit the rest
            if (isProbable)
            {
                candidate.Status         = StatusNotIfcSpace;
                candidate.CanConvertLater = false;
                return candidate;
            }

            // — Bounding box → host coordinates ——————————————————————————————
            double? bottomElevationFeet = null;
            double? approxAreaM2        = null;

            var bbox = element.get_BoundingBox(null);
            if (bbox != null)
            {
                var hostBbox = TransformBoundingBox(bbox, linkTransform);

                bottomElevationFeet = hostBbox.Min.Z;

                // Approximate area: width × depth of the floor footprint
                double widthFeet = hostBbox.Max.X - hostBbox.Min.X;
                double depthFeet = hostBbox.Max.Y - hostBbox.Min.Y;
                double areaFtSq  = widthFeet * depthFeet;
                approxAreaM2     = Math.Round(areaFtSq * 0.092903, 2); // ft² → m²
            }
            else
            {
                candidate.Warnings.Add("Bounding box not available — elevation and area could not be estimated.");
            }

            candidate.BottomElevationMm = bottomElevationFeet.HasValue
                ? Math.Round(LevelMatcher.FeetToMm(bottomElevationFeet.Value), 1)
                : null;
            candidate.ApproxAreaM2 = approxAreaM2;

            // — Level matching ————————————————————————————————————————————————
            var levelMatch = _levelMatcher.Match(bottomElevationFeet, options.LevelMatchToleranceMm);
            candidate.TargetLevelId   = levelMatch.LevelId;
            candidate.TargetLevelName = levelMatch.LevelName;

            if (levelMatch.LevelId.HasValue)
            {
                candidate.LevelOffsetMm = Math.Round(LevelMatcher.FeetToMm(levelMatch.OffsetFeet), 1);
                if (!levelMatch.IsWithinTolerance)
                {
                    candidate.Warnings.Add(
                        $"Level offset {candidate.LevelOffsetMm:F0} mm exceeds tolerance {options.LevelMatchToleranceMm:F0} mm. " +
                        $"Closest level: {levelMatch.LevelName}.");
                }
            }

            // — Existing room check ———————————————————————————————————————————
            if (options.IncludeExistingRoomCheck)
            {
                candidate.ExistingRoomMatch = _roomMatcher.Check(
                    candidate.TargetLevelId, candidate.Number, candidate.Name);
            }
            else
            {
                candidate.ExistingRoomMatch = ExistingRoomPreviewMatcher.Skipped;
            }

            // — Determine status ——————————————————————————————————————————————
            candidate.Status         = DetermineStatus(candidate, levelMatch);
            candidate.CanConvertLater = candidate.Status is StatusReady or StatusWarning;
        }
        catch (Exception ex)
        {
            candidate.Status         = StatusError;
            candidate.CanConvertLater = false;
            candidate.Warnings.Add($"Unexpected error processing element {element.Id.Value}: {ex.Message}");
        }

        return candidate;
    }

    private static string DetermineStatus(IfcSpaceCandidate candidate, LevelMatchResult levelMatch)
    {
        // Fatal: no level could be resolved
        if (candidate.TargetLevelId == null)
            return StatusNoLevelMatch;

        // Already exists in host
        if (candidate.ExistingRoomMatch == ExistingRoomPreviewMatcher.ExactNumberNameLevelMatch)
            return StatusAlreadyExists;

        // Missing usable room identity
        bool hasNumber = !string.IsNullOrWhiteSpace(candidate.Number);
        bool hasName   = !string.IsNullOrWhiteSpace(candidate.Name);
        if (!hasNumber && !hasName)
            return StatusMissingMetadata;

        // Level offset is outside tolerance → Warning (still convertible)
        if (!levelMatch.IsWithinTolerance)
            return StatusWarning;

        // Detection or other advisory warnings present
        if (candidate.Warnings.Count > 0)
            return StatusWarning;

        return StatusReady;
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static IfcSpacePreviewSummary BuildSummary(List<IfcSpaceCandidate> spaces, bool truncated)
    {
        return new IfcSpacePreviewSummary
        {
            TotalCandidates = spaces.Count,
            Ready           = spaces.Count(s => s.Status == StatusReady),
            AlreadyExists   = spaces.Count(s => s.Status == StatusAlreadyExists),
            Warnings        = spaces.Count(s => s.Status == StatusWarning),
            MissingMetadata = spaces.Count(s => s.Status == StatusMissingMetadata),
            NoLevelMatch    = spaces.Count(s => s.Status == StatusNoLevelMatch),
            Failed          = spaces.Count(s => s.Status is StatusError or StatusNotIfcSpace),
            Truncated       = truncated
        };
    }

    private static IfcSpacePreviewResult Fail(long linkInstanceId, string error) =>
        new()
        {
            Success        = false,
            LinkInstanceId = linkInstanceId,
            Error          = error,
            Summary        = new IfcSpacePreviewSummary()
        };

    // ── Geometry helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Transforms a link-local bounding box into host coordinates by transforming
    /// all 8 corners and computing a new axis-aligned bounding box.
    /// </summary>
    private static BoundingBoxXYZ TransformBoundingBox(BoundingBoxXYZ box, Transform transform)
    {
        if (transform.IsIdentity) return box;

        var corners = new[]
        {
            new XYZ(box.Min.X, box.Min.Y, box.Min.Z),
            new XYZ(box.Max.X, box.Min.Y, box.Min.Z),
            new XYZ(box.Min.X, box.Max.Y, box.Min.Z),
            new XYZ(box.Max.X, box.Max.Y, box.Min.Z),
            new XYZ(box.Min.X, box.Min.Y, box.Max.Z),
            new XYZ(box.Max.X, box.Min.Y, box.Max.Z),
            new XYZ(box.Min.X, box.Max.Y, box.Max.Z),
            new XYZ(box.Max.X, box.Max.Y, box.Max.Z)
        };

        var pts    = corners.Select(c => transform.OfPoint(c)).ToArray();
        var result = new BoundingBoxXYZ();
        result.Min = new XYZ(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Min(p => p.Z));
        result.Max = new XYZ(pts.Max(p => p.X), pts.Max(p => p.Y), pts.Max(p => p.Z));
        return result;
    }
}
