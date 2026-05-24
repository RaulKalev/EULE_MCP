using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>
/// Finds the best bottom horizontal planar face from a collection of solids.
/// "Bottom" = lowest elevation horizontal face. Horizontal = face normal is within
/// <paramref name="toleranceDegrees"/> of vertical.
/// Phase 2: read-only.
/// </summary>
public class BottomFaceFinder
{
    /// <summary>
    /// Finds the most suitable bottom face from <paramref name="solids"/>.
    /// </summary>
    /// <param name="solids">Solids from <c>SolidExtractionService</c>.</param>
    /// <param name="toleranceDegrees">
    /// Maximum angle in degrees between the face normal and the Z axis for the face
    /// to be classified as horizontal. Default 2°.
    /// </param>
    /// <param name="minimumAreaFt2">
    /// Minimum face area in square feet. Faces below this threshold are ignored.
    /// </param>
    /// <param name="warning">Set to a diagnostic string when no face is found.</param>
    public PlanarFace? Find(
        IEnumerable<Solid> solids,
        double toleranceDegrees   = 2.0,
        double minimumAreaFt2     = 0.0,   // caller converts from m²
        out string? warning)
    {
        warning = null;

        // cos(toleranceDegrees) gives the minimum |dot(normal, Z)| for a horizontal face.
        // e.g. tolerance = 2° → cos(2°) ≈ 0.9994
        double minNormalZ = Math.Cos(toleranceDegrees * Math.PI / 180.0);

        PlanarFace? bestFace    = null;
        double      bestZ       = double.MaxValue;
        double      bestArea    = 0.0;
        int         totalFaces  = 0;
        int         hFaceCount  = 0;

        foreach (var solid in solids)
        {
            foreach (Face face in solid.Faces)
            {
                totalFaces++;
                if (face is not PlanarFace pf) continue;

                // Face normal must be nearly vertical (|Z component| ≥ threshold)
                var normal = pf.FaceNormal;
                if (Math.Abs(normal.Z) < minNormalZ) continue;
                hFaceCount++;

                // Skip faces smaller than the minimum area threshold
                double area = pf.Area;
                if (area < minimumAreaFt2) continue;

                // Face origin gives a representative Z elevation for the face plane.
                // For the bottom face of a simple extrusion this is the floor elevation.
                double z = pf.Origin.Z;

                // Selection rule:
                //   1. Prefer the face with the lowest Z (= floor)
                //   2. Among faces at the same elevation, prefer the largest area
                bool isBetter = bestFace == null
                    || z < bestZ - 1e-6
                    || (Math.Abs(z - bestZ) < 1e-6 && area > bestArea);

                if (isBetter)
                {
                    bestFace = pf;
                    bestZ    = z;
                    bestArea = area;
                }
            }
        }

        if (bestFace == null)
        {
            warning = totalFaces == 0
                ? "Solid has no faces."
                : hFaceCount == 0
                    ? $"No horizontal face found in {totalFaces} face(s) — all faces are non-vertical normals."
                    : $"All {hFaceCount} horizontal face(s) were smaller than the minimum area threshold.";
        }

        return bestFace;
    }
}
