using Autodesk.Revit.DB;
using RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>
/// High-level service that orchestrates the full geometry extraction pipeline for a single
/// linked IFC Space element:
///   solid extraction → bottom face selection → loop extraction →
///   host-coordinate transform → loop cleanup → loop validation →
///   area calculation → placement point.
/// Phase 2: strictly read-only. Creates new curve objects but never writes to any document.
/// </summary>
public class IfcSpaceGeometryExtractor
{
    private readonly SolidExtractionService    _solidService    = new();
    private readonly BottomFaceFinder          _faceFinder      = new();
    private readonly CurveLoopExtractor        _loopExtractor   = new();
    private readonly CurveLoopCleaner          _loopCleaner     = new();
    private readonly CurveLoopValidator        _loopValidator   = new();
    private readonly RoomPlacementPointService _placementService = new();

    /// <summary>
    /// Extracts the footprint for <paramref name="linkedElement"/>.
    /// </summary>
    /// <param name="linkedElement">The IFC Space element inside the linked document.</param>
    /// <param name="linkTransform">
    /// Total transform from linked-document coordinates to host-document coordinates.
    /// Obtain via <c>linkInstance.GetTotalTransform()</c>.
    /// </param>
    /// <param name="levelElevationFeet">
    /// Elevation of the matched host Level in feet (Revit internal units).
    /// Used as the Z coordinate for the placement point.
    /// </param>
    /// <param name="options">Geometry extraction options.</param>
    /// <param name="declaredAreaM2">
    /// The space's area from its IFC data, when known. A footprint whose area differs from it by more
    /// than <see cref="AreaMismatchWarningPercent"/> gets a warning (#68).
    /// </param>
    public IfcSpaceFootprint Extract(
        Element linkedElement,
        Transform linkTransform,
        double levelElevationFeet,
        IfcGeometryExtractionOptions options,
        double? declaredAreaM2 = null)
    {
        var footprint = new IfcSpaceFootprint
        {
            LinkedElementId = linkedElement.Id.Value
        };

        try
        {
            // ── 1. Extract solids ─────────────────────────────────────────────
            var solids = _solidService.GetSolids(linkedElement);

            if (solids.Count == 0)
            {
                footprint.Status = IfcGeometryStatus.NoSolidGeometry;
                footprint.Errors.Add("No solid geometry found in element.");
                return footprint;
            }

            // ── 2. Find bottom face ───────────────────────────────────────────
            double minAreaFt2 = options.MinimumAreaSquareFeet;

            var bottomFace = _faceFinder.Find(
                solids,
                out string? faceWarning,
                out FootprintFaceChoice? choice,
                out Solid? ownerSolid,
                options.HorizontalFaceToleranceDegrees,
                minAreaFt2);

            if (bottomFace == null)
            {
                footprint.Status = IfcGeometryStatus.NoUsableBottomFace;
                footprint.Errors.Add(faceWarning ?? "No horizontal bottom face found.");
                return footprint;
            }
            if (faceWarning != null)
                footprint.Warnings.Add(faceWarning);

            // A stepped or split floor: the largest floor face is only part of the space. Use the
            // plan outline (shadow) of the whole space instead when Revit can compute it.
            if (choice is { IsStepped: true })
            {
                var outline = PlanOutline(solids, ownerSolid, bottomFace, choice);
                if (outline != null)
                {
                    footprint.Warnings.Add(
                        $"Stepped floor ({choice.FloorLevels} level(s)): the footprint is the plan outline of the whole space.");
                    bottomFace = outline;
                }
                else
                {
                    footprint.Warnings.Add(
                        $"Stepped floor ({choice.FloorLevels} level(s)): the footprint is the largest floor face, " +
                        $"{choice.AreaFt2 * SquareMetresPerSquareFoot:0.#} of {choice.TotalFloorAreaFt2 * SquareMetresPerSquareFoot:0.#} m².");
                }
            }

            // ── 3. Extract CurveLoops from face ───────────────────────────────
            _loopExtractor.Extract(
                bottomFace,
                out CurveLoop? rawOuterLoop,
                out List<CurveLoop> rawInnerLoops,
                out List<string> extractWarnings);

            footprint.Warnings.AddRange(extractWarnings);

            if (rawOuterLoop == null)
            {
                footprint.Status = IfcGeometryStatus.NoClosedLoop;
                footprint.Errors.Add("Could not extract a closed edge loop from the bottom face.");
                return footprint;
            }

            // ── 4. Transform to host coordinates ─────────────────────────────
            CurveLoop hostOuterLoop = TransformLoop(rawOuterLoop, linkTransform);
            var hostInnerLoops = rawInnerLoops
                .Select(l => TransformLoop(l, linkTransform))
                .ToList();

            // Capture bottom elevation from the transformed face origin
            footprint.BottomElevationFeet = linkTransform.OfPoint(bottomFace.Origin).Z;

            // ── 5. Clean the outer loop ───────────────────────────────────────
            var cleanedLoop = _loopCleaner.Clean(
                hostOuterLoop,
                options.TinySegmentToleranceFeet,
                options.EndpointSnapToleranceFeet,
                out List<string> cleanWarnings);

            footprint.Warnings.AddRange(cleanWarnings);

            if (cleanedLoop == null)
            {
                footprint.Status = IfcGeometryStatus.LoopCleanupFailed;
                footprint.Errors.Add("Loop cleanup produced an unusable result.");
                return footprint;
            }

            // ── 6. Validate ───────────────────────────────────────────────────
            bool valid = _loopValidator.Validate(
                cleanedLoop,
                minAreaFt2,
                options.EndpointSnapToleranceFeet,
                out List<string> validationWarnings);

            footprint.Warnings.AddRange(validationWarnings);

            if (!valid)
            {
                footprint.Status = IfcGeometryStatus.InvalidLoop;
                footprint.Errors.Add("Loop failed geometric validation. See warnings for details.");
                return footprint;
            }

            // ── 7. Area calculation ───────────────────────────────────────────
            double area = LoopAreaCalculator.ComputeAreaFt2(cleanedLoop);

            var mismatch = FootprintFaceSelector.AreaMismatchPercent(area * SquareMetresPerSquareFoot, declaredAreaM2);
            if (mismatch > AreaMismatchWarningPercent)
                footprint.Warnings.Add(
                    $"Footprint area {area * SquareMetresPerSquareFoot:0.#} m² differs from the declared IFC area " +
                    $"{declaredAreaM2:0.#} m² by {mismatch:0} % — check the space geometry.");

            // ── 8. Placement point ────────────────────────────────────────────
            var placement = _placementService.Find(cleanedLoop, levelElevationFeet);

            if (placement.Warning != null)
                footprint.Warnings.Add(placement.Warning);

            // ── 9. Populate result ────────────────────────────────────────────
            footprint.OuterLoop            = cleanedLoop;
            footprint.InnerLoops           = hostInnerLoops;
            footprint.OuterLoopCurveCount  = cleanedLoop.Count();
            footprint.ApproxAreaSquareFeet = area;
            footprint.PlacementPoint       = placement.Point;
            footprint.PlacementMethod      = placement.Method;

            // Status and Success:
            // GeometryReady  → loop valid and interior point found  → Success = true
            // PlacementPointFailed → loop valid but no interior point → Success = false
            //   (Phase 3 needs a placement point; without one room cannot be placed)
            footprint.Status  = placement.Success
                ? IfcGeometryStatus.GeometryReady
                : IfcGeometryStatus.PlacementPointFailed;
            footprint.Success = placement.Success;
        }
        catch (Exception ex)
        {
            footprint.Status = IfcGeometryStatus.Error;
            footprint.Errors.Add($"Unexpected exception: {ex.Message}");
        }

        return footprint;
    }

    // ─────────────────────────────────────────────────────────────────────────

    public const double SquareMetresPerSquareFoot = 0.09290304;

    /// <summary>Declared vs. extracted area difference that triggers a warning, in percent.</summary>
    public const double AreaMismatchWarningPercent = 20;

    /// <summary>
    /// The plan outline of the space at the chosen floor's elevation: the shadow of its solid
    /// (all solids united when there are several) projected down onto that plane. Accepted only when
    /// its area lies between the chosen floor face and the whole floor (+5 %), so a bad projection
    /// never replaces a usable face. Null when Revit cannot compute it.
    /// </summary>
    private static PlanarFace? PlanOutline(List<Solid> solids, Solid? ownerSolid, PlanarFace floor, FootprintFaceChoice choice)
    {
        try
        {
            Solid? solid = ownerSolid;
            if (solids.Count > 1)
            {
                Solid? union = null;
                foreach (var s in solids)
                {
                    union = union == null
                        ? s
                        : BooleanOperationsUtils.ExecuteBooleanOperation(union, s, BooleanOperationsType.Union);
                }
                solid = union ?? ownerSolid;
            }
            if (solid == null) return null;

            var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, floor.Origin.Z));
            var analyzer = ExtrusionAnalyzer.Create(solid, plane, XYZ.BasisZ);
            if (analyzer.GetExtrusionBase() is not PlanarFace outline) return null;

            var area = outline.Area;
            if (area < choice.AreaFt2 * 0.99 || area > choice.TotalFloorAreaFt2 * 1.05) return null;
            return outline;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Transforms every curve in <paramref name="loop"/> to host coordinates using
    /// <paramref name="transform"/> and returns a new <c>CurveLoop</c>.
    /// </summary>
    private static CurveLoop TransformLoop(CurveLoop loop, Transform transform)
    {
        if (transform.IsIdentity) return loop;

        var transformed = new CurveLoop();
        foreach (var curve in loop)
        {
            Curve hostCurve = curve.CreateTransformed(transform);
            transformed.Append(hostCurve);
        }
        return transformed;
    }
}
