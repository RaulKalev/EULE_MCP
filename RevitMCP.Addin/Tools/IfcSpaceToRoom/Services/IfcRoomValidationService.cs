using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>
/// Orchestrates the Phase 4 validation pipeline:
///   IFC Space candidates → Level match → optional geometry extraction →
///   Room candidate scoring → confidence assignment → validation report.
///
/// Read-only: no document modifications.
/// Comparison uses built-in Room fields only (Level, Number, Name, Location, Area).
/// No shared parameters, IFC GUIDs, Comments, or Extensible Storage are read or written.
/// </summary>
public class IfcRoomValidationService
{
    // ── Sub-services ──────────────────────────────────────────────────────────

    private readonly IfcSpaceCollector           _collector      = new();
    private readonly IfcParameterReader          _reader         = new();
    private readonly LevelMatcher                _levelMatcher   = new();
    private readonly IfcSpaceGeometryExtractor   _extractor      = new();
    private readonly RoomMatchConfidenceService  _confidenceService = new();

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the full validation for one linked IFC model against the host document's Rooms.
    /// </summary>
    public IfcRoomValidationResult Run(
        Document hostDoc,
        IfcRoomValidationOptions options)
    {
        // ── 1. Resolve link ────────────────────────────────────────────────────
        var linkElement = hostDoc.GetElement(new ElementId(options.LinkInstanceId));
        if (linkElement is not RevitLinkInstance linkInstance)
        {
            return Fail(options,
                $"Element {options.LinkInstanceId} is not a RevitLinkInstance.");
        }

        var linkedDoc = linkInstance.GetLinkDocument();
        if (linkedDoc == null)
        {
            return Fail(options,
                $"Link {options.LinkInstanceId} ({linkInstance.Name}) is not loaded.");
        }

        Transform linkTransform;
        try   { linkTransform = linkInstance.GetTotalTransform(); }
        catch (Exception ex)
        {
            return Fail(options,
                $"Could not obtain link transform for {linkInstance.Name}: {ex.Message}");
        }

        // ── 2. Collect candidate elements ──────────────────────────────────────
        List<Element> candidates;
        if (options.LinkedElementIds.Count > 0)
        {
            candidates = new List<Element>(options.LinkedElementIds.Count);
            foreach (var eid in options.LinkedElementIds)
            {
                var el = linkedDoc.GetElement(new ElementId(eid));
                if (el != null) candidates.Add(el);
            }
        }
        else
        {
            var warnings = new List<string>();
            candidates = _collector.Collect(linkedDoc, warnings);
        }

        // ── 3. Pre-load host state ─────────────────────────────────────────────
        _levelMatcher.LoadLevels(hostDoc);
        _confidenceService.LoadRooms(hostDoc);

        // ── 4. Process candidates ──────────────────────────────────────────────
        var items    = new List<IfcRoomValidationItem>(candidates.Count);
        bool truncated = false;

        foreach (var element in candidates)
        {
            if (options.MaxResults > 0 && items.Count >= options.MaxResults)
            {
                truncated = true;
                break;
            }

            var item = ProcessElement(element, linkTransform, options);
            items.Add(item);
        }

        // ── 5. Build result ────────────────────────────────────────────────────
        var summary = BuildSummary(items, truncated);

        return new IfcRoomValidationResult
        {
            Success        = true,
            LinkInstanceId = options.LinkInstanceId,
            Summary        = summary,
            Items          = items
        };
    }

    // ─────────────────────────────────────────────────────────────────────────

    private IfcRoomValidationItem ProcessElement(
        Element element,
        Transform linkTransform,
        IfcRoomValidationOptions options)
    {
        var item = new IfcRoomValidationItem
        {
            LinkedElementId = element.Id.Value
        };

        try
        {
            // ── a. Read IFC metadata ─────────────────────────────────────────
            var meta = _reader.ReadMetadata(element);
            item.IfcNumber   = meta.Number;
            item.IfcName     = meta.Name;

            // ── b. Level match ────────────────────────────────────────────
            double? bboxZ = TryGetBboxBottomZ(element, linkTransform);
            var levelMatch = _levelMatcher.Match(bboxZ, options.LevelMatchToleranceMm);

            item.TargetLevelId   = levelMatch.LevelId;
            item.TargetLevelName = levelMatch.LevelName;

            if (levelMatch.LevelId == null)
            {
                item.Status          = ValidationStatus.NoLevelMatch;
                item.RecommendedAction = "ReviewManually";
                item.Warnings.Add("No host Level could be matched to this element's elevation.");
                return item;
            }

            long   levelId       = levelMatch.LevelId.Value;
            double levelElevFeet = levelMatch.LevelElevationFeet;

            // ── c. Optional geometry extraction ──────────────────────────
            double? placementXFeet = null;
            double? placementYFeet = null;
            double? ifcAreaSqFt    = null;

            if (options.IncludeGeometryComparison)
            {
                var geomOptions = new IfcGeometryExtractionOptions
                {
                    EndpointSnapToleranceMm  = options.EndpointSnapToleranceMm,
                    TinySegmentToleranceMm   = options.TinySegmentToleranceMm,
                    LevelMatchToleranceMm    = options.LevelMatchToleranceMm
                };

                var footprint = _extractor.Extract(element, linkTransform, levelElevFeet, geomOptions);

                if (footprint.Success && footprint.PlacementPoint != null)
                {
                    placementXFeet = footprint.PlacementPoint.X;
                    placementYFeet = footprint.PlacementPoint.Y;
                    ifcAreaSqFt    = footprint.ApproxAreaSquareFeet;

                    item.IfcAreaM2  = Math.Round(footprint.ApproxAreaSquareFeet * 0.092903, 2);
                }
                else
                {
                    item.Warnings.Add($"Geometry extraction returned {footprint.Status} — using bbox for location comparison.");
                    // Fall back to bbox centre for location
                    var bboxCentre = TryGetBboxCentre(element, linkTransform);
                    placementXFeet = bboxCentre?.x;
                    placementYFeet = bboxCentre?.y;
                }
            }
            else
            {
                // Fast path: bbox centre as approximate location
                var bboxCentre = TryGetBboxCentre(element, linkTransform);
                placementXFeet = bboxCentre?.x;
                placementYFeet = bboxCentre?.y;
            }

            // ── d. Score match ────────────────────────────────────────────
            var scoreResult = _confidenceService.Score(
                levelId,
                item.IfcNumber,
                item.IfcName,
                placementXFeet,
                placementYFeet,
                ifcAreaSqFt,
                options.LocationToleranceMm,
                options.AreaTolerancePercent);

            item.Confidence = scoreResult.Confidence;

            // ── e. Populate match fields and status ───────────────────────
            if (scoreResult.BestRoomId.HasValue)
            {
                item.MatchedRoomId     = scoreResult.BestRoomId;
                item.MatchedRoomNumber = scoreResult.BestRoomNumber;
                item.MatchedRoomName   = scoreResult.BestRoomName;
            }

            AssignStatusAndAction(item, scoreResult, options.LocationToleranceMm, options.AreaTolerancePercent);
        }
        catch (Exception ex)
        {
            item.Status           = ValidationStatus.Error;
            item.RecommendedAction = "ReviewManually";
            item.Warnings.Add($"Unexpected exception: {ex.Message}");
        }

        return item;
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static void AssignStatusAndAction(
        IfcRoomValidationItem item,
        RoomMatchConfidenceService.ScoreResult score,
        double locationToleranceMm,
        double areaTolerancePct)
    {
        switch (score.Confidence)
        {
            case RoomMatchConfidence.High:
                if (score.GeometryAnomaly)
                {
                    item.Status           = ValidationStatus.PossibleMoveOrGeometryChange;
                    item.RecommendedAction = "ReviewGeometry";
                }
                else
                {
                    item.Status           = ValidationStatus.HighConfidenceMatch;
                    item.RecommendedAction = "None";
                }
                break;

            case RoomMatchConfidence.Medium:
                // Determine whether it's a number-only or name-only match
                bool numMatch  = string.Equals(item.IfcNumber, item.MatchedRoomNumber, StringComparison.OrdinalIgnoreCase);
                bool nameMatch = string.Equals(item.IfcName,   item.MatchedRoomName,   StringComparison.OrdinalIgnoreCase);

                if (numMatch && !nameMatch)
                {
                    item.Status = ValidationStatus.PossibleRename;
                }
                else if (!numMatch && nameMatch)
                {
                    item.Status = ValidationStatus.NumberMismatch;
                }
                else
                {
                    item.Status = ValidationStatus.MediumConfidenceMatch;
                }
                item.RecommendedAction = "ReviewAndSync";
                break;

            case RoomMatchConfidence.Low:
                item.Status           = ValidationStatus.LowConfidenceMatch;
                item.RecommendedAction = "ReviewManually";
                break;

            case RoomMatchConfidence.Ambiguous:
                item.Status           = ValidationStatus.AmbiguousMatch;
                item.RecommendedAction = "ReviewAndResolve";
                item.Warnings.Add(
                    $"Multiple Rooms could match this IFC Space. " +
                    $"Best candidate: Room id {score.BestRoomId}, " +
                    $"second candidate: Room id {score.SecondBestRoomId}.");
                break;

            default: // None
                item.Status           = ValidationStatus.MissingRoom;
                item.Confidence       = RoomMatchConfidence.None;
                item.RecommendedAction = "CreateRoom";
                break;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static double? TryGetBboxBottomZ(Element element, Transform transform)
    {
        try
        {
            var bbox = element.get_BoundingBox(null);
            if (bbox == null) return null;

            var corners = new[]
            {
                new XYZ(bbox.Min.X, bbox.Min.Y, bbox.Min.Z),
                new XYZ(bbox.Max.X, bbox.Min.Y, bbox.Min.Z),
                new XYZ(bbox.Min.X, bbox.Max.Y, bbox.Min.Z),
                new XYZ(bbox.Max.X, bbox.Max.Y, bbox.Min.Z),
                new XYZ(bbox.Min.X, bbox.Min.Y, bbox.Max.Z),
                new XYZ(bbox.Max.X, bbox.Min.Y, bbox.Max.Z),
                new XYZ(bbox.Min.X, bbox.Max.Y, bbox.Max.Z),
                new XYZ(bbox.Max.X, bbox.Max.Y, bbox.Max.Z)
            };

            return corners.Select(c => transform.OfPoint(c).Z).Min();
        }
        catch
        {
            return null;
        }
    }

    private static (double x, double y)? TryGetBboxCentre(Element element, Transform transform)
    {
        try
        {
            var bbox = element.get_BoundingBox(null);
            if (bbox == null) return null;

            var centre = transform.OfPoint(new XYZ(
                (bbox.Min.X + bbox.Max.X) / 2.0,
                (bbox.Min.Y + bbox.Max.Y) / 2.0,
                (bbox.Min.Z + bbox.Max.Z) / 2.0));

            return (centre.X, centre.Y);
        }
        catch
        {
            return null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static IfcRoomValidationSummary BuildSummary(
        List<IfcRoomValidationItem> items, bool truncated)
    {
        return new IfcRoomValidationSummary
        {
            TotalIfcSpaces          = items.Count,
            HighConfidenceMatches   = items.Count(i => i.Status == ValidationStatus.HighConfidenceMatch),
            MediumConfidenceMatches = items.Count(i => i.Status is
                ValidationStatus.MediumConfidenceMatch or
                ValidationStatus.PossibleRename or
                ValidationStatus.NumberMismatch),
            LowConfidenceMatches    = items.Count(i => i.Status == ValidationStatus.LowConfidenceMatch),
            MissingRooms            = items.Count(i => i.Status == ValidationStatus.MissingRoom),
            AmbiguousMatches        = items.Count(i => i.Status == ValidationStatus.AmbiguousMatch),
            PossibleGeometryChanges = items.Count(i => i.Status == ValidationStatus.PossibleMoveOrGeometryChange),
            PossibleRenames         = items.Count(i => i.Status == ValidationStatus.PossibleRename),
            Unprocessable           = items.Count(i => i.Status is
                ValidationStatus.NoLevelMatch or
                ValidationStatus.NoGeometry or
                ValidationStatus.Error),
            Truncated               = truncated
        };
    }

    private static IfcRoomValidationResult Fail(IfcRoomValidationOptions options, string error) =>
        new()
        {
            Success        = false,
            Error          = error,
            LinkInstanceId = options.LinkInstanceId
        };
}
