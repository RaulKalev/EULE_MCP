using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Placement;

namespace RevitMCP.Addin.Mep;

/// <summary>Everything the builder needs, resolved against the live model by the create tool.</summary>
internal sealed class MepBuildRequest
{
    public Document Doc { get; init; } = null!;
    public MEPCurveType Type { get; init; } = null!;
    public string Kind { get; init; } = MepKind.CableTray;
    public Level Level { get; init; } = null!;
    public MepRunSpec Spec { get; init; } = null!;
    public MepRunPlan Plan { get; init; } = null!;
    public WorksetId? WorksetId { get; init; }
    public string RunId { get; init; } = string.Empty;
}

internal sealed class MepSegmentResult
{
    public int Index { get; init; }
    public long? ElementId { get; set; }
    public MepPoint From { get; init; }
    public MepPoint To { get; init; }
    public double LengthMm { get; set; }
    public string Status { get; set; } = MepStatus.Skipped;
    public string? Error { get; set; }
    public List<string> Notes { get; } = new();
}

internal sealed class MepFittingResult
{
    public int BendIndex { get; init; }
    public long? ElementId { get; set; }
    public MepPoint At { get; init; }
    public double AngleDeg { get; init; }
    public string Plane { get; init; } = "horizontal";
    public string Status { get; set; } = MepStatus.Skipped;
    public string? Error { get; set; }
}

internal sealed class MepConnectionResult
{
    public string End { get; init; } = "start";
    public string Requested { get; init; } = "none";
    public long? ToElementId { get; set; }
    public string Status { get; set; } = MepStatus.Skipped;
    public string? Action { get; set; }
    public long? FittingId { get; set; }
    public double? DistanceMm { get; set; }
    public string? Relation { get; set; }
    public string? Reason { get; set; }
}

internal sealed class MepBuildResult
{
    public List<MepSegmentResult> Segments { get; } = new();
    public List<MepFittingResult> Fittings { get; } = new();
    public List<MepConnectionResult> Connections { get; } = new();
    public List<string> Warnings { get; } = new();
}

/// <summary>What one join of two open ends did.</summary>
internal sealed class MepJoinResult
{
    public string Status { get; set; } = MepStatus.Failed;
    public string? Action { get; set; }
    public long? FittingId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public MepConnectGeometry? Geometry { get; set; }
}

/// <summary>Thrown to abandon an atomic run: escaping the transaction body rolls it back.</summary>
internal sealed class MepBuildAbortedException : Exception
{
    public MepBuildAbortedException(string message) : base(message) { }
}

/// <summary>
/// The Revit calls of the MEP routing tools (#106): CableTray.Create / Conduit.Create per segment, NewElbowFitting per
/// bend, and the joins at the run ends. Runs inside the caller's transaction. A fitting or end connection that fails is
/// rolled back on its own (SubTransaction) and reported; it never takes the straight segments with it.
/// </summary>
internal static class MepRunBuilder
{
    public static MepBuildResult Build(MepBuildRequest request, CancellationToken cancellationToken)
    {
        var doc = request.Doc;
        var result = new MepBuildResult();
        var spec = request.Spec;
        var created = new Dictionary<int, MEPCurve>();

        // 1. Segments.
        foreach (var segment in request.Plan.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = new MepSegmentResult { Index = segment.Index, From = segment.From, To = segment.To, LengthMm = segment.LengthMm };
            result.Segments.Add(item);

            if (spec.Atomic)
            {
                try
                {
                    created[segment.Index] = CreateSegment(request, segment, item);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    item.Status = MepStatus.Failed;
                    item.Error = ex.Message;
                    throw new MepBuildAbortedException(
                        $"atomic=true: Revit refused segment {segment.Index} ({ex.Message}), so the whole run was rolled back.");
                }
                continue;
            }

            using var sub = new SubTransaction(doc);
            sub.Start();
            try
            {
                var curve = CreateSegment(request, segment, item);
                doc.Regenerate();
                sub.Commit();
                created[segment.Index] = curve;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
                item.Status = MepStatus.Failed;
                item.Error = ex.Message;
                item.ElementId = null;
            }
        }

        doc.Regenerate();

        // 2. An elbow at every bend whose two segments exist.
        foreach (var bend in request.Plan.Bends)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fitting = new MepFittingResult { BendIndex = bend.Index, At = bend.At, AngleDeg = bend.AngleDeg, Plane = bend.Plane };
            result.Fittings.Add(fitting);
            if (bend.Fitting == null)
            {
                fitting.Status = MepStatus.Skipped;
                fitting.Error = "fittings=none: the two segments meet without a fitting.";
                continue;
            }
            if (!created.TryGetValue(bend.SegmentBefore, out var before) || !created.TryGetValue(bend.SegmentAfter, out var after))
            {
                fitting.Status = MepStatus.Skipped;
                fitting.Error = "A segment of this bend was not created.";
                continue;
            }
            PlaceElbow(request, bend, before, after, fitting);
        }

        // 3. The run ends.
        var own = created.Values.Select(c => c.Id).ToList();
        if (created.TryGetValue(0, out var first))
            result.Connections.Add(ConnectEnd(request, "start", spec.ConnectStart, first, request.Plan.Segments[0].From, own));
        var lastIndex = request.Plan.Segments.Count - 1;
        if (created.TryGetValue(lastIndex, out var last))
            result.Connections.Add(ConnectEnd(request, "end", spec.ConnectEnd, last, request.Plan.Segments[lastIndex].To, own));

        doc.Regenerate();

        // 4. Read back what Revit made.
        foreach (var item in result.Segments.Where(s => s.ElementId.HasValue))
        {
            var curve = doc.GetElement(new ElementId(item.ElementId!.Value)) as MEPCurve;
            if ((curve?.Location as LocationCurve)?.Curve is { } line)
                item.LengthMm = MepMath.Round(MepTypeCatalog.FtToMm(line.Length));
        }
        return result;
    }

    private static MEPCurve CreateSegment(MepBuildRequest request, MepPlannedSegment segment, MepSegmentResult item)
    {
        var doc = request.Doc;
        var from = PlacementHelpers.PointFromMm(segment.From.X, segment.From.Y, segment.From.Z);
        var to = PlacementHelpers.PointFromMm(segment.To.X, segment.To.Y, segment.To.Z);
        MEPCurve curve = request.Kind == MepKind.CableTray
            ? CableTray.Create(doc, request.Type.Id, from, to, request.Level.Id)
            : Conduit.Create(doc, request.Type.Id, from, to, request.Level.Id);
        if (curve == null)
            throw new InvalidOperationException("Revit returned no element.");

        var spec = request.Spec;
        if (request.Kind == MepKind.CableTray)
        {
            if (spec.WidthMm.HasValue) SetLength(curve, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM, spec.WidthMm.Value, "width");
            if (spec.HeightMm.HasValue) SetLength(curve, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM, spec.HeightMm.Value, "height");
        }
        else if (spec.DiameterMm.HasValue)
        {
            SetLength(curve, BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM, spec.DiameterMm.Value, "diameter");
        }

        if (request.WorksetId != null && !WorksetAssigner.TryAssign(doc, curve, request.WorksetId, out var reason))
            item.Notes.Add($"Not moved to the workset: {reason}.");

        MepRunSchema.Mark(curve, request.RunId, request.Kind, MepRunSchema.RoleSegment);
        item.ElementId = curve.Id.Value;
        item.Status = MepStatus.Created;
        return curve;
    }

    private static void SetLength(Element element, BuiltInParameter bip, double mm, string what)
    {
        var parameter = element.get_Parameter(bip);
        if (parameter == null || parameter.IsReadOnly)
            throw new InvalidOperationException($"The {what} of this type cannot be set.");
        if (!parameter.Set(PlacementHelpers.MmToFt(mm)))
            throw new InvalidOperationException($"Revit refused the {what} {MepMath.FormatMm(mm)} mm.");
    }

    private static void PlaceElbow(MepBuildRequest request, MepPlannedBend bend, MEPCurve before, MEPCurve after, MepFittingResult fitting)
    {
        var doc = request.Doc;
        var at = PlacementHelpers.PointFromMm(bend.At.X, bend.At.Y, bend.At.Z);
        using var sub = new SubTransaction(doc);
        sub.Start();
        try
        {
            var a = MepConnectorFinder.NearestEnd(before, at, openOnly: true)
                    ?? throw new InvalidOperationException("The segment before the bend has no open end there.");
            var b = MepConnectorFinder.NearestEnd(after, at, openOnly: true)
                    ?? throw new InvalidOperationException("The segment after the bend has no open end there.");
            var elbow = doc.Create.NewElbowFitting(a, b)
                        ?? throw new InvalidOperationException("Revit returned no elbow.");
            MepRunSchema.Mark(elbow, request.RunId, request.Kind, MepRunSchema.RoleFitting);
            doc.Regenerate();
            sub.Commit();
            fitting.ElementId = elbow.Id.Value;
            fitting.Status = MepStatus.Created;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
            fitting.Status = MepStatus.FittingFailed;
            fitting.Error = $"{ex.GetType().Name}: {ex.Message} The two segments stay, unjoined: fix the type's " +
                            $"{(bend.Plane == "vertical" ? "vertical bend" : "bend")} fitting family and join them with revit_connect_mep_curves.";
        }
    }

    private static MepConnectionResult ConnectEnd(MepBuildRequest request, string end, MepEndConnection wanted, MEPCurve segment,
        MepPoint pointMm, ICollection<ElementId> own)
    {
        var doc = request.Doc;
        var connection = new MepConnectionResult { End = end, Requested = wanted.Describe() };
        if (wanted.Mode == MepEndMode.None)
        {
            connection.Status = MepStatus.Skipped;
            return connection;
        }

        var point = PlacementHelpers.PointFromMm(pointMm.X, pointMm.Y, pointMm.Z);
        var runConnector = MepConnectorFinder.NearestEnd(segment, point, openOnly: true);
        if (runConnector == null)
        {
            connection.Status = MepStatus.NoOpenEnd;
            connection.Reason = "The run end has no open connector.";
            return connection;
        }

        Element? target;
        Connector? targetConnector;
        double maxGap;
        if (wanted.Mode == MepEndMode.Auto)
        {
            var candidate = MepConnectorFinder.Candidates(doc, request.Kind, point, request.Spec.SnapMm, own).FirstOrDefault();
            if (candidate == null)
            {
                connection.Status = MepStatus.NoCandidate;
                connection.Reason = $"No open {MepKind.Label(request.Kind)} end within snapMm ({MepMath.FormatMm(request.Spec.SnapMm)} mm) of the run {end}.";
                return connection;
            }
            target = candidate.Element;
            targetConnector = candidate.Connector;
            maxGap = Math.Max(request.Spec.SnapMm, 1) * 2;
        }
        else
        {
            target = doc.GetElement(new ElementId(wanted.ElementId));
            if (!MepTypeCatalog.IsKind(target, request.Kind))
            {
                connection.ToElementId = wanted.ElementId;
                connection.Status = MepStatus.Failed;
                connection.Reason = target == null
                    ? $"Element {wanted.ElementId} does not exist."
                    : $"Element {wanted.ElementId} is not a {MepKind.Label(request.Kind)}; a run joins trays to trays and conduits to conduits.";
                return connection;
            }
            targetConnector = MepConnectorFinder.NearestEnd(target, point, openOnly: true);
            if (targetConnector == null)
            {
                connection.ToElementId = wanted.ElementId;
                connection.Status = MepStatus.NoOpenEnd;
                connection.Reason = $"Element {wanted.ElementId} has no open end: both its ends are connected.";
                return connection;
            }
            maxGap = MepConnectMath.DefaultMaxGapMm;
        }

        connection.ToElementId = target!.Id.Value;
        connection.DistanceMm = MepMath.Round(MepTypeCatalog.FtToMm(targetConnector!.Origin.DistanceTo(point)));
        var join = Join(doc, segment, runConnector, target, targetConnector, "auto", maxGap, request.RunId, request.Kind);
        connection.Status = join.Status;
        connection.Action = join.Action;
        connection.FittingId = join.FittingId;
        connection.Relation = join.Geometry?.Relation;
        connection.Reason = join.Reason;
        return connection;
    }

    /// <summary>
    /// Joins open end <paramref name="aEnd"/> of <paramref name="a"/> to <paramref name="bEnd"/> of <paramref name="b"/>
    /// with the call MepConnectMath picks. Runs in its own SubTransaction: on failure nothing of the join stays. Must be
    /// called inside a transaction. <paramref name="runId"/> marks a new fitting as part of a run (empty = no marker).
    /// </summary>
    public static MepJoinResult Join(Document doc, MEPCurve a, Connector aEnd, Element b, Connector bEnd, string mode, double maxGapMm,
        string runId, string kind)
    {
        var geometry = MepConnectMath.Analyze(MepConnectorFinder.ToEnd(aEnd), MepConnectorFinder.ToEnd(bEnd));
        var decision = MepConnectMath.Decide(geometry, mode, MepConnectorFinder.SameSize(a, b), maxGapMm);
        var result = new MepJoinResult { Geometry = geometry, Reason = decision.Reason, Action = decision.Action };
        if (decision.Action == null)
        {
            result.Status = decision.RefusedStatus ?? MepStatus.Failed;
            return result;
        }

        using var sub = new SubTransaction(doc);
        sub.Start();
        try
        {
            FamilyInstance? fitting = null;
            var target = bEnd.Origin;
            switch (decision.Action)
            {
                case "connect_to":
                    aEnd.ConnectTo(bEnd);
                    break;
                case "extend":
                    aEnd = ExtendTo(doc, a, aEnd, target);
                    aEnd.ConnectTo(bEnd);
                    break;
                case "union":
                    if (geometry.Relation != MepRelation.Coincident) aEnd = ExtendTo(doc, a, aEnd, target);
                    fitting = doc.Create.NewUnionFitting(aEnd, bEnd);
                    break;
                case "transition":
                    fitting = doc.Create.NewTransitionFitting(aEnd, bEnd);
                    break;
                case "elbow":
                    fitting = doc.Create.NewElbowFitting(aEnd, bEnd);
                    break;
            }
            if (decision.Action is "union" or "transition" or "elbow" && fitting == null)
                throw new InvalidOperationException($"Revit returned no {decision.Action} fitting.");
            if (fitting != null && runId.Length > 0)
                MepRunSchema.Mark(fitting, runId, kind, MepRunSchema.RoleFitting);
            doc.Regenerate();
            sub.Commit();
            result.Status = MepStatus.Connected;
            result.FittingId = fitting?.Id.Value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
            result.Status = MepStatus.FittingFailed;
            result.Reason = $"{decision.Reason} Revit refused it: {ex.GetType().Name}: {ex.Message}";
        }
        return result;
    }

    /// <summary>Moves the end of <paramref name="curve"/> at <paramref name="end"/> onto <paramref name="target"/>; returns the moved end's connector.</summary>
    private static Connector ExtendTo(Document doc, MEPCurve curve, Connector end, XYZ target)
    {
        if (curve.Location is not LocationCurve location || location.Curve is not Line line)
            throw new InvalidOperationException("Only a straight curve can be extended.");
        var p0 = line.GetEndPoint(0);
        var p1 = line.GetEndPoint(1);
        var atStart = end.Origin.DistanceTo(p0) < end.Origin.DistanceTo(p1);
        location.Curve = atStart ? Line.CreateBound(target, p1) : Line.CreateBound(p0, target);
        doc.Regenerate();
        return MepConnectorFinder.NearestEnd(curve, target, openOnly: true)
               ?? throw new InvalidOperationException("After extending, the curve has no open end at the target.");
    }
}
