using Autodesk.Revit.DB;
using RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>
/// Orchestrates the Phase 2 geometry preview for an entire linked IFC model.
/// Resolves the link, collects candidates (or uses a supplied element ID filter),
/// runs geometry extraction per element, and builds the serializable response.
/// Phase 2: strictly read-only. No document modifications.
/// </summary>
public class GeometryPreviewService
{
    private readonly IfcSpaceCollector        _collector   = new();
    private readonly IfcParameterReader       _reader      = new();
    private readonly LevelMatcher             _levelMatcher = new();
    private readonly IfcSpaceGeometryExtractor _extractor  = new();

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the geometry preview for one linked IFC model.
    /// </summary>
    /// <param name="hostDoc">Active host document.</param>
    /// <param name="linkInstanceId">Revit element id of the RevitLinkInstance.</param>
    /// <param name="linkedElementIds">
    /// Optional filter — if non-empty, only these linked element IDs are processed.
    /// If empty, all IFC Space candidates in the link are processed.
    /// </param>
    /// <param name="options">Geometry extraction and display options.</param>
    /// <param name="maxResults">Maximum number of items to return.</param>
    public IfcGeometryPreviewResult Run(
        Document hostDoc,
        long linkInstanceId,
        IReadOnlyCollection<long> linkedElementIds,
        IfcGeometryExtractionOptions options,
        int maxResults = 1000)
    {
        // ── 1. Resolve link ───────────────────────────────────────────────────
        var linkElement = hostDoc.GetElement(new ElementId(linkInstanceId));
        if (linkElement is not RevitLinkInstance linkInstance)
        {
            return Fail(linkInstanceId,
                $"Element {linkInstanceId} is not a RevitLinkInstance in the active document.");
        }

        var linkedDoc = linkInstance.GetLinkDocument();
        if (linkedDoc == null)
        {
            return Fail(linkInstanceId,
                $"Link {linkInstanceId} ({linkInstance.Name}) is not loaded or accessible.");
        }

        Transform linkTransform;
        try   { linkTransform = linkInstance.GetTotalTransform(); }
        catch (Exception ex)
        {
            return Fail(linkInstanceId,
                $"Could not obtain link transform for {linkInstance.Name}: {ex.Message}");
        }

        // ── 2. Collect candidate elements ─────────────────────────────────────
        List<Element> candidates;
        if (linkedElementIds.Count > 0)
        {
            // User specified particular element IDs
            candidates = new List<Element>(linkedElementIds.Count);
            foreach (var eid in linkedElementIds)
            {
                var el = linkedDoc.GetElement(new ElementId(eid));
                if (el != null) candidates.Add(el);
            }
        }
        else
        {
            // Discover all IFC Space candidates automatically
            var collectionWarnings = new List<string>();
            candidates = _collector.Collect(linkedDoc, collectionWarnings);
        }

        // ── 3. Pre-load host levels ───────────────────────────────────────────
        _levelMatcher.LoadLevels(hostDoc);

        // ── 4. Process each candidate ─────────────────────────────────────────
        var items    = new List<IfcGeometryItem>(candidates.Count);
        bool truncated = false;

        foreach (var element in candidates)
        {
            if (maxResults > 0 && items.Count >= maxResults)
            {
                truncated = true;
                break;
            }

            var item = ProcessElement(element, linkInstance, linkTransform, linkedDoc, options);
            items.Add(item);
        }

        // ── 5. Build summary ──────────────────────────────────────────────────
        var summary = BuildSummary(items, truncated);

        return new IfcGeometryPreviewResult
        {
            Success        = true,
            LinkInstanceId = linkInstanceId,
            Items          = items,
            Summary        = summary
        };
    }

    // ─────────────────────────────────────────────────────────────────────────

    private IfcGeometryItem ProcessElement(
        Element element,
        RevitLinkInstance linkInstance,
        Transform linkTransform,
        Document linkedDoc,
        IfcGeometryExtractionOptions options)
    {
        var item = new IfcGeometryItem
        {
            LinkedElementId = element.Id.Value
        };

        try
        {
            // — Read IFC metadata ——————————————————————————————————————————————
            var meta = _reader.ReadMetadata(element);
            item.Number     = meta.Number;
            item.Name       = meta.Name;
            item.StoreyName = meta.StoreyName;

            // — Level match (using bbox fallback for elevation) ────────────────
            double? bboxZ = TryGetBboxBottomZ(element, linkTransform);
            var levelMatch = _levelMatcher.Match(bboxZ, options.LevelMatchToleranceMm);

            item.TargetLevelId   = levelMatch.LevelId;
            item.TargetLevelName = levelMatch.LevelName;

            if (levelMatch.LevelId == null)
            {
                item.Status = IfcGeometryStatus.LevelMatchFailed;
                item.Warnings.Add("No host Level could be matched to this element's elevation.");
                return item;
            }

            double levelElevFeet = levelMatch.LevelElevationFeet;

            // — Geometry extraction ────────────────────────────────────────────
            var footprint = _extractor.Extract(element, linkTransform, levelElevFeet, options);

            // — Map footprint → item ───────────────────────────────────────────
            item.Status              = footprint.Status;
            item.OuterLoopCurveCount = footprint.OuterLoopCurveCount;
            item.LoopCount           = footprint.OuterLoop != null
                ? 1 + footprint.InnerLoops.Count
                : 0;
            item.Warnings.AddRange(footprint.Warnings);
            item.Warnings.AddRange(footprint.Errors);

            if (footprint.OuterLoop != null)
            {
                item.ApproxAreaM2     = Math.Round(footprint.ApproxAreaSquareFeet * 0.092903, 2);
                item.BottomElevationMm = Math.Round(footprint.BottomElevationFeet * 304.8, 1);
            }

            if (footprint.PlacementPoint != null)
            {
                item.PlacementPoint = new PointMm
                {
                    XMm = Math.Round(footprint.PlacementPoint.X * 304.8, 1),
                    YMm = Math.Round(footprint.PlacementPoint.Y * 304.8, 1),
                    ZMm = Math.Round(footprint.PlacementPoint.Z * 304.8, 1)
                };
                item.PlacementMethod = footprint.PlacementMethod;
            }

            // — Optional loop coordinates ─────────────────────────────────────
            if (options.IncludeLoopCoordinates && footprint.OuterLoop != null)
            {
                item.Loops = BuildLoopInfoList(footprint, options.MaxCoordinatePoints);
            }
        }
        catch (Exception ex)
        {
            item.Status = IfcGeometryStatus.Error;
            item.Warnings.Add($"Unexpected error: {ex.Message}");
        }

        return item;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static double? TryGetBboxBottomZ(Element element, Transform transform)
    {
        try
        {
            var bbox = element.get_BoundingBox(null);
            if (bbox == null) return null;

            // Transform all 8 corners and take the minimum Z
            var corners = new[]
            {
                new XYZ(bbox.Min.X, bbox.Min.Y, bbox.Min.Z),
                new XYZ(bbox.Max.X, bbox.Min.Y, bbox.Min.Z),
                new XYZ(bbox.Min.X, bbox.Max.Y, bbox.Min.Z),
                new XYZ(bbox.Max.X, bbox.Max.Y, bbox.Min.Z),
                new XYZ(bbox.Min.X, bbox.Min.Y, bbox.Max.Z),
                new XYZ(bbox.Max.X, bbox.Min.Y, bbox.Max.Z),
                new XYZ(bbox.Min.X, bbox.Max.Y, bbox.Max.Z),
                new XYZ(bbox.Max.X, bbox.Max.Y, bbox.Max.Z)
            };

            return corners.Select(c => transform.OfPoint(c).Z).Min();
        }
        catch
        {
            return null;
        }
    }

    private static List<IfcGeometryLoopInfo> BuildLoopInfoList(
        IfcSpaceFootprint footprint,
        int maxPoints)
    {
        var result = new List<IfcGeometryLoopInfo>();
        int loopIndex = 0;

        void AddLoop(CurveLoop loop, bool isOuter)
        {
            var tessellated = LoopAreaCalculator.TessellateLoop(loop);
            List<PointMm>? pts = null;

            if (tessellated.Count > 0)
            {
                // Limit total points returned
                int take = Math.Min(tessellated.Count, maxPoints - result.Sum(l => l.Points?.Count ?? 0));
                if (take > 0)
                {
                    pts = tessellated
                        .Take(take)
                        .Select(p => new PointMm
                        {
                            XMm = Math.Round(p.X * 304.8, 1),
                            YMm = Math.Round(p.Y * 304.8, 1),
                            ZMm = Math.Round(p.Z * 304.8, 1)
                        })
                        .ToList();
                }
            }

            double perimeter = 0;
            try { perimeter = loop.GetExactLength() * 304.8; } catch { }

            result.Add(new IfcGeometryLoopInfo
            {
                LoopIndex    = loopIndex++,
                IsOuter      = isOuter,
                CurveCount   = loop.Count(),
                Points       = pts,
                PerimeterMm  = Math.Round(perimeter, 1)
            });
        }

        if (footprint.OuterLoop != null)
            AddLoop(footprint.OuterLoop, isOuter: true);

        foreach (var inner in footprint.InnerLoops)
            AddLoop(inner, isOuter: false);

        return result;
    }

    private static IfcGeometryPreviewSummary BuildSummary(
        List<IfcGeometryItem> items,
        bool truncated)
    {
        return new IfcGeometryPreviewSummary
        {
            TotalCandidates  = items.Count,
            GeometryReady    = items.Count(i => i.Status == IfcGeometryStatus.GeometryReady),
            WithWarnings     = items.Count(i => i.Status == IfcGeometryStatus.GeometryReady && i.Warnings.Count > 0),
            NoSolidGeometry  = items.Count(i => i.Status is IfcGeometryStatus.NoGeometry
                                                           or IfcGeometryStatus.NoSolidGeometry),
            NoUsableBottomFace = items.Count(i => i.Status == IfcGeometryStatus.NoUsableBottomFace),
            InvalidLoop      = items.Count(i => i.Status is IfcGeometryStatus.NoClosedLoop
                                                           or IfcGeometryStatus.InvalidLoop
                                                           or IfcGeometryStatus.LoopCleanupFailed),
            Failed           = items.Count(i => i.Status is IfcGeometryStatus.Error
                                                           or IfcGeometryStatus.LevelMatchFailed
                                                           or IfcGeometryStatus.PlacementPointFailed),
            Truncated        = truncated
        };
    }

    private static IfcGeometryPreviewResult Fail(long linkInstanceId, string error) =>
        new()
        {
            Success        = false,
            LinkInstanceId = linkInstanceId,
            Error          = error,
            Summary        = new IfcGeometryPreviewSummary()
        };
}
