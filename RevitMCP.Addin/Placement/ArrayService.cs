using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Placement;

/// <summary>What one array step produced.</summary>
internal sealed class ArrayStepResult
{
    public ArrayStep Step { get; init; } = null!;
    public List<long> NewIds { get; } = new();
    public List<(long SourceId, long CopyId)> Copies { get; } = new();
    public string? Error { get; set; }
}

/// <summary>A resolved array request.</summary>
internal sealed class ArrayPlan
{
    public ArrayRequest Request { get; init; } = null!;
    public List<CopyItem> Items { get; init; } = new();
    public List<CopyBatch> Batches { get; init; } = new();
    public List<ArrayStep> Steps { get; set; } = new();
    public PointMm? LinearStepMm { get; set; }
    public double StepAngleDeg { get; set; }
    public PointMm ReferenceMm { get; set; }

    /// <summary>The view a Revit array group is created in (asGroup=true).</summary>
    public View? GroupView { get; set; }

    /// <summary>Request-level refusal found while planning (a batch off its view plane, no view for a group).</summary>
    public string? Error { get; set; }
}

internal sealed class ArrayRun
{
    public List<ArrayStepResult> Steps { get; } = new();
    public List<long> NewIds { get; } = new();
    public long? ArrayId { get; set; }
    public int? NumMembers { get; set; }
    public int Failures { get; set; }
}

/// <summary>
/// The Revit side of the array tools (#108). Plain copies (the default) go through
/// <see cref="CopyElementsService.CopySet(Document, CopyBatch, List{ElementId}, Transform)"/> once per step — model or
/// view path chosen exactly as the copy tool does — so every copy is an independent element. asGroup=true creates a
/// Revit array (LinearArray / RadialArray) whose member count is its parameter.
/// </summary>
internal static class ArrayService
{
    public static ArrayPlan? Plan(Document doc, Dictionary<string, object?> arguments, out string? error, out string? status)
    {
        var request = ArrayMath.Parse(arguments, out error, out status);
        if (request == null) return null;

        var (items, batches) = CopyElementsService.Plan(doc, new CopyRequest { ElementIds = request.ElementIds, Atomic = request.Atomic }, request.ElementIds);
        var plan = new ArrayPlan { Request = request, Items = items, Batches = batches };
        var ready = items.Where(i => i.CanCopy).ToList();

        // The view whose axes deltaRightMm/deltaUpMm follow: the owner view when everything belongs to one, else viewId.
        var requestView = request.ViewId > 0 ? doc.GetElement(MoveElementsService.ToElementId(request.ViewId)) as View : null;
        if (request.ViewId > 0 && requestView == null)
        {
            error = $"viewId {request.ViewId} is not a view in this document.";
            return null;
        }
        var viewBatches = batches.Where(b => b.Operation == CopyOperation.View && b.SourceView != null).ToList();
        var frameView = viewBatches.Count == 1 && batches.Count == 1 ? viewBatches[0].SourceView : requestView;

        if (request.Kind == ArrayKind.Linear)
        {
            plan.LinearStepMm = ArrayMath.LinearStep(request, MoveElementsService.FrameOf(frameView), out error);
            if (plan.LinearStepMm == null) return null;
            if (plan.LinearStepMm.Value.Length < MoveElementsMath.NegligibleMoveMm)
            {
                error = "The spacing is zero: every copy would land on the original.";
                return null;
            }
        }
        else
        {
            plan.StepAngleDeg = ArrayMath.StepAngleDeg(request);
        }

        var anchors = ready.Where(i => i.Source?.Anchor != null).Select(i => i.Source!.Anchor!.Value).ToList();
        plan.ReferenceMm = ArrayMath.Centroid(anchors);
        plan.Steps = ArrayMath.Steps(request, plan.LinearStepMm, plan.ReferenceMm);

        // View-specific copies stay in their view: a linear step must lie in the view plane, a radial axis must be the view's normal.
        foreach (var batch in viewBatches)
        {
            var frame = MoveElementsService.FrameOf(batch.SourceView);
            if (frame == null) continue;
            string? batchError = null;
            if (request.Kind == ArrayKind.Linear || !request.RotateElements)
            {
                var worst = plan.Steps.Select(s => MoveElementsMath.OutOfPlaneMm(s.TranslationMm, frame.Value)).DefaultIfEmpty(0).Max();
                if (worst > MoveElementsMath.OutOfPlaneToleranceMm)
                    batchError = $"The elements of view '{batch.SourceView!.Name}' can only be arrayed within that view's plane; the steps leave it by up to " +
                                 $"{MoveElementsMath.Round(worst)} mm. Use deltaRightMm/deltaUpMm, or a spacing in the view plane.";
            }
            else
            {
                var alignment = Math.Abs(ArrayMath.AxisVector(request.Axis).Dot(frame.Value.Normal));
                if (alignment < 1 - 1e-6)
                    batchError = $"The elements of view '{batch.SourceView!.Name}' can only be rotated about an axis perpendicular to that view; " +
                                 $"axis {request.Axis} is not. In a plan use axis z.";
            }
            if (batchError == null) continue;
            batch.Error = batchError;
            foreach (var item in batch.Items.Where(i => i.CanCopy))
            {
                item.Status = CopyStatus.Unsupported;
                item.Reason = batchError;
                item.IsFailure = true;
                item.CanCopy = false;
            }
        }

        if (request.AsGroup)
            PlanGroup(doc, plan, requestView);

        error = null;
        return plan;
    }

    private static void PlanGroup(Document doc, ArrayPlan plan, View? requestView)
    {
        var request = plan.Request;
        var ready = plan.Items.Where(i => i.CanCopy).ToList();
        var used = plan.Batches.Where(b => b.Items.Any(i => i.CanCopy)).ToList();
        if (used.Count > 1)
        {
            plan.Error = "A Revit array holds the model elements, or the elements of one view — not both, and not several views. " +
                         "Array them separately, or pass asGroup=false for plain copies.";
            return;
        }

        if (request.Kind == ArrayKind.Radial && !request.RotateElements)
        {
            plan.Error = "A Revit radial array always turns its members with the array. Pass rotateElements=true, or asGroup=false " +
                         "for copies that keep their orientation.";
            return;
        }

        var batch = used.FirstOrDefault();
        plan.GroupView = batch?.Operation == CopyOperation.View ? batch.SourceView : requestView;
        if (plan.GroupView == null)
        {
            try { plan.GroupView = doc.ActiveView; }
            catch { plan.GroupView = null; }
        }
        if (plan.GroupView == null)
        {
            plan.Error = "A Revit array of model elements is created in a view: pass viewId (this document has no active view).";
            return;
        }

        foreach (var item in ready)
        {
            bool arrayable;
            try { arrayable = LinearArray.IsElementArrayable(doc, MoveElementsService.ToElementId(item.ElementId)); }
            catch { arrayable = false; }
            if (arrayable) continue;
            item.Status = CopyStatus.Unsupported;
            item.Reason = "Revit cannot put this element in an array (LinearArray.IsElementArrayable is false). Pass asGroup=false for plain copies.";
            item.IsFailure = true;
            item.CanCopy = false;
        }
    }

    /// <summary>Creates the array. Must run inside a transaction; throws on an atomic failure so the caller rolls back.</summary>
    public static ArrayRun Execute(Document doc, ArrayPlan plan, CancellationToken cancellationToken)
    {
        return plan.Request.AsGroup ? ExecuteGroup(doc, plan) : ExecuteCopies(doc, plan, cancellationToken);
    }

    private static ArrayRun ExecuteGroup(Document doc, ArrayPlan plan)
    {
        var run = new ArrayRun();
        var request = plan.Request;
        var ids = plan.Items.Where(i => i.CanCopy).Select(i => MoveElementsService.ToElementId(i.ElementId)).ToList();
        if (ids.Count == 0) return run;

        BaseArray array;
        if (request.Kind == ArrayKind.Linear)
        {
            var step = plan.LinearStepMm!.Value;
            array = LinearArray.Create(doc, plan.GroupView!, ids, request.Count, ToFeet(step), ArrayAnchorMember.Second);
        }
        else
        {
            var center = ToFeetPoint(request.CenterMm!.Value);
            var axisVector = ArrayMath.AxisVector(request.Axis);
            var axis = Line.CreateBound(center, center + new XYZ(axisVector.X, axisVector.Y, axisVector.Z));
            array = RadialArray.Create(doc, plan.GroupView!, ids, request.Count, axis, plan.StepAngleDeg * Math.PI / 180.0, ArrayAnchorMember.Second);
        }

        doc.Regenerate();
        run.ArrayId = array.Id.Value;
        try { run.NumMembers = array.NumMembers; }
        catch { run.NumMembers = null; }

        ICollection<ElementId> copied;
        try
        {
            copied = array is LinearArray linear ? linear.GetCopiedMemberIds() : ((RadialArray)array).GetCopiedMemberIds();
        }
        catch
        {
            copied = new List<ElementId>();
        }
        run.NewIds.AddRange(copied.Select(id => id.Value));
        foreach (var item in plan.Items.Where(i => i.CanCopy)) item.Status = CopyStatus.Copied;
        return run;
    }

    private static ArrayRun ExecuteCopies(Document doc, ArrayPlan plan, CancellationToken cancellationToken)
    {
        var run = new ArrayRun();
        var request = plan.Request;

        foreach (var step in plan.Steps)
        {
            var result = new ArrayStepResult { Step = step };
            run.Steps.Add(result);

            foreach (var batch in plan.Batches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ready = batch.Items.Where(i => i.CanCopy).ToList();
                if (ready.Count == 0 || batch.Error != null) continue;

                if (request.Atomic)
                {
                    try
                    {
                        CopyStep(doc, plan, batch, ready, step, result);
                    }
                    catch (Exception ex)
                    {
                        result.Error = ex.Message;
                        run.Failures += ready.Count;
                        foreach (var item in ready)
                        {
                            item.Status = CopyStatus.Failed;
                            item.Reason = $"Step {step.Index}: {ex.Message}";
                            item.IsFailure = true;
                        }
                        throw new CopyBatchAbortedException(ex.Message);
                    }
                    continue;
                }

                using var sub = new SubTransaction(doc);
                sub.Start();
                try
                {
                    CopyStep(doc, plan, batch, ready, step, result);
                    sub.Commit();
                }
                catch (Exception ex)
                {
                    if (sub.GetStatus() == TransactionStatus.Started) sub.RollBack();
                    result.Error = (result.Error == null ? string.Empty : result.Error + " ") + ex.Message;
                    run.Failures += ready.Count;
                }
            }

            run.NewIds.AddRange(result.NewIds);
        }

        foreach (var item in plan.Items.Where(i => i.CanCopy && i.Status != CopyStatus.Failed))
        {
            var copies = run.Steps.Count(s => s.Copies.Any(c => c.SourceId == item.ElementId));
            item.Status = copies > 0 ? CopyStatus.Copied : CopyStatus.Failed;
            if (copies == 0) { item.IsFailure = true; item.Reason ??= "No step produced a copy of this element."; }
        }
        return run;
    }

    /// <summary>One batch, one step: copy the set, then turn the copies for a rotating radial array.</summary>
    private static void CopyStep(Document doc, ArrayPlan plan, CopyBatch batch, List<CopyItem> ready, ArrayStep step, ArrayStepResult result)
    {
        var ids = ready.Select(i => MoveElementsService.ToElementId(i.ElementId)).ToList();
        var transform = Transform.CreateTranslation(ToFeet(step.TranslationMm));
        var created = CopyElementsService.CopySet(doc, batch, ids, transform);

        if (Math.Abs(step.RotationDeg) > 0 && created.Count > 0)
        {
            var center = ToFeetPoint(plan.Request.CenterMm!.Value);
            var axisVector = ArrayMath.AxisVector(plan.Request.Axis);
            var axis = Line.CreateBound(center, center + new XYZ(axisVector.X, axisVector.Y, axisVector.Z));
            ElementTransformUtils.RotateElements(doc, created, axis, step.RotationDeg * Math.PI / 180.0);
        }

        doc.Regenerate();
        var snapshots = created.Select(doc.GetElement).Where(e => e != null).Select(e => CopyElementsService.Snapshot(doc, e!)).ToList();
        var sources = ready.Where(i => i.Source != null).Select(i => i.Source!).ToList();

        // Rotated copies cannot be found at source + translation; they are paired by kind and id order.
        var matches = CopyElementsMath.MatchCopies(sources, snapshots, Math.Abs(step.RotationDeg) > 0 ? null : step.TranslationMm);
        result.NewIds.AddRange(snapshots.Select(s => s.Id));
        foreach (var (sourceId, match) in matches)
            result.Copies.Add((sourceId, match.Copy.Id));
    }

    private static XYZ ToFeet(PointMm vectorMm) =>
        new(MoveElementsMath.MmToFt(vectorMm.X), MoveElementsMath.MmToFt(vectorMm.Y), MoveElementsMath.MmToFt(vectorMm.Z));

    private static XYZ ToFeetPoint(PointMm pointMm) => ToFeet(pointMm);
}
