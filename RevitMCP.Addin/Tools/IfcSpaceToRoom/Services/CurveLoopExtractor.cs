using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>
/// Extracts the outer (and optionally inner) CurveLoops from a PlanarFace.
/// Phase 2: read-only.
/// </summary>
public class CurveLoopExtractor
{
    /// <summary>
    /// Extracts loops from <paramref name="face"/>.
    /// The largest loop (by perimeter) is returned as the outer boundary.
    /// Any remaining loops are treated as inner loops (holes).
    /// </summary>
    /// <param name="face">The bottom planar face selected by <c>BottomFaceFinder</c>.</param>
    /// <param name="outerLoop">The outer boundary loop, or null on failure.</param>
    /// <param name="innerLoops">Any inner loops (holes); may be empty.</param>
    /// <param name="warnings">Advisory messages populated during extraction.</param>
    public void Extract(
        PlanarFace face,
        out CurveLoop? outerLoop,
        out List<CurveLoop> innerLoops,
        out List<string> warnings)
    {
        outerLoop  = null;
        innerLoops = new List<CurveLoop>();
        warnings   = new List<string>();

        IList<CurveLoop> loops;
        try
        {
            loops = face.GetEdgesAsCurveLoops();
        }
        catch (Exception ex)
        {
            warnings.Add($"GetEdgesAsCurveLoops failed: {ex.Message}");
            return;
        }

        if (loops == null || loops.Count == 0)
        {
            warnings.Add("Face returned no edge loops.");
            return;
        }

        // Select largest loop (by perimeter) as the outer boundary.
        // Sort descending by perimeter; first = largest = outer.
        var sorted = loops
            .Select(l => (Loop: l, Perimeter: SafeGetLength(l)))
            .OrderByDescending(t => t.Perimeter)
            .ToList();

        outerLoop = sorted[0].Loop;

        for (int i = 1; i < sorted.Count; i++)
            innerLoops.Add(sorted[i].Loop);

        if (innerLoops.Count > 0)
        {
            warnings.Add(
                $"Face has {innerLoops.Count} inner loop(s) (possible holes/voids). " +
                "Using only the outer boundary in Phase 2. Inner loops will be ignored during Phase 3 room creation.");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static double SafeGetLength(CurveLoop loop)
    {
        try   { return loop.GetExactLength(); }
        catch { return 0.0; }
    }
}
