using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Geometry;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Placement;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Geometry;

/// <summary>revit_find_nearest_surface (#107): the nearest wall, ceiling, floor or roof from a point, by ray casting.</summary>
public sealed class FindNearestSurfaceTool : IRevitMcpTool
{
    public string Name => "revit_find_nearest_surface";

    public const double DefaultMaxDistanceMm = 10000;
    public const int DefaultHorizontalSamples = 16;

    /// <summary>Hits per direction that get classified, nearest first.</summary>
    private const int MaxHitsPerDirection = 10;

    /// <summary>Candidates per surface kind whose perpendicular distance is measured (one extra ray each).</summary>
    private const int RefinedCandidates = 3;

    public string Description =>
        "Finds the nearest wall, ceiling, floor, roof or any surface from a point {x,y,z} in host mm or an element " +
        "(elementId: its bounding box centre; the element itself is ignored) by casting rays in a 3D view, through " +
        "the host and loaded links (includeLinks, default true). directions: horizontal (default; horizontalSamples " +
        "compass directions, default 16), up, down, all, axes, labels like \"+x,up\", or vectors [{x,y,z}] (max 32). " +
        "surfaces[]: wall | ceiling | floor | roof | any (default any); a surface is classified by its face normal, a " +
        "Roofs category makes it a roof. Returns the best hit per surface kind — distanceMm is the perpendicular " +
        "distance to the face when the foot of the perpendicular lies on it, else the distance along the ray — and " +
        "the nearest hit per direction, each with elementId, category, linkInstanceId, pointMm, normal. maxDistanceMm " +
        "default 10000. categories[] restricts what counts as a surface. The result names the 3D view used; only " +
        "geometry visible in it is found (searchViewId to choose; a section box limits it). Status " +
        "no_3d_view_for_raycast when the model has no 3D view.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return Task.FromResult(documentFailure!);
        var doc = target.Document;
        var uidoc = ToolDocument.UiDocumentOf(uiapp, target);
        var args = request.Arguments;
        var warnings = new List<string>();

        var hasPoint = GeometryArgs.IsGiven(args, "point");
        var elementId = ToolArguments.GetLong(args, "elementId");
        var tooBroad = GeometryQueryGuard.Validate(request.RequestId, hasCategoryFilter: false, hasElementIds: elementId > 0, hasSpatialScope: hasPoint,
            "revit_find_nearest_surface needs an origin: point {x,y,z} in mm or elementId.",
            "Pass point {x, y, z} in host mm", "or elementId (its bounding box centre is used)");
        if (tooBroad != null) return Task.FromResult(tooBroad);

        var surfaces = GeometryToolSupport.Strings(args, "surfaces").Select(s => s.ToLowerInvariant()).Distinct().ToList();
        if (surfaces.Count == 0) surfaces.Add(SurfaceClassifier.Any);
        var unknown = surfaces.Where(s => !SurfaceClassifier.IsRequestable(s)).ToList();
        if (unknown.Count > 0)
            return Task.FromResult(GeometryToolSupport.Fail(request, $"Unknown surface(s) {string.Join(", ", unknown)}. Valid: wall, ceiling, floor, roof, any."));

        if (!GeometryToolSupport.TryResolveOrigin(doc, args, "point", "elementId", out var originMm, out var element, out _, out var originError))
            return Task.FromResult(GeometryToolSupport.Fail(request, originError!));

        var samples = ToolArguments.GetInt(args, "horizontalSamples", DefaultHorizontalSamples);
        if (samples < 4 || samples > ClearanceMath.MaxDirections)
        {
            warnings.Add($"horizontalSamples {samples} is outside 4-{ClearanceMath.MaxDirections}; using {DefaultHorizontalSamples}.");
            samples = DefaultHorizontalSamples;
        }
        args.TryGetValue("directions", out var rawDirections);
        var (facing, hand) = Orientation(element);
        var directions = ClearanceMath.Resolve(rawDirections, ClearanceMath.SetHorizontal, samples, facing, hand, out var directionError);
        if (directions == null)
            return Task.FromResult(GeometryQueryGuard.Refuse(request.RequestId, directionError!, "Use a direction set or at most 32 vectors."));

        var maxDistanceMm = GeometryToolSupport.ClampDistanceMm(ToolArguments.GetDouble(args, "maxDistanceMm", DefaultMaxDistanceMm), DefaultMaxDistanceMm, "maxDistanceMm", warnings);
        var maxFt = GeometryUnits.MmToFt(maxDistanceMm);
        var angle = AlignmentMath.ClampAngleTolerance(ToolArguments.GetDouble(args, "angleToleranceDegrees", 30));
        var includeLinks = ToolArguments.GetBool(args, "includeLinks", true);
        var categories = GeometryToolSupport.Strings(args, "categories");
        var ignore = GeometryToolSupport.IgnoreSet(ToolArguments.GetLongArray(args, "ignoreElementIds"), element, includeDependents: false);

        var view = RayCaster.ResolveView(doc, uidoc, request, warnings, out var viewFailure);
        if (view == null) return Task.FromResult(viewFailure!);
        var caster = new RayCaster(doc, view, includeLinks);
        var accept = GeometryToolSupport.Accept(ignore, categories);
        var origin = GeometryConvert.FromMm(originMm);

        var perDirection = new List<object>();
        var candidates = new List<SurfaceCandidate>();
        var firstHits = new HashSet<SurfaceCandidate>();
        foreach (var direction in directions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ray = GeometryConvert.ToXyz(direction.Direction).Normalize();
            var hits = caster.HitsAlong(origin, ray, maxFt, accept).Take(MaxHitsPerDirection).ToList();
            SurfaceCandidate? first = null;
            var kindsSeen = new HashSet<string>();
            foreach (var hit in hits)
            {
                var candidate = Classify(caster, direction, ray, hit, angle);
                if (first == null)
                {
                    first = candidate;
                    firstHits.Add(candidate);
                }
                // The nearest hit of each kind along this direction can win that kind; the rest lie behind it.
                if (kindsSeen.Add(candidate.Kind)) candidates.Add(candidate);
                if (surfaces.All(s => s == SurfaceClassifier.Any || kindsSeen.Contains(s))) break;
            }
            perDirection.Add(new
            {
                direction = direction.Label,
                vector = DirectionDto.From(direction.Direction),
                hit = first == null ? null : RayCaster.ToDto(first.Hit, first.Normal, first.Kind, first.NormalSource)
            });
        }

        var best = new Dictionary<string, object?>(StringComparer.Ordinal);
        var winners = new Dictionary<string, SurfaceCandidate>(StringComparer.Ordinal);
        foreach (var surface in surfaces)
        {
            var pool = surface == SurfaceClassifier.Any
                ? candidates.Where(firstHits.Contains)
                : candidates.Where(c => c.Kind == surface);
            var refined = pool.OrderBy(c => c.Hit.DistanceFt).Take(RefinedCandidates).ToList();
            foreach (var candidate in refined) Refine(caster, origin, candidate, maxFt, accept);
            var winner = refined.OrderBy(c => c.DistanceFt).FirstOrDefault();
            if (winner != null) winners[surface] = winner;
            best[surface] = winner == null ? null : new
            {
                direction = winner.Direction.Label,
                vector = DirectionDto.From(winner.Direction.Direction),
                perpendicular = winner.Perpendicular,
                rayDistanceMm = GeometryToolSupport.Mm(winner.Hit.DistanceFt),
                hit = Dto(winner)
            };
        }

        var missing = surfaces.Where(s => !winners.ContainsKey(s)).ToList();
        return Task.FromResult(GeometryToolSupport.Ok(request, sw,
            winners.Count == 0
                ? $"No {string.Join("/", surfaces)} surface within {maxDistanceMm:0} mm in {directions.Count} direction(s)."
                : "Nearest " + string.Join(", ", winners.Select(w =>
                    $"{w.Key}: {GeometryToolSupport.Mm(w.Value.DistanceFt):0.#} mm ({w.Value.Hit.Target.Category} {w.Value.Hit.Target.ElementId}, {w.Value.Direction.Label})")) +
                  (missing.Count > 0 ? $"; no {string.Join("/", missing)} within {maxDistanceMm:0} mm." : "."),
            new
            {
                units = "mm, host internal coordinates",
                originMm = PointMmDto.FromMm(originMm),
                originSource = element != null ? $"element {element.Id.Value} bounding box centre" : "point",
                view = caster.DescribeView(),
                includeLinks,
                maxDistanceMm = GeometryUnits.RoundMm(maxDistanceMm),
                surfaces,
                best,
                hits = perDirection
            },
            warnings));
    }

    private static (Vec3? Facing, Vec3? Hand) Orientation(Element? element)
    {
        if (element is not FamilyInstance instance) return (null, null);
        try { return (GeometryConvert.ToVec(instance.FacingOrientation), GeometryConvert.ToVec(instance.HandOrientation)); }
        catch { return (null, null); }
    }

    private static SurfaceCandidate Classify(RayCaster caster, NamedDirection direction, XYZ ray, RayHit hit, double angle)
    {
        var geometric = caster.FaceNormal(hit.Reference, hit.Point, out _);
        var towardCaster = GeometryConvert.ToVec(ray.Negate());
        var normal = geometric.HasValue ? RayCaster.TowardCaster(geometric.Value, ray) : towardCaster;
        return new SurfaceCandidate
        {
            Direction = direction,
            Hit = hit,
            Normal = normal,
            NormalSource = geometric.HasValue ? "geometry" : "ray",
            Kind = SurfaceClassifier.Classify(normal, hit.Target.BuiltInCategory, hit.Target.Category, angle),
            DistanceFt = hit.DistanceFt,
            Point = hit.Point
        };
    }

    /// <summary>
    /// The perpendicular distance from the origin to the hit face: measured along the face normal and confirmed with
    /// one ray straight at the face. When that ray meets another element (the face ends before the foot of the
    /// perpendicular) the distance along the search ray is kept.
    /// </summary>
    private static void Refine(RayCaster caster, XYZ origin, SurfaceCandidate candidate, double maxFt, Func<RayTarget, bool> accept)
    {
        candidate.DistanceFt = candidate.Hit.DistanceFt;
        candidate.Point = candidate.Hit.Point;
        var ray = GeometryConvert.ToXyz(candidate.Direction.Direction).Normalize();

        if (candidate.NormalSource == "ray")
        {
            var sameElement = SameElement(candidate.Hit.Reference);
            var measured = RayCaster.MeasurePlane(origin, ray, candidate.Hit.Point, probe =>
            {
                var hit = caster.NearestHit(probe, ray, maxFt * 2, reference => !sameElement(reference));
                return hit == null ? null : probe + ray * hit.Proximity;
            });
            if (measured.HasValue)
            {
                candidate.Normal = measured.Value;
                candidate.NormalSource = "measured";
            }
        }
        if (candidate.NormalSource == "ray") return;   // the face's plane is unknown: the ray distance is all there is

        var straight = GeometryConvert.ToXyz(candidate.Normal.Negated()).Normalize();
        var perpendicular = FacePlacementMath.DistanceToPlane(GeometryConvert.ToVec(origin), GeometryConvert.ToVec(candidate.Hit.Point), candidate.Normal);
        var hits = caster.HitsAlong(origin, straight, Math.Min(maxFt, perpendicular + RayCaster.DefaultProbeOffsetFt), accept);
        var onFace = hits.FirstOrDefault();
        if (onFace != null && onFace.Target.ElementId == candidate.Hit.Target.ElementId &&
            onFace.Target.LinkInstanceId == candidate.Hit.Target.LinkInstanceId && onFace.DistanceFt <= candidate.Hit.DistanceFt + 1e-9)
        {
            candidate.DistanceFt = onFace.DistanceFt;
            candidate.Point = onFace.Point;
            candidate.Perpendicular = true;
        }
    }

    private static Func<Reference, bool> SameElement(Reference reference)
    {
        var id = reference.ElementId.Value;
        var linked = reference.LinkedElementId.Value;
        return other => other.ElementId.Value == id && other.LinkedElementId.Value == linked;
    }

    private static HitDto Dto(SurfaceCandidate candidate)
    {
        var dto = RayCaster.ToDto(candidate.Hit, candidate.Normal, candidate.Kind, candidate.NormalSource,
            GeometryUnits.FtToMm(candidate.DistanceFt));
        dto.PointMm = GeometryConvert.Point(candidate.Point);
        return dto;
    }
}

/// <summary>A classified hit of revit_find_nearest_surface and, after refinement, its perpendicular distance.</summary>
internal sealed class SurfaceCandidate
{
    public NamedDirection Direction = null!;
    public RayHit Hit = null!;
    public Vec3 Normal;
    public string NormalSource = "ray";
    public string Kind = SurfaceClassifier.Other;

    // After refinement.
    public double DistanceFt;
    public XYZ Point = XYZ.Zero;
    public bool Perpendicular;
}
