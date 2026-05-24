using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>
/// Cleans an extracted CurveLoop so it is more likely to be accepted by Revit's room
/// boundary placement API in Phase 3.
/// Phase 2: read-only — creates new curve objects but does not write to the document.
/// </summary>
public class CurveLoopCleaner
{
    /// <summary>
    /// Cleans <paramref name="inputLoop"/> and returns a new CurveLoop.
    /// </summary>
    /// <param name="inputLoop">Raw loop from <c>CurveLoopExtractor</c>.</param>
    /// <param name="tinySegmentThresholdFeet">
    /// Segments shorter than this are removed. In Revit internal feet.
    /// </param>
    /// <param name="snapToleranceFeet">
    /// Endpoint gaps smaller than this are snapped (connected). In Revit internal feet.
    /// </param>
    /// <param name="warnings">Advisory messages about changes made during cleanup.</param>
    /// <returns>
    /// A cleaned <c>CurveLoop</c>, or null if cleanup failed to produce a usable result.
    /// </returns>
    public CurveLoop? Clean(
        CurveLoop inputLoop,
        double tinySegmentThresholdFeet,
        double snapToleranceFeet,
        out List<string> warnings)
    {
        warnings = new List<string>();

        // ── Step 1: Collect curves, remove tiny segments ──────────────────────
        var rawCurves = inputLoop.ToList();
        var kept      = new List<Curve>();
        int removed   = 0;

        foreach (var curve in rawCurves)
        {
            double len;
            try { len = curve.Length; }
            catch { len = 0.0; }

            if (len < tinySegmentThresholdFeet)
            {
                removed++;
            }
            else
            {
                kept.Add(curve);
            }
        }

        if (removed > 0)
            warnings.Add($"Removed {removed} tiny segment(s) shorter than {tinySegmentThresholdFeet * 304.8:F1} mm.");

        if (kept.Count < 3)
        {
            warnings.Add($"Only {kept.Count} segment(s) remain after removing tiny segments (need ≥ 3).");
            return null;
        }

        // ── Step 2: Snap consecutive endpoint gaps ────────────────────────────
        var snapped   = new List<Curve>(kept.Count);
        int snapCount = 0;

        for (int i = 0; i < kept.Count; i++)
        {
            var current = kept[i];
            var next    = kept[(i + 1) % kept.Count];

            XYZ currentEnd = current.GetEndPoint(1);
            XYZ nextStart  = next.GetEndPoint(0);
            double gap     = currentEnd.DistanceTo(nextStart);

            if (gap < 1e-10)
            {
                // Already connected — add as-is
                snapped.Add(current);
            }
            else if (gap <= snapToleranceFeet)
            {
                // Snap: create a version of 'next' that starts exactly at currentEnd.
                // Only supported for Lines in Phase 2. Arcs are left as-is.
                if (current is Line currentLine)
                {
                    // Create new Line sharing the same start but ending at nextStart
                    try
                    {
                        var snappedLine = Line.CreateBound(
                            currentLine.GetEndPoint(0),
                            nextStart);  // snap end to next start
                        snapped.Add(snappedLine);
                        snapCount++;
                    }
                    catch
                    {
                        snapped.Add(current);
                    }
                }
                else
                {
                    // Arc or other — accept gap with warning, add as-is
                    snapped.Add(current);
                    warnings.Add(
                        $"Gap of {gap * 304.8:F2} mm at segment {i + 1} could not be snapped " +
                        "(non-line curve). Accepted within tolerance.");
                }
            }
            else
            {
                // Gap exceeds tolerance — add as-is and report warning
                snapped.Add(current);
                warnings.Add(
                    $"Endpoint gap of {gap * 304.8:F2} mm at segment {i + 1} exceeds snap " +
                    $"tolerance ({snapToleranceFeet * 304.8:F1} mm). Loop may not be perfectly closed.");
            }
        }

        if (snapCount > 0)
            warnings.Add($"Snapped {snapCount} endpoint gap(s) to improve loop closure.");

        // ── Step 3: Rebuild CurveLoop ─────────────────────────────────────────
        try
        {
            var result = new CurveLoop();
            foreach (var c in snapped)
                result.Append(c);
            return result;
        }
        catch (Exception ex)
        {
            warnings.Add($"Failed to build cleaned CurveLoop: {ex.Message}");
            return null;
        }
    }
}
