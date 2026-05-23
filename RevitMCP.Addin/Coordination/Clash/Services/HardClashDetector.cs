using Autodesk.Revit.DB;
using RevitMCP.Addin.Coordination.Clash.DTOs;
using RevitMCP.Addin.Coordination.Clash.Geometry;

namespace RevitMCP.Addin.Coordination.Clash.Services;

public class HardClashDetector
{
    private const string StrictModeNotice =
        "Hard clash detection uses bounding-box overlap only as a candidate pre-check. Reported hard clashes require successful solid intersection above tolerance.";

    public (List<ClashResultDto> clashes, List<string> warnings) Detect(
        List<ClashCandidateInfo> sources,
        List<ClashCandidateInfo> targets,
        string ruleName,
        string severity,
        double toleranceMm,
        int limit,
        int maxPairs,
        bool allowBoundingBoxFallback = false,
        CancellationToken cancellationToken = default)
    {
        var clashes = new List<ClashResultDto>();
        var warnings = new List<string> { StrictModeNotice };
        int pairCount = 0;
        int clashIndex = 0;
        int skippedNoSolid = 0;
        int skippedBooleanFailure = 0;
        int skippedBelowTolerance = 0;
        int solidIntersectionCount = 0;
        // Tolerance: volume in cubic feet for intersection test
        double tolFeet = toleranceMm / 304.8;
        double tolVolume = tolFeet * tolFeet * tolFeet;

        if (cancellationToken.IsCancellationRequested)
        {
            warnings.Add("Hard clash detection cancelled before it started.");
            return (clashes, warnings);
        }

        foreach (var src in sources)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                warnings.Add($"Hard clash detection cancelled mid-run — {clashes.Count} clash(es) found before cancellation. Results are partial.");
                return (clashes, warnings);
            }
            if (src.BoundingBox == null) continue;
            foreach (var tgt in targets)
            {
                if (tgt.BoundingBox == null) continue;
                if (src.OwnerDocument == tgt.OwnerDocument && src.ElementId == tgt.ElementId) continue;

                pairCount++;
                if (maxPairs > 0 && pairCount > maxPairs)
                {
                    warnings.Add($"maxPairs limit ({maxPairs}) reached — detection stopped early. Results may be incomplete.");
                    goto done;
                }

                // Bounding-box precheck (candidate filter only)
                if (!ClashBoundingBoxHelper.Overlaps(src.BoundingBox, tgt.BoundingBox)) continue;

                // Attempt solid intersection
                bool? solidResult = null;  // true=intersects, false=no-intersect, null=skipped
                double? volume = null;
                string? skipReason = null;
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

                        if (srcSolid == null || tgtSolid == null)
                        {
                            skippedNoSolid++;
                            skipReason = "no-solid";
                        }
                        else
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
                                solidResult = true;
                                volume = intersection.Volume * (304.8 * 304.8 * 304.8); // cubic feet → cubic mm
                                solidIntersectionCount++;
                            }
                            else
                            {
                                solidResult = false;
                                skippedBelowTolerance++;
                            }
                        }
                    }
                }
                catch
                {
                    skippedBooleanFailure++;
                    skipReason = "boolean-failure";
                }

                // Strict mode: only report when solid intersection confirmed
                if (solidResult == true)
                {
                    clashIndex++;
                    if (limit > 0 && clashes.Count >= limit)
                    {
                        warnings.Add($"Result limit ({limit}) reached.");
                        goto done;
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
                        DetectionMethod = "SolidIntersection",
                        Confidence = "High",
                        Status = "New",
                        Message = $"{src.Category} intersects {tgt.Category}."
                    });
                }
                else if (skipReason != null && allowBoundingBoxFallback)
                {
                    // Explicit opt-in fallback — clearly marked low-confidence
                    clashIndex++;
                    if (limit > 0 && clashes.Count >= limit)
                    {
                        warnings.Add($"Result limit ({limit}) reached.");
                        goto done;
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
                        IntersectionVolume = null,
                        DetectionMethod = "BoundingBoxFallback",
                        Confidence = "Low",
                        Status = "New",
                        Message = $"{src.Category} bounding-box overlaps {tgt.Category} (bounding-box fallback only; verify visually)."
                    });
                }
            }
        }

        done:
        if (skippedNoSolid > 0)
            warnings.Add($"Skipped {skippedNoSolid} candidate pair(s) because usable solids could not be extracted.");
        if (skippedBooleanFailure > 0)
            warnings.Add($"Skipped {skippedBooleanFailure} candidate pair(s) because solid boolean intersection failed.");
        if (skippedBelowTolerance > 0)
            warnings.Add($"Skipped {skippedBelowTolerance} candidate pair(s) because intersection volume was below tolerance ({toleranceMm}mm³).");
        if (allowBoundingBoxFallback)
            warnings.Add("Bounding-box fallback was enabled. Some hard clash results are low-confidence and must be reviewed visually.");

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
