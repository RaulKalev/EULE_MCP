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
    public IfcSpaceFootprint Extract(
        Element linkedElement,
        Transform linkTransform,
        double levelElevationFeet,
        IfcGeometryExtractionOptions options)
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
                options.HorizontalFaceToleranceDegrees,
                minAreaFt2);

            if (bottomFace == null)
            {
                footprint.Status = IfcGeometryStatus.NoUsableBottomFace;
                footprint.Errors.Add(faceWarning ?? "No horizontal bottom face found.");
                return footprint;
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
