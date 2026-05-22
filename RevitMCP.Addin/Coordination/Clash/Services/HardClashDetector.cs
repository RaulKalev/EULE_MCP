using Autodesk.Revit.DB;
using RevitMCP.Addin.Coordination.Clash.DTOs;
using RevitMCP.Addin.Coordination.Clash.Geometry;

namespace RevitMCP.Addin.Coordination.Clash.Services;

public class HardClashDetector
{
    public (List<ClashResultDto> clashes, List<string> warnings) Detect(
        List<ClashCandidateInfo> sources,
        List<ClashCandidateInfo> targets,
        string ruleName,
        string severity,
        double toleranceMm,
        int limit,
        int maxPairs)
    {
        var clashes = new List<ClashResultDto>();
        var warnings = new List<string>();
        int pairCount = 0;
        int clashIndex = 0;
        // Tolerance: volume in cubic feet for intersection test
        double tolFeet = toleranceMm / 304.8;
        double tolVolume = tolFeet * tolFeet * tolFeet;

        foreach (var src in sources)
        {
            if (src.BoundingBox == null) continue;
            foreach (var tgt in targets)
            {
                if (tgt.BoundingBox == null) continue;
                if (src.OwnerDocument == tgt.OwnerDocument && src.ElementId == tgt.ElementId) continue;

                pairCount++;
                if (maxPairs > 0 && pairCount > maxPairs)
                {
                    warnings.Add($"maxPairs limit ({maxPairs}) reached — detection stopped early. Results may be incomplete.");
                    return (clashes, warnings);
                }

                // Bounding-box precheck
                if (!ClashBoundingBoxHelper.Overlaps(src.BoundingBox, tgt.BoundingBox)) continue;

                // Attempt solid intersection
                bool intersects = false;
                double? volume = null;
                try
                {
                    var srcElem = src.OwnerDocument.GetElement(new ElementId(src.ElementId));
                    var tgtElem = tgt.OwnerDocument.GetElement(new ElementId(tgt.ElementId));

                    if (srcElem != null && tgtElem != null)
                    {
                        var srcSolid = ClashSolidExtractor.TryExtract(srcElem, out var sw);
                        var tgtSolid = ClashSolidExtractor.TryExtract(tgtElem, out var tw);
                        if (sw != null) warnings.Add(sw);
                        if (tw != null) warnings.Add(tw);

                        if (srcSolid != null && tgtSolid != null)
                        {
                            var workSrc = src.LinkTransform.IsIdentity
                                ? srcSolid
                                : SolidUtils.CreateTransformed(srcSolid, src.LinkTransform);
                            var workTgt = tgt.LinkTransform.IsIdentity
                                ? tgtSolid
                                : SolidUtils.CreateTransformed(tgtSolid, tgt.LinkTransform);

                            var intersection = BooleanOperationsUtils.ExecuteBooleanOperation(
                                workSrc, workTgt, BooleanOperationsType.Intersect);

                            if (intersection != null && intersection.Volume > tolVolume)
                            {
                                intersects = true;
                                volume = intersection.Volume * (304.8 * 304.8 * 304.8); // cubic feet → cubic mm
                            }
                        }
                        else
                        {
                            intersects = true; // bbox overlap with no solid — report conservatively
                        }
                    }
                }
                catch
                {
                    intersects = true;
                    warnings.Add($"Solid intersection failed for pair ({src.ElementId}, {tgt.ElementId}) — using bounding-box result.");
                }

                if (!intersects) continue;

                clashIndex++;
                if (limit > 0 && clashes.Count >= limit)
                {
                    warnings.Add($"Result limit ({limit}) reached.");
                    return (clashes, warnings);
                }

                var (lx, ly, lz) = ClashLocationResolver.ResolveMeters(src.BoundingBox, tgt.BoundingBox);
                clashes.Add(new ClashResultDto
                {
                    ClashId = $"CL-{clashIndex:D4}",
                    RuleName = ruleName,
                    ClashType = "HardClash",
                    Severity = severity,
                    Source = BuildRef(src),
                    Target = BuildRef(tgt),
                    Location = new ClashLocationDto { X = lx, Y = ly, Z = lz },
                    IntersectionVolume = volume,
                    Status = "New",
                    Message = $"{src.Category} intersects {tgt.Category}."
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
