using Autodesk.Revit.DB;
using RevitMCP.Addin.Geometry;

namespace RevitMCP.Addin.Placement;

/// <summary>
/// Resolves, per element, the surface it should be moved against and how far it has to travel.
/// Surfaces are found by ray casting through the shared <see cref="RayCaster"/> (a ReferenceIntersector
/// that sees both the host model and loaded links), so IFC links converted to Revit links work the same
/// as native geometry. What the ray hits is classified by face orientation rather than category.
/// </summary>
internal sealed class AlignmentService
{
    /// <summary>Offset used by the probe rays that estimate the hit face's plane, in feet.</summary>
    private const double ProbeOffsetFt = RayCaster.DefaultProbeOffsetFt;

    /// <summary>
    /// How many candidates get their surface measured before the search falls back to the cheaper
    /// direction inference. Each measurement costs two extra rays.
    /// </summary>
    private const int MaxMeasuredCandidates = 6;

    /// <summary>Hits closer than this sit on the ray origin itself and are ignored, in feet (~3 mm).</summary>
    private const double MinHitDistanceFt = 0.01;

    private readonly AlignmentOptions _options;
    private readonly RayCaster _caster;

    public AlignmentService(Document doc, View3D view, AlignmentOptions options)
    {
        _options = options;

        // No ElementFilter is passed on purpose (see RayCaster): ReferenceIntersector applies its filter
        // to the RevitLinkInstance rather than the elements inside the link, so a category filter would
        // discard every linked hit — exactly the geometry this tool exists to find. Category filtering
        // happens after the hit, in IsExcluded, where the linked element is resolvable.
        _caster = new RayCaster(doc, view, options.IncludeLinks);
    }

    /// <summary>
    /// Works out where <paramref name="element"/> should end up. Every direction in the search set
    /// is probed; the nearest hit whose surface matches the request wins, and the rest are kept as
    /// alternates so the preview can show what else was in range.
    /// </summary>
    public AlignmentPlan Resolve(Element element)
    {
        var plan = new AlignmentPlan
        {
            ElementId = element.Id.Value,
            ElementName = SafeName(element),
            Category = element.Category?.Name ?? string.Empty
        };

        var box = element.get_BoundingBox(null);
        if (box == null)
        {
            plan.BlockedReason = "Element has no geometry to measure, so it cannot be aligned.";
            return plan;
        }

        var centre = (box.Min + box.Max) * 0.5;
        var halfExtents = new Vec3(
            Math.Abs(box.Max.X - box.Min.X) * 0.5,
            Math.Abs(box.Max.Y - box.Min.Y) * 0.5,
            Math.Abs(box.Max.Z - box.Min.Z) * 0.5);

        plan.Origin = centre;

        var facing = TryGetFacing(element);
        var directions = AlignmentMath.SearchDirections(
            _options.Surface, _options.HorizontalSamples, facing);

        if (directions.Count == 0)
        {
            plan.BlockedReason = $"No search directions for surface '{_options.Surface}'.";
            return plan;
        }

        // One ray per direction first. Measuring a hit face costs two more rays, so that is done
        // only for the nearest few candidates, in order, until one matches what was asked for.
        var byTarget = new Dictionary<string, AlignmentCandidate>(StringComparer.Ordinal);
        foreach (var direction in directions)
        {
            var candidate = Probe(centre, direction, halfExtents);
            if (candidate == null)
                continue;

            // Several directions reach the same surface — the preferred facing repeats entries of
            // the ring, and a ring direction closer to perpendicular gives a shorter, better move.
            var key = $"{candidate.TargetElementId}|{candidate.LinkInstanceId}";
            if (!byTarget.TryGetValue(key, out var existing) ||
                Math.Abs(candidate.TravelDistanceFt) < Math.Abs(existing.TravelDistanceFt))
            {
                byTarget[key] = candidate;
            }
        }

        plan.Candidates = byTarget.Values
            .OrderBy(c => Math.Abs(c.TravelDistanceFt))
            .ToList();

        AlignmentCandidate? chosen = null;
        var measured = 0;
        foreach (var candidate in plan.Candidates)
        {
            if (measured < MaxMeasuredCandidates)
            {
                MeasureSurface(centre, candidate);
                measured++;
            }

            if (AlignmentMath.SurfaceSatisfies(_options.Surface, candidate.SurfaceKind))
            {
                chosen = candidate;
                break;
            }
        }

        if (chosen == null)
        {
            plan.BlockedReason = plan.Candidates.Count == 0
                ? $"Nothing found within {_options.SearchRadiusFt * 304.8:F0} mm. Increase searchRadiusMm, " +
                  "widen the scope, or check that the link is loaded and visible in the 3D view used for the search."
                : $"Found {plan.Candidates.Count} surface(s) in range but none of them is a " +
                  $"'{_options.Surface}'. Nearest was a '{plan.Candidates[0].SurfaceKind}' " +
                  $"({plan.Candidates[0].TargetCategory}). Raise angleToleranceDegrees or pick another surface.";
            return plan;
        }

        plan.Chosen = chosen;
        return plan;
    }

    /// <summary>
    /// Casts one ray and records what it hit and how far the element would have to travel.
    /// The surface orientation starts as an inference from the ray direction; <see cref="MeasureSurface"/>
    /// replaces it with a measured value for the candidates that matter.
    /// </summary>
    private AlignmentCandidate? Probe(XYZ origin, Vec3 direction, Vec3 halfExtents)
    {
        var rayDirection = ToXyz(direction).Normalize();
        var hit = NearestHit(origin, rayDirection);
        if (hit == null)
            return null;

        var reach = AlignmentMath.SupportDistance(halfExtents, direction);
        var candidate = new AlignmentCandidate
        {
            Direction = direction,
            HitDistanceFt = hit.Proximity,
            ReachFt = reach,
            CurrentGapFt = AlignmentMath.CurrentGap(hit.Proximity, reach),
            TravelDistanceFt = AlignmentMath.TravelDistance(hit.Proximity, reach, _options.GapFt),
            HitPoint = origin + rayDirection * hit.Proximity,
            // A hit found by casting up is, from the element's point of view, a downward-facing
            // surface. Good enough to sort by; replaced with a measured normal before it is chosen.
            SurfaceNormal = direction.Negated(),
            SurfaceKind = AlignmentMath.ClassifySurface(direction.Negated(), _options.AngleToleranceDegrees),
            NormalIsMeasured = false
        };

        var target = ResolveTarget(hit.GetReference());
        candidate.TargetElementId = target.ElementId;
        candidate.TargetCategory = target.Category;
        candidate.TargetName = target.Name;
        candidate.Model = target.Model;
        candidate.LinkInstanceId = target.LinkInstanceId;
        candidate.LinkName = target.LinkName;

        return candidate;
    }

    /// <summary>
    /// Replaces a candidate's inferred orientation with one measured from the hit face, by casting
    /// two more rays alongside the first. Leaves the inference in place when the probes disagree.
    /// </summary>
    private void MeasureSurface(XYZ origin, AlignmentCandidate candidate)
    {
        var rayDirection = ToXyz(candidate.Direction).Normalize();
        var normal = EstimatePlaneNormal(origin, rayDirection, candidate);
        if (!normal.HasValue)
            return;

        candidate.SurfaceNormal = normal.Value;
        candidate.SurfaceKind = AlignmentMath.ClassifySurface(normal.Value, _options.AngleToleranceDegrees);
        candidate.NormalIsMeasured = true;
    }

    /// <summary>
    /// Measures the hit face by casting two more rays from points offset perpendicular to the first.
    /// Three points on the same planar face define its plane exactly. Returns null when a probe
    /// misses, lands on a different element, or the three points turn out collinear — all of which
    /// mean the face is curved, fragmented, or narrower than the probe offset.
    /// </summary>
    private Vec3? EstimatePlaneNormal(XYZ origin, XYZ rayDirection, AlignmentCandidate candidate) =>
        RayCaster.MeasurePlane(origin, rayDirection, candidate.HitPoint,
            probeOrigin => ProbePointOn(probeOrigin, rayDirection, candidate), ProbeOffsetFt);

    /// <summary>
    /// Casts a probe ray and returns where it landed, but only if it hit the same element as the
    /// primary ray. A probe that strays onto a beam in front of the wall would otherwise define a
    /// plane that has nothing to do with the surface being measured.
    /// </summary>
    private XYZ? ProbePointOn(XYZ origin, XYZ rayDirection, AlignmentCandidate candidate)
    {
        var hit = NearestHit(origin, rayDirection);
        if (hit == null)
            return null;

        var target = ResolveTarget(hit.GetReference());
        if (target.ElementId != candidate.TargetElementId ||
            target.LinkInstanceId != candidate.LinkInstanceId)
            return null;

        return origin + rayDirection * hit.Proximity;
    }

    /// <summary>
    /// The nearest hit that is not excluded. Faces sitting on the ray origin (within MinHitDistanceFt)
    /// and anything past the search radius are ignored.
    /// </summary>
    private ReferenceWithContext? NearestHit(XYZ origin, XYZ direction) =>
        _caster.NearestHit(origin, direction, _options.SearchRadiusFt,
            reference => IsExcluded(ResolveTarget(reference)), MinHitDistanceFt);

    /// <summary>Skips the elements being moved, links the caller ruled out, and unwanted categories.</summary>
    private bool IsExcluded(RayTarget target)
    {
        if (target.LinkInstanceId.HasValue)
        {
            if (_options.LinkInstanceIds.Count > 0 &&
                !_options.LinkInstanceIds.Contains(target.LinkInstanceId.Value))
                return true;
        }
        else
        {
            if (!_options.IncludeHost)
                return true;
            if (_options.ExcludedElementIds.Contains(target.ElementId))
                return true;
        }

        return _options.TargetCategories.Count > 0 &&
               !_options.TargetCategories.Contains(target.Category);
    }

    /// <summary>Resolves what a hit reference actually points at, in the host model or inside a link.</summary>
    private RayTarget ResolveTarget(Reference reference) => _caster.ResolveTarget(reference);

    /// <summary>The facing direction of a family instance, used to look behind wall-mounted devices first.</summary>
    private static Vec3? TryGetFacing(Element element)
    {
        try
        {
            if (element is FamilyInstance instance)
            {
                var facing = instance.FacingOrientation;
                if (facing != null && !facing.IsZeroLength())
                    return ToVec(facing);
            }
        }
        catch { }
        return null;
    }

    public static XYZ ToXyz(Vec3 v) => GeometryConvert.ToXyz(v);

    public static Vec3 ToVec(XYZ p) => GeometryConvert.ToVec(p);

    public static string SafeName(Element element) => GeometryConvert.SafeName(element);
}

internal sealed class AlignmentOptions
{
    public string Surface { get; set; } = AlignmentMath.SurfaceNearest;
    public double SearchRadiusFt { get; set; }
    public double GapFt { get; set; }
    public int HorizontalSamples { get; set; } = 8;
    public double AngleToleranceDegrees { get; set; } = 30.0;
    public bool IncludeLinks { get; set; } = true;
    public bool IncludeHost { get; set; } = true;
    public bool RotateToSurface { get; set; }
    public HashSet<long> LinkInstanceIds { get; set; } = new();
    public HashSet<long> ExcludedElementIds { get; set; } = new();
    public HashSet<string> TargetCategories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class AlignmentCandidate
{
    public Vec3 Direction { get; set; }
    public Vec3 SurfaceNormal { get; set; }
    public bool NormalIsMeasured { get; set; }
    public string SurfaceKind { get; set; } = AlignmentMath.SurfaceOther;
    public double HitDistanceFt { get; set; }
    public double ReachFt { get; set; }
    public double CurrentGapFt { get; set; }
    public double TravelDistanceFt { get; set; }
    public XYZ HitPoint { get; set; } = XYZ.Zero;
    public long TargetElementId { get; set; }
    public string TargetCategory { get; set; } = string.Empty;
    public string TargetName { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public long? LinkInstanceId { get; set; }
    public string LinkName { get; set; } = string.Empty;

    public object ToPayload() => new
    {
        surfaceKind = SurfaceKind,
        surfaceNormalMeasured = NormalIsMeasured,
        targetElementId = TargetElementId,
        targetCategory = TargetCategory,
        targetName = TargetName,
        model = Model,
        linkInstanceId = LinkInstanceId,
        distanceMm = Math.Round(HitDistanceFt * 304.8, 1),
        currentGapMm = Math.Round(CurrentGapFt * 304.8, 1),
        moveDistanceMm = Math.Round(TravelDistanceFt * 304.8, 1),
        direction = new { x = Math.Round(Direction.X, 4), y = Math.Round(Direction.Y, 4), z = Math.Round(Direction.Z, 4) }
    };
}

internal sealed class AlignmentPlan
{
    public long ElementId { get; set; }
    public string ElementName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public XYZ Origin { get; set; } = XYZ.Zero;
    public List<AlignmentCandidate> Candidates { get; set; } = new();
    public AlignmentCandidate? Chosen { get; set; }
    public string? BlockedReason { get; set; }

    public bool CanAlign => Chosen != null && BlockedReason == null;
}
