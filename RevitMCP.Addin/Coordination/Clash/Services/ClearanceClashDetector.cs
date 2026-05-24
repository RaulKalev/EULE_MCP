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
        "Clearance detection uses expanded bounding-box approximation. Results should be visually reviewed.";

    public (List<ClashResultDto> clashes, List<string> warnings) Detect(
        List<ClashCandidateInfo> sources,
        List<ClashCandidateInfo> targets,
        string ruleName,
        string severity,
        double clearanceMm,
        int limit,
        int maxPairs,
        string distanceMode = "BoundingBoxApproximation",
        CancellationToken cancellationToken = default)
    {
        var clashes = new List<ClashResultDto>();
        var warnings = new List<string> { ApproximationWarning };

        bool useCentroid = distanceMode.Equals("SolidCentroidApproximation", StringComparison.OrdinalIgnoreCase);
        if (useCentroid)
            warnings.Add("distanceMode=SolidCentroidApproximation: using bounding-box centroid distance as centroid approximation. Solid geometry is not evaluated.");

        int pairCount = 0;
        int clashIndex = 0;

        if (cancellationToken.IsCancellationRequested)
        {
            warnings.Add("Clearance clash detection cancelled before it started.");
            return (clashes, warnings);
        }

        foreach (var src in sources)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                warnings.Add($"Clearance clash detection cancelled mid-run — {clashes.Count} clash(es) found before cancellation. Results are partial.");
                return (clashes, warnings);
            }
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

                var distMm = useCentroid
                    ? ComputeCentroidDistanceMm(src.BoundingBox, tgt.BoundingBox)
                    : ClashBoundingBoxHelper.ApproximateDistanceMm(src.BoundingBox, tgt.BoundingBox);
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
                    DetectionMethod = useCentroid ? "CentroidApproximation" : "ExpandedBoundingBox",
                    Confidence = useCentroid ? "Low" : "Medium",
                    Status = "New",
                    Message = $"{src.Category} within {clearanceMm}mm clearance of {tgt.Category} ({(useCentroid ? "centroid" : "bbox")} approx, ~{Math.Round(distMm, 0)}mm)."
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

    private static double ComputeCentroidDistanceMm(
        Autodesk.Revit.DB.BoundingBoxXYZ a,
        Autodesk.Revit.DB.BoundingBoxXYZ b)
    {
        const double FeetToMm = 304.8;
        var cx1 = (a.Min.X + a.Max.X) / 2.0;
        var cy1 = (a.Min.Y + a.Max.Y) / 2.0;
        var cz1 = (a.Min.Z + a.Max.Z) / 2.0;
        var cx2 = (b.Min.X + b.Max.X) / 2.0;
        var cy2 = (b.Min.Y + b.Max.Y) / 2.0;
        var cz2 = (b.Min.Z + b.Max.Z) / 2.0;
        var dx = cx2 - cx1; var dy = cy2 - cy1; var dz = cz2 - cz1;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz) * FeetToMm;
    }
}
