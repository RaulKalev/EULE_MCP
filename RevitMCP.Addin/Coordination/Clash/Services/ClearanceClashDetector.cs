using RevitMCP.Addin.Coordination.Clash.DTOs;
using RevitMCP.Addin.Coordination.Clash.Geometry;

namespace RevitMCP.Addin.Coordination.Clash.Services;

/// <summary>
/// Clearance clash detection via expanded bounding-box approximation.
/// NOTE: This is an approximation — see warnings in results.
/// </summary>
public class ClearanceClashDetector
{
    private const string ApproximationWarning =
        "MVP clearance detection uses expanded bounding-box approximation — reported distances are conservative estimates, not true surface-to-surface measurements.";

    public (List<ClashResultDto> clashes, List<string> warnings) Detect(
        List<ClashCandidateInfo> sources,
        List<ClashCandidateInfo> targets,
        string ruleName,
        string severity,
        double clearanceMm,
        int limit,
        int maxPairs)
    {
        var clashes = new List<ClashResultDto>();
        var warnings = new List<string> { ApproximationWarning };
        int pairCount = 0;
        int clashIndex = 0;

        foreach (var src in sources)
        {
            if (src.BoundingBox == null) continue;
            var expandedSrc = ClashBoundingBoxHelper.Expand(src.BoundingBox, clearanceMm);

            foreach (var tgt in targets)
            {
                if (tgt.BoundingBox == null) continue;
                if (src.OwnerDocument == tgt.OwnerDocument && src.ElementId == tgt.ElementId) continue;

                pairCount++;
                if (maxPairs > 0 && pairCount > maxPairs)
                {
                    warnings.Add($"maxPairs limit ({maxPairs}) reached — detection stopped early.");
                    return (clashes, warnings);
                }

                if (!ClashBoundingBoxHelper.Overlaps(expandedSrc, tgt.BoundingBox)) continue;

                clashIndex++;
                if (limit > 0 && clashes.Count >= limit)
                {
                    warnings.Add($"Result limit ({limit}) reached.");
                    return (clashes, warnings);
                }

                var distMm = ClashBoundingBoxHelper.ApproximateDistanceMm(src.BoundingBox, tgt.BoundingBox);
                var (lx, ly, lz) = ClashLocationResolver.ResolveMeters(src.BoundingBox, tgt.BoundingBox);

                clashes.Add(new ClashResultDto
                {
                    ClashId = $"CL-{clashIndex:D4}",
                    RuleName = ruleName,
                    ClashType = "Clearance",
                    Severity = severity,
                    Source = BuildRef(src),
                    Target = BuildRef(tgt),
                    Location = new ClashLocationDto { X = lx, Y = ly, Z = lz },
                    DistanceMm = Math.Round(distMm, 1),
                    RequiredClearanceMm = clearanceMm,
                    Status = "New",
                    Message = $"{src.Category} within {clearanceMm}mm clearance of {tgt.Category} (approx)."
                });
            }
        }

        return (clashes, warnings);
    }

    private static ClashElementRefDto BuildRef(ClashCandidateInfo c) => new()
    {
        ElementId = c.ElementId,
        Category = c.Category,
        Model = c.Model,
        LinkInstanceId = c.LinkInstanceId,
        LinkName = c.LinkName
    };
}
