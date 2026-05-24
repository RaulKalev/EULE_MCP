using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>
/// Validates whether a CurveLoop is suitable for room boundary creation.
/// Phase 2: read-only, geometry analysis only.
/// </summary>
public class CurveLoopValidator
{
    // Minimum segments in a valid room footprint
    private const int MinSegments = 3;

    /// <summary>
    /// Validates <paramref name="loop"/> and populates <paramref name="warnings"/> with
    /// any non-fatal advisories.
    /// </summary>
    /// <param name="loop">Cleaned CurveLoop to validate.</param>
    /// <param name="minimumAreaFt2">Minimum required area in square feet.</param>
    /// <param name="closureToleranceFeet">
    /// Maximum gap allowed between the last curve's end and first curve's start
    /// for the loop to be considered closed.
    /// </param>
    /// <param name="warnings">Populated with non-fatal advisory messages.</param>
    /// <returns>
    /// True if the loop passes all checks (may still have warnings).
    /// False if a blocking failure was detected.
    /// </returns>
    public bool Validate(
        CurveLoop loop,
        double minimumAreaFt2,
        double closureToleranceFeet,
        out List<string> warnings)
    {
        warnings = new List<string>();

        var curves = loop.ToList();

        // ── Check: minimum segment count ─────────────────────────────────────
        if (curves.Count < MinSegments)
        {
            warnings.Add($"Loop has only {curves.Count} segment(s); minimum is {MinSegments}.");
            return false;
        }

        // ── Check: all curves are bound ───────────────────────────────────────
        foreach (var c in curves)
        {
            if (!c.IsBound)
            {
                warnings.Add("Loop contains an unbound (infinite) curve. Loop is invalid.");
                return false;
            }
        }

        // ── Check: loop closure ───────────────────────────────────────────────
        var firstStart = curves[0].GetEndPoint(0);
        var lastEnd    = curves[curves.Count - 1].GetEndPoint(1);
        double closure = firstStart.DistanceTo(lastEnd);

        if (closure > closureToleranceFeet)
        {
            warnings.Add(
                $"Loop is not closed — gap between first start and last end is " +
                $"{closure * 304.8:F2} mm (tolerance {closureToleranceFeet * 304.8:F1} mm).");
            return false;
        }

        // ── Check: consecutive endpoint connectivity ───────────────────────────
        for (int i = 0; i < curves.Count - 1; i++)
        {
            double gap = curves[i].GetEndPoint(1).DistanceTo(curves[i + 1].GetEndPoint(0));
            if (gap > closureToleranceFeet)
            {
                warnings.Add(
                    $"Gap of {gap * 304.8:F2} mm between segment {i + 1} and {i + 2} " +
                    $"(tolerance {closureToleranceFeet * 304.8:F1} mm).");
                return false;
            }
        }

        // ── Check: coplanarity ────────────────────────────────────────────────
        // For Phase 2 MVP, check that all endpoints share approximately the same Z.
        double baseZ = curves[0].GetEndPoint(0).Z;
        bool zVariance = false;
        foreach (var c in curves)
        {
            double z0 = c.GetEndPoint(0).Z;
            double z1 = c.GetEndPoint(1).Z;
            if (Math.Abs(z0 - baseZ) > closureToleranceFeet ||
                Math.Abs(z1 - baseZ) > closureToleranceFeet)
            {
                zVariance = true;
                break;
            }
        }

        if (zVariance)
        {
            warnings.Add(
                "Loop curves are not coplanar (Z values vary beyond tolerance). " +
                "The loop may need to be projected to the level elevation before Phase 3.");
            // Not a hard failure — issue a warning and continue
        }

        // ── Check: area above minimum ─────────────────────────────────────────
        double area = LoopAreaCalculator.ComputeAreaFt2(loop);
        if (area < minimumAreaFt2)
        {
            warnings.Add(
                $"Footprint area {area * 0.092903:F2} m² is below the minimum " +
                $"{minimumAreaFt2 * 0.092903:F2} m².");
            return false;
        }

        // ── Check: MVP self-intersection (lines only) ─────────────────────────
        var intersectionWarning = CheckSelfIntersection(curves);
        if (intersectionWarning != null)
            warnings.Add(intersectionWarning);

        return true;
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Checks for obvious self-intersections between non-adjacent Line segments.
    /// Arcs are skipped with a note. Returns a warning string or null if clean.
    /// </summary>
    private static string? CheckSelfIntersection(List<Curve> curves)
    {
        var lines = new List<(int Index, XYZ P0, XYZ P1)>();
        bool hasArcs = false;

        for (int i = 0; i < curves.Count; i++)
        {
            if (curves[i] is Line ln)
                lines.Add((i, ln.GetEndPoint(0), ln.GetEndPoint(1)));
            else
                hasArcs = true;
        }

        // Check all pairs of non-adjacent lines for 2D intersection
        for (int a = 0; a < lines.Count; a++)
        {
            for (int b = a + 2; b < lines.Count; b++)
            {
                // Skip the wrap-around adjacent pair (last, first)
                if (a == 0 && b == lines.Count - 1) continue;

                if (SegmentsIntersect2D(lines[a].P0, lines[a].P1, lines[b].P0, lines[b].P1))
                {
                    return $"Possible self-intersection detected between segment {lines[a].Index + 1} " +
                           $"and segment {lines[b].Index + 1} (XY plane check). " +
                           "Review the footprint before Phase 3 conversion.";
                }
            }
        }

        if (hasArcs)
            return "Loop contains arc segments — full self-intersection check skipped for arcs. Verify manually.";

        return null;
    }

    /// <summary>
    /// 2-D segment intersection test (XY projection only). Ignores Z.
    /// Returns true if the two segments properly intersect (not at shared endpoints).
    /// </summary>
    private static bool SegmentsIntersect2D(XYZ a0, XYZ a1, XYZ b0, XYZ b1)
    {
        double ax = a1.X - a0.X, ay = a1.Y - a0.Y;
        double bx = b1.X - b0.X, by = b1.Y - b0.Y;
        double denom = ax * by - ay * bx;

        if (Math.Abs(denom) < 1e-12) return false; // parallel

        double cx = b0.X - a0.X, cy = b0.Y - a0.Y;
        double t = (cx * by - cy * bx) / denom;
        double u = (cx * ay - cy * ax) / denom;

        // Intersection happens when both parameters are strictly in (0,1)
        // (we skip exact-endpoint intersections which are normal for a loop)
        const double eps = 1e-6;
        return t > eps && t < 1 - eps && u > eps && u < 1 - eps;
    }
}
