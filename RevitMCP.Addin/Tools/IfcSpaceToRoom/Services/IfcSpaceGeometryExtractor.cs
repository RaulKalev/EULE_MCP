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
            // ── 1. Extract solids (and meshes, for the fallback) ──────────────
            var (solids, meshes) = _solidService.GetSolidsAndMeshes(linkedElement);

            if (solids.Count == 0 && meshes.Count == 0)
            {
                footprint.Status = IfcGeometryStatus.NoSolidGeometry;
                footprint.Errors.Add("No solid geometry found in element.");
                return footprint;
            }

            // ── 2. Find bottom face ───────────────────────────────────────────
            double minAreaFt2 = options.MinimumAreaSquareFeet;

            string? faceWarning = null;
            FootprintFaceChoice? choice = null;
            Solid? ownerSolid = null;
            var bottomFace = solids.Count > 0
                ? _faceFinder.Find(
                    solids,
                    out faceWarning,
                    out choice,
                    out ownerSolid,
                    options.HorizontalFaceToleranceDegrees,
                    minAreaFt2)
                : null;

            if (faceWarning != null && bottomFace != null)
                footprint.Warnings.Add(faceWarning);

            // A stepped or split floor: the largest floor face is only part of the space. Use the
            // plan outline (shadow) of the whole space instead when Revit can compute it.
            if (bottomFace != null && choice is { IsStepped: true })
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

            // No usable floor face, or one far from the declared area (a mesh body, faceted or
            // non-planar floor): project the whole space geometry, meshes included, onto the floor
            // plane and keep that outline when it is closer to the declared area (#68).
            var faceMismatch = bottomFace == null
                ? null
                : FootprintFaceSelector.AreaMismatchPercent(bottomFace.Area * SquareMetresPerSquareFoot, declaredAreaM2);
            if (bottomFace == null || faceMismatch > AreaMismatchWarningPercent)
            {
                var shadow = GeometryShadow(solids, meshes, bottomFace?.Origin.Z);
                var shadowMismatch = shadow == null
                    ? null
                    : FootprintFaceSelector.AreaMismatchPercent(shadow.Area * SquareMetresPerSquareFoot, declaredAreaM2);
                var better = shadow != null && (bottomFace == null || (shadowMismatch != null && shadowMismatch < faceMismatch));
                if (better)
                {
                    footprint.Warnings.Add(
                        $"The footprint is the plan outline of the whole space geometry ({DescribeGeometry(solids, meshes)}); " +
                        "no flat floor face matched the space.");
                    bottomFace = shadow;
                }
                else if (bottomFace != null)
                {
                    footprint.Warnings.Add($"Space geometry: {DescribeGeometry(solids, meshes)}.");
                }
            }

            if (bottomFace == null)
            {
                footprint.Status = IfcGeometryStatus.NoUsableBottomFace;
                footprint.Errors.Add((faceWarning ?? "No horizontal bottom face found.") +
                                     $" Space geometry: {DescribeGeometry(solids, meshes)}.");
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
    /// The plan shadow of all the space's geometry: solids as they are, meshes rebuilt as solids
    /// (TessellatedShapeBuilder), united and projected onto a horizontal plane at
    /// <paramref name="floorZ"/> (default: the lowest point of the geometry). Null when Revit cannot
    /// build it.
    /// </summary>
    private static PlanarFace? GeometryShadow(List<Solid> solids, List<Mesh> meshes, double? floorZ)
    {
        var bodies = new List<Solid>(solids);
        foreach (var mesh in meshes)
        {
            var built = MeshToSolid(mesh);
            if (built != null) bodies.Add(built);
        }
        if (bodies.Count == 0) return null;

        var z = floorZ ?? LowestZ(bodies, meshes);
        var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, z));

        // Prefer the shadow of everything united; if the union fails, take the largest single shadow.
        Solid? united = null;
        try
        {
            foreach (var body in bodies)
                united = united == null ? body : BooleanOperationsUtils.ExecuteBooleanOperation(united, body, BooleanOperationsType.Union);
        }
        catch { united = null; }

        var candidates = united != null ? new List<Solid> { united } : bodies;
        PlanarFace? best = null;
        foreach (var body in candidates)
        {
            try
            {
                if (ExtrusionAnalyzer.Create(body, plane, XYZ.BasisZ).GetExtrusionBase() is PlanarFace face &&
                    (best == null || face.Area > best.Area))
                    best = face;
            }
            catch { /* this body has no usable shadow */ }
        }
        return best;
    }

    /// <summary>Rebuilds a closed mesh as a solid; null when the mesh is open or Revit rejects it.</summary>
    private static Solid? MeshToSolid(Mesh mesh)
    {
        try
        {
            var builder = new TessellatedShapeBuilder();
            builder.OpenConnectedFaceSet(true);
            for (int i = 0; i < mesh.NumTriangles; i++)
            {
                var t = mesh.get_Triangle(i);
                builder.AddFace(new TessellatedFace(new List<XYZ> { t.get_Vertex(0), t.get_Vertex(1), t.get_Vertex(2) }, ElementId.InvalidElementId));
            }
            builder.CloseConnectedFaceSet();
            builder.Target = TessellatedShapeBuilderTarget.Solid;
            builder.Fallback = TessellatedShapeBuilderFallback.Abort;
            builder.Build();
            return builder.GetBuildResult().GetGeometricalObjects().OfType<Solid>().FirstOrDefault(s => s.Volume > 0);
        }
        catch
        {
            return null;
        }
    }

    private static double LowestZ(List<Solid> solids, List<Mesh> meshes)
    {
        var z = double.MaxValue;
        foreach (var solid in solids)
        {
            try
            {
                var box = solid.GetBoundingBox();
                z = Math.Min(z, box.Transform.OfPoint(box.Min).Z);
            }
            catch { }
        }
        foreach (var mesh in meshes)
            foreach (var v in mesh.Vertices)
                z = Math.Min(z, v.Z);
        return z == double.MaxValue ? 0 : z;
    }

    /// <summary>"2 solid(s), 1 mesh(es), 14 non-planar face(s), floor faces 1.1 m²" — for diagnostics.</summary>
    private static string DescribeGeometry(List<Solid> solids, List<Mesh> meshes)
    {
        int nonPlanar = 0, floorFaces = 0;
        double floorArea = 0;
        foreach (var solid in solids)
        {
            foreach (Face face in solid.Faces)
            {
                if (face is not PlanarFace pf) { nonPlanar++; continue; }
                if (pf.FaceNormal.Z < -0.99) { floorFaces++; floorArea += pf.Area; }
            }
        }
        return $"{solids.Count} solid(s), {meshes.Count} mesh(es), {nonPlanar} non-planar face(s), " +
               $"{floorFaces} flat floor face(s) totalling {floorArea * SquareMetresPerSquareFoot:0.#} m²";
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
