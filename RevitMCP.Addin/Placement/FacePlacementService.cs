using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Placement;

internal sealed class FacePlacementOptions
{
    public double MaxDistanceFt { get; init; }
    public bool IncludeHost { get; init; } = true;
    public bool IncludeLinks { get; init; } = true;

    /// <summary>When non-empty, only faces in these link instances are considered among the links.</summary>
    public HashSet<long> LinkInstanceIds { get; init; } = new();

    /// <summary>When non-empty, only faces of elements in these categories are considered.</summary>
    public HashSet<string> HostCategories { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public int HorizontalSamples { get; init; } = 16;
    public double AngleToleranceDegrees { get; init; } = 10.0;
    public double OffsetFromHostFt { get; init; }
    public bool HasOffsetFromHost { get; init; }
}

/// <summary>A face a family could be hosted on, found by ray casting.</summary>
internal sealed class FaceTarget
{
    public Reference Reference { get; init; } = null!;

    /// <summary>The same face as a reference built from the element's geometry, for links. A second way in when Revit refuses the first.</summary>
    public Reference? GeometryReference { get; set; }

    public XYZ Location { get; set; } = XYZ.Zero;

    /// <summary>The direction of the ray that found this face.</summary>
    public XYZ RayDirection { get; set; } = XYZ.BasisZ;
    public Vec3 Normal { get; set; }
    public string NormalSource { get; set; } = "ray";
    public string SurfaceKind { get; set; } = AlignmentMath.SurfaceOther;

    /// <summary>Perpendicular distance from the requested point to the face, in feet.</summary>
    public double DistanceFt { get; set; }

    /// <summary>True when the family lands at the foot of the perpendicular from the requested point.</summary>
    public bool LocationIsPerpendicular { get; set; }

    public long HostElementId { get; set; }
    public string HostCategory { get; set; } = string.Empty;
    public string HostName { get; set; } = string.Empty;
    public string Model { get; set; } = "Host";
    public long? LinkInstanceId { get; set; }
    public string? LinkName { get; set; }
    public string FaceKey { get; set; } = string.Empty;
}

/// <summary>One placement and what became of it.</summary>
internal sealed class FacePlacementPlan
{
    public FacePlacementRequest Request { get; init; } = null!;
    public FaceTarget? Target { get; set; }
    public Vec3? ReferenceDirection { get; set; }

    public string Status { get; set; } = FacePlacementStatus.Ready;
    public string? Reason { get; set; }
    public bool IsFailure { get; set; }
    public bool CanPlace { get; set; }

    /// <summary>What else was in reach, nearest first — shown when nothing matched.</summary>
    public List<FaceTarget> Alternates { get; set; } = new();

    public long? ElementId { get; set; }
    public long? PlacedHostId { get; set; }
    public XYZ? PlacedPoint { get; set; }
    public string? PlacedVia { get; set; }
    public List<string> Notes { get; } = new();
}

/// <summary>
/// Finds the face a family should sit on and hosts it there. Faces are found by ray casting through
/// <see cref="ReferenceIntersector"/> in a 3D view, which sees the host model and loaded links alike,
/// so a wall in a linked architecture or IFC model is found the same way as a native one. What a ray
/// hits is classified by the orientation of the face — not by category — because linked IFC geometry
/// often has no useful category.
/// </summary>
internal sealed class FacePlacementService
{
    /// <summary>Offset of the two extra rays that measure the hit face's plane, in feet (~76 mm).</summary>
    private const double ProbeOffsetFt = 0.25;

    /// <summary>Candidates whose plane gets measured, nearest first. Each costs two more rays.</summary>
    private const int MaxMeasuredCandidates = 8;

    /// <summary>How far behind the requested point the rays start, in feet (~15 mm), so a point lying exactly on a face still meets it.</summary>
    private const double BackOffFt = 0.05;

    private readonly Document _doc;
    private readonly FacePlacementOptions _options;
    private readonly ReferenceIntersector _intersector;

    public FacePlacementService(Document doc, View3D view, FacePlacementOptions options)
    {
        _doc = doc;
        _options = options;

        // No ElementFilter: ReferenceIntersector applies its filter to the RevitLinkInstance rather
        // than to the elements inside the link, so a category filter here would drop every linked
        // hit. Categories are checked after the hit, where the linked element can be resolved.
        _intersector = new ReferenceIntersector(view) { TargetType = FindReferenceTarget.Face };
        _intersector.FindReferencesInRevitLinks = options.IncludeLinks;
    }

    /// <summary>Works out which face each placement would use, touching nothing.</summary>
    public List<FacePlacementPlan> BuildPlans(IReadOnlyList<FacePlacementRequest> requests, CancellationToken cancellationToken)
    {
        var plans = new List<FacePlacementPlan>(requests.Count);
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            plans.Add(BuildPlan(request));
        }
        return plans;
    }

    private FacePlacementPlan BuildPlan(FacePlacementRequest request)
    {
        var plan = new FacePlacementPlan { Request = request };
        var origin = PlacementHelpers.PointFromMm(request.PointMm.X, request.PointMm.Y, request.PointMm.Z);

        var directions = request.Direction.HasValue
            ? new List<Vec3> { request.Direction.Value }
            : AlignmentMath.SearchDirections(request.MountOn, _options.HorizontalSamples, null);

        // One ray per direction; several directions reach the same face, so keep one target per face.
        var byFace = new Dictionary<string, FaceTarget>(StringComparer.Ordinal);
        foreach (var direction in directions)
        {
            var target = Probe(origin, AlignmentService.ToXyz(direction).Normalize());
            if (target == null) continue;
            if (!byFace.TryGetValue(target.FaceKey, out var existing) || target.DistanceFt < existing.DistanceFt)
                byFace[target.FaceKey] = target;
        }

        var candidates = byFace.Values.OrderBy(t => t.DistanceFt).Take(MaxMeasuredCandidates).ToList();
        foreach (var candidate in candidates)
            Refine(origin, candidate);

        // The perpendicular distance is what "nearest face" means; the ray that found a wall may have
        // met it at a shallow angle, a long way along it.
        candidates = candidates.Where(c => c.DistanceFt <= _options.MaxDistanceFt).OrderBy(c => c.DistanceFt).ToList();

        var wanted = request.Direction.HasValue ? AlignmentMath.SurfaceNearest : request.MountOn;
        var chosen = request.Direction.HasValue
            ? candidates.FirstOrDefault()
            : candidates.FirstOrDefault(c => AlignmentMath.SurfaceSatisfies(wanted, c.SurfaceKind));

        if (chosen == null)
        {
            plan.Alternates = candidates;
            plan.IsFailure = true;
            if (candidates.Count == 0)
            {
                plan.Status = FacePlacementStatus.NoFace;
                plan.Reason =
                    $"No face within {_options.MaxDistanceFt * 304.8:F0} mm of the point" +
                    (request.Direction.HasValue ? " in the given direction" : $" for mountOn='{request.MountOn}'") +
                    ". Move the point closer, raise maxDistanceMm, or check that the host or link is loaded and " +
                    "visible in the 3D view used for the search (searchViewId).";
            }
            else
            {
                var nearest = candidates[0];
                plan.Status = FacePlacementStatus.WrongSurface;
                plan.Reason =
                    $"Found {candidates.Count} face(s) in reach, but none is a '{request.MountOn}'. The nearest is a " +
                    $"'{nearest.SurfaceKind}' ({Describe(nearest)}) {nearest.DistanceFt * 304.8:F0} mm away. " +
                    "Use another mountOn, an explicit direction, or raise angleToleranceDegrees for sloped surfaces.";
            }
            return plan;
        }

        plan.Target = chosen;
        plan.ReferenceDirection = FacePlacementMath.ReferenceDirection(chosen.Normal, request.RotationDegrees);
        plan.CanPlace = true;
        plan.Alternates = candidates.Where(c => !ReferenceEquals(c, chosen)).Take(3).ToList();

        if (!chosen.LocationIsPerpendicular)
            plan.Notes.Add("The point's perpendicular foot is not on this face (the face ends before it), so the family " +
                           "lands where the search ray met the face instead.");
        if (chosen.NormalSource == "ray")
            plan.Notes.Add("The face's orientation could not be measured (a curved or very narrow face); it was taken " +
                           "from the search direction. Check the placed family's orientation.");
        return plan;
    }

    /// <summary>Casts one ray and describes the nearest face it meets.</summary>
    private FaceTarget? Probe(XYZ origin, XYZ direction)
    {
        var start = origin - direction * BackOffFt;
        var hit = NearestHit(start, direction, _options.MaxDistanceFt * 4 + BackOffFt);
        if (hit == null) return null;

        var reference = hit.GetReference();
        var target = Resolve(reference);
        target.Location = start + direction * hit.Proximity;
        target.Normal = AlignmentService.ToVec(direction.Negate());
        target.DistanceFt = Math.Max(0, hit.Proximity - BackOffFt);
        target.RayDirection = direction;
        return target;
    }

    /// <summary>
    /// Measures the face's plane, and moves the placement point to the foot of the perpendicular from
    /// the requested point when that foot lies on the same face.
    /// </summary>
    private void Refine(XYZ origin, FaceTarget target)
    {
        var rayDirection = target.RayDirection;
        var towardCaster = AlignmentService.ToVec(rayDirection.Negate());

        var measured = MeasurePlane(origin, rayDirection, target);
        var geometric = GeometricNormal(target);
        var normal = FacePlacementMath.ChooseNormal(measured, geometric, towardCaster);

        target.Normal = normal;
        target.NormalSource = measured.HasValue ? "measured" : geometric.HasValue ? "geometry" : "ray";
        target.SurfaceKind = AlignmentMath.ClassifySurface(normal, _options.AngleToleranceDegrees);

        var hitPoint = AlignmentService.ToVec(target.Location);
        target.DistanceFt = FacePlacementMath.DistanceToPlane(AlignmentService.ToVec(origin), hitPoint, normal);

        // Straight at the face: if that ray lands on the same face, its hit is the placement point.
        var straight = AlignmentService.ToXyz(normal.Negated()).Normalize();
        var start = origin - straight * BackOffFt;
        var perpendicular = NearestHit(start, straight, target.DistanceFt + BackOffFt + ProbeOffsetFt);
        if (perpendicular != null && FaceKeyOf(perpendicular.GetReference()) == target.FaceKey)
        {
            target.Location = start + straight * perpendicular.Proximity;
            target.LocationIsPerpendicular = true;
        }
    }

    /// <summary>Three points on a planar face define its plane: two more rays alongside the first.</summary>
    private Vec3? MeasurePlane(XYZ origin, XYZ rayDirection, FaceTarget target)
    {
        var seed = Math.Abs(rayDirection.Z) > 0.9 ? XYZ.BasisX : XYZ.BasisZ;
        var u = rayDirection.CrossProduct(seed).Normalize();
        var v = rayDirection.CrossProduct(u).Normalize();

        var first = PointOnSameFace(origin + u * ProbeOffsetFt, rayDirection, target);
        var second = PointOnSameFace(origin + v * ProbeOffsetFt, rayDirection, target);
        if (first == null || second == null) return null;

        return AlignmentMath.PlaneNormal(
            AlignmentService.ToVec(target.Location), AlignmentService.ToVec(first), AlignmentService.ToVec(second),
            AlignmentService.ToVec(rayDirection.Negate()));
    }

    private XYZ? PointOnSameFace(XYZ origin, XYZ direction, FaceTarget target)
    {
        var start = origin - direction * BackOffFt;
        var hit = NearestHit(start, direction, _options.MaxDistanceFt * 4 + BackOffFt + ProbeOffsetFt);
        if (hit == null || FaceKeyOf(hit.GetReference()) != target.FaceKey) return null;
        return start + direction * hit.Proximity;
    }

    /// <summary>The face's own normal at the hit point, in model coordinates, or null when the face cannot be read.</summary>
    private Vec3? GeometricNormal(FaceTarget target)
    {
        try
        {
            var reference = target.Reference;
            Face? face;
            var toModel = Transform.Identity;

            if (target.LinkInstanceId.HasValue)
            {
                if (_doc.GetElement(reference.ElementId) is not RevitLinkInstance link) return null;
                var linked = link.GetLinkDocument()?.GetElement(reference.LinkedElementId);
                if (linked == null) return null;
                face = linked.GetGeometryObjectFromReference(reference.CreateReferenceInLink()) as Face;
                toModel = link.GetTotalTransform();
                if (face?.Reference != null)
                    target.GeometryReference = face.Reference.CreateLinkReference(link);
            }
            else
            {
                face = _doc.GetElement(reference.ElementId)?.GetGeometryObjectFromReference(reference) as Face;
            }

            if (face == null) return null;

            var local = toModel.Inverse.OfPoint(target.Location);
            var projection = face.Project(local);
            if (projection == null) return null;

            var normal = toModel.OfVector(face.ComputeNormal(projection.UVPoint));
            return normal.IsZeroLength() ? null : AlignmentService.ToVec(normal.Normalize());
        }
        catch
        {
            return null;
        }
    }

    private ReferenceWithContext? NearestHit(XYZ origin, XYZ direction, double maxDistanceFt)
    {
        IList<ReferenceWithContext> hits;
        try { hits = _intersector.Find(origin, direction); }
        catch { return null; }
        if (hits == null) return null;

        ReferenceWithContext? best = null;
        foreach (var hit in hits)
        {
            if (hit.Proximity < 0 || hit.Proximity > maxDistanceFt) continue;
            if (best != null && hit.Proximity >= best.Proximity) continue;
            if (IsExcluded(hit.GetReference())) continue;
            best = hit;
        }
        return best;
    }

    private bool IsExcluded(Reference reference)
    {
        var isLinked = reference.LinkedElementId != ElementId.InvalidElementId;
        if (isLinked)
        {
            if (_options.LinkInstanceIds.Count > 0 && !_options.LinkInstanceIds.Contains(reference.ElementId.Value))
                return true;
        }
        else if (!_options.IncludeHost)
        {
            return true;
        }

        if (_options.HostCategories.Count == 0) return false;
        return !_options.HostCategories.Contains(Resolve(reference).HostCategory);
    }

    /// <summary>Resolves what a hit reference points at, in the host model or inside a link.</summary>
    private FaceTarget Resolve(Reference reference)
    {
        var target = new FaceTarget { Reference = reference, FaceKey = FaceKeyOf(reference) };
        try
        {
            if (reference.LinkedElementId != ElementId.InvalidElementId)
            {
                var link = _doc.GetElement(reference.ElementId) as RevitLinkInstance;
                var linked = link?.GetLinkDocument()?.GetElement(reference.LinkedElementId);
                target.LinkInstanceId = reference.ElementId.Value;
                target.LinkName = link?.Name ?? string.Empty;
                target.Model = string.IsNullOrEmpty(target.LinkName) ? "Link" : target.LinkName;
                target.HostElementId = reference.LinkedElementId.Value;
                target.HostCategory = linked?.Category?.Name ?? string.Empty;
                target.HostName = linked != null ? AlignmentService.SafeName(linked) : string.Empty;
                return target;
            }

            var element = _doc.GetElement(reference.ElementId);
            target.HostElementId = reference.ElementId.Value;
            target.HostCategory = element?.Category?.Name ?? string.Empty;
            target.HostName = element != null ? AlignmentService.SafeName(element) : string.Empty;
        }
        catch
        {
            target.Model = "Unknown";
        }
        return target;
    }

    /// <summary>Identifies a face: two references to the same face give the same key.</summary>
    private string FaceKeyOf(Reference reference)
    {
        try
        {
            // The stable representation ends with the hit's UV for point-on-face references; the part
            // before it names the element and the face.
            var stable = reference.ConvertToStableRepresentation(_doc);
            var uv = stable.LastIndexOf("/", StringComparison.Ordinal);
            return uv > 0 && stable.IndexOf(":", uv, StringComparison.Ordinal) < 0 ? stable.Substring(0, uv) : stable;
        }
        catch
        {
            return $"{reference.ElementId.Value}|{reference.LinkedElementId.Value}";
        }
    }

    // ── Placing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Hosts one instance on the plan's face. Must run inside a transaction. A linked face is tried
    /// through the reference the ray returned and then through one rebuilt from the linked element's
    /// geometry — Revit accepts one or the other depending on how the link was made.
    /// </summary>
    public void Place(FamilySymbol symbol, FacePlacementPlan plan)
    {
        var target = plan.Target!;
        var direction = AlignmentService.ToXyz(plan.ReferenceDirection!.Value).Normalize();

        FamilyInstance instance;
        try
        {
            instance = _doc.Create.NewFamilyInstance(target.Reference, target.Location, direction, symbol);
            plan.PlacedVia = "ray reference";
        }
        catch (Exception first) when (target.GeometryReference != null)
        {
            try
            {
                instance = _doc.Create.NewFamilyInstance(target.GeometryReference, target.Location, direction, symbol);
                plan.PlacedVia = "geometry reference";
            }
            catch (Exception second)
            {
                throw new InvalidOperationException(
                    $"Revit refused to host on this face. With the ray reference: {first.Message} " +
                    $"With the geometry reference: {second.Message}");
            }
        }

        if (_options.HasOffsetFromHost)
        {
            var offset = instance.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM);
            if (offset != null && !offset.IsReadOnly)
                offset.Set(_options.OffsetFromHostFt);
            else
                plan.Notes.Add("This family has no editable 'Offset from Host' parameter; offsetFromHostMm was not applied.");
        }

        plan.ElementId = instance.Id.Value;
    }

    /// <summary>Reads back where the instance ended up and what it is hosted on. Call after regeneration.</summary>
    public void RecordResult(FacePlacementPlan plan)
    {
        if (!plan.ElementId.HasValue) return;
        if (_doc.GetElement(new ElementId(plan.ElementId.Value)) is not FamilyInstance instance) return;

        try
        {
            if (instance.Location is LocationPoint location)
                plan.PlacedPoint = location.Point;
            plan.PlacedHostId = instance.Host?.Id.Value;

            if (instance.Host == null)
                plan.Notes.Add("Revit placed the family but reports no host for it. Check that it is attached to the face.");
        }
        catch
        {
            // Reading the result back is a courtesy; the placement itself succeeded.
        }
    }

    public static string Describe(FaceTarget target)
    {
        var what = string.IsNullOrEmpty(target.HostCategory) ? "element" : target.HostCategory;
        var name = string.IsNullOrEmpty(target.HostName) ? string.Empty : $" '{target.HostName}'";
        var where = target.LinkInstanceId.HasValue ? $" in link '{target.LinkName}'" : string.Empty;
        return $"{what}{name} {target.HostElementId}{where}";
    }
}
