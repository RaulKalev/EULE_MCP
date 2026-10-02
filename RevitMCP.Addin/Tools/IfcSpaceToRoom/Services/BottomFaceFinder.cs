using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>
/// Finds the floor face of a space from its solids. The choice itself — largest downward-facing
/// horizontal face, lowest elevation as tie-break — lives in <see cref="FootprintFaceSelector"/>
/// so it can be unit tested (#68).
/// Phase 2: read-only.
/// </summary>
public class BottomFaceFinder
{
    /// <summary>
    /// Finds the most suitable floor face from <paramref name="solids"/>.
    /// </summary>
    /// <param name="solids">Solids from <c>SolidExtractionService</c>.</param>
    /// <param name="warning">Set to a diagnostic string when no face is found.</param>
    /// <param name="toleranceDegrees">
    /// Maximum angle in degrees between the face normal and the Z axis for the face
    /// to be classified as horizontal. Default 2°.
    /// </param>
    /// <param name="minimumAreaFt2">
    /// Minimum face area in square feet. Faces below this threshold are ignored.
    /// </param>
    public PlanarFace? Find(
        IEnumerable<Solid> solids,
        out string? warning,
        double toleranceDegrees   = 2.0,
        double minimumAreaFt2     = 0.0)   // caller converts from m²
        => Find(solids, out warning, out _, out _, toleranceDegrees, minimumAreaFt2);

    /// <summary>
    /// As <see cref="Find(IEnumerable{Solid}, out string?, double, double)"/>, also returning the
    /// floor analysis and the solid the chosen face belongs to.
    /// </summary>
    public PlanarFace? Find(
        IEnumerable<Solid> solids,
        out string? warning,
        out FootprintFaceChoice? choice,
        out Solid? ownerSolid,
        double toleranceDegrees = 2.0,
        double minimumAreaFt2   = 0.0)
    {
        warning = null;
        choice = null;
        ownerSolid = null;

        // cos(toleranceDegrees) gives the minimum |dot(normal, Z)| for a horizontal face.
        double minNormalZ = Math.Cos(toleranceDegrees * Math.PI / 180.0);

        var faces = new List<(PlanarFace Face, Solid Solid)>();
        var candidates = new List<FaceCandidate>();
        int totalFaces = 0;

        foreach (var solid in solids)
        {
            foreach (Face face in solid.Faces)
            {
                totalFaces++;
                if (face is not PlanarFace pf) continue;
                candidates.Add(new FaceCandidate(faces.Count, pf.Area, pf.FaceNormal.Z, pf.Origin.Z));
                faces.Add((pf, solid));
            }
        }

        choice = FootprintFaceSelector.Choose(candidates, minNormalZ, minimumAreaFt2);
        if (choice == null)
        {
            var horizontal = candidates.Count(c => Math.Abs(c.NormalZ) >= minNormalZ);
            warning = totalFaces == 0
                ? "Solid has no faces."
                : horizontal == 0
                    ? $"No horizontal face found in {totalFaces} face(s) — all faces are non-vertical normals."
                    : $"All {horizontal} horizontal face(s) were smaller than the minimum area threshold.";
            return null;
        }

        if (!choice.FromDownwardFaces)
            warning = "No downward-facing floor face — the solid's normals may be inverted; the largest horizontal face was used.";

        ownerSolid = faces[choice.Index].Solid;
        return faces[choice.Index].Face;
    }
}
