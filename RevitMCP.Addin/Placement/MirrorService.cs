using Autodesk.Revit.DB;
using RevitMCP.Addin.Tools;

namespace RevitMCP.Addin.Placement;

/// <summary>One source element of a mirror request and what happened to it.</summary>
internal sealed class MirrorItem
{
    public long ElementId { get; init; }
    public ElementSnapshot? Source { get; set; }
    public string? OwnerViewName { get; set; }
    public bool IsGroupInstance { get; set; }

    public string Status { get; set; } = MirrorStatus.Ready;
    public string? Reason { get; set; }
    public bool IsFailure { get; set; }
    public bool CanMirror { get; set; }
    public int BatchIndex { get; set; } = -1;

    /// <summary>The source anchor (insertion point, curve midpoint or box centre), mm.</summary>
    public PointMm? FromMm { get; set; }

    /// <summary>Where the anchor lands: predicted by reflection, then read back from the copy or the moved element.</summary>
    public PointMm? ToMm { get; set; }

    public ElementSnapshot? Copy { get; set; }
    public string? Mapping { get; set; }
    public List<string> Notes { get; } = new();
}

/// <summary>Elements mirrored in one Revit call: the model elements, or those of one owner view.</summary>
internal sealed class MirrorBatch
{
    public View? OwnerView { get; init; }
    public List<MirrorItem> Items { get; } = new();
    public string? Error { get; set; }
}

internal sealed class MirrorOptions
{
    public bool Copy { get; init; } = true;
    public bool Atomic { get; init; } = true;
    public bool SkipPinned { get; init; } = true;
    public bool AllowHorizontalPlane { get; init; }
}

/// <summary>A resolved mirror request: the plane, every element's verdict, the batches.</summary>
internal sealed class MirrorPlan
{
    public MirrorPlaneRequest PlaneRequest { get; init; } = null!;
    public MirrorPlane Plane { get; init; }
    public View? PlaneView { get; init; }
    public MirrorOptions Options { get; init; } = new();
    public List<MirrorItem> Items { get; } = new();
    public List<MirrorBatch> Batches { get; } = new();

    /// <summary>Ids ElementTransformUtils.CanMirrorElement refused.</summary>
    public List<long> NotAllowed { get; } = new();
}

internal sealed class MirrorRun
{
    public List<ElementSnapshot> Created { get; } = new();
    public List<ElementSnapshot> Dependencies { get; } = new();
    public int Done { get; set; }
    public int Failures { get; set; }
}

/// <summary>
/// The Revit side of the mirror tools (#108): resolving the plane and the elements, Revit's own CanMirrorElement
/// check, and the ElementTransformUtils.MirrorElements calls — one per owner view, since Revit mirrors view-specific
/// elements only together with elements of the same view.
/// </summary>
internal static class MirrorService
{
    public static MirrorOptions ParseOptions(Dictionary<string, object?> arguments) => new()
    {
        Copy = ToolArguments.GetBool(arguments, "copy", true),
        Atomic = ToolArguments.GetBool(arguments, "atomic", true),
        SkipPinned = ToolArguments.GetBool(arguments, "skipPinned", true),
        AllowHorizontalPlane = ToolArguments.GetBool(arguments, "allowHorizontalPlane", false)
    };

    /// <summary>Resolves the request, touching nothing. Returns null with <paramref name="error"/> when it cannot run.</summary>
    public static MirrorPlan? Plan(Document doc, Dictionary<string, object?> arguments, out string? error)
    {
        var ids = ToolArguments.GetLongArray(arguments, "elementIds").ToList();
        if (ids.Count == 0)
        {
            error = "Provide elementIds: the elements to mirror.";
            return null;
        }
        if (ids.Count > MirrorMath.MaxElements)
        {
            error = $"'elementIds' holds {ids.Count} entries; the limit is {MirrorMath.MaxElements} per request. Split the batch.";
            return null;
        }
        var duplicate = ids.GroupBy(id => id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
        {
            error = $"Element {duplicate.Key} appears {duplicate.Count()} times in elementIds. List each element once.";
            return null;
        }

        arguments.TryGetValue("plane", out var rawPlane);
        var planeRequest = MirrorMath.ParsePlane(rawPlane, ToolArguments.GetLong(arguments, "viewId"), out error);
        if (planeRequest == null) return null;

        View? planeView = null;
        if (planeRequest.ViewId > 0)
        {
            planeView = doc.GetElement(MoveElementsService.ToElementId(planeRequest.ViewId)) as View;
            if (planeView == null)
            {
                error = $"plane.viewId {planeRequest.ViewId} is not a view in this document.";
                return null;
            }
        }

        var frame = MoveElementsService.FrameOf(planeView);
        if (planeView != null && frame == null && planeRequest.Mode == MirrorPlaneMode.Line)
        {
            error = $"View '{planeView.Name}' has no right/up directions, so a mirror line cannot be drawn in it.";
            return null;
        }

        var plane = MirrorMath.Resolve(planeRequest, frame, planeView?.Name, out error);
        if (plane == null) return null;

        var options = ParseOptions(arguments);
        error = MirrorMath.HorizontalPlaneRefusal(plane.Value, options.AllowHorizontalPlane);
        if (error != null) return null;

        var plan = new MirrorPlan { PlaneRequest = planeRequest, Plane = plane.Value, PlaneView = planeView, Options = options };
        var byView = new Dictionary<long, MirrorBatch>();
        MirrorBatch? modelBatch = null;

        foreach (var id in ids)
        {
            var item = new MirrorItem { ElementId = id };
            plan.Items.Add(item);

            var element = doc.GetElement(MoveElementsService.ToElementId(id));
            if (element == null)
            {
                Reject(item, MirrorStatus.Missing, "No element with this id exists in the document.");
                continue;
            }

            item.Source = CopyElementsService.Snapshot(doc, element);
            item.FromMm = item.Source.Anchor;
            item.ToMm = item.FromMm.HasValue ? MirrorMath.Reflect(item.FromMm.Value, plane.Value) : null;

            if (element is View or ElementType)
            {
                Reject(item, MirrorStatus.Unsupported, element is View
                    ? "This is a view or sheet; views are not mirrored."
                    : "This is a type, not a placed element; pass instance ids.");
                continue;
            }

            var ownerView = MoveElementsService.OwnerViewOf(doc, element);
            item.OwnerViewName = ownerView?.Name;
            if (ownerView != null)
            {
                if (planeView != null && ownerView.Id != planeView.Id)
                {
                    Reject(item, MirrorStatus.Unsupported,
                        $"This element belongs to view '{ownerView.Name}', but the mirror line was drawn in view '{planeView.Name}'. " +
                        "A view-specific element is mirrored in its own view: draw the line there (plane.viewId), or mirror it separately.");
                    continue;
                }
                var ownerFrame = MoveElementsService.FrameOf(ownerView);
                if (ownerFrame != null && !MirrorMath.KeepsViewPlane(plane.Value, ownerFrame.Value))
                {
                    Reject(item, MirrorStatus.Unsupported,
                        $"This element belongs to view '{ownerView.Name}', and {plane.Value.Description} is not perpendicular to that " +
                        "view, so the mirror image would leave the view's plane.");
                    continue;
                }
            }

            if (element is Group)
            {
                item.IsGroupInstance = true;
                item.Notes.Add($"Group instance: the whole group is mirrored, with its {MoveElementsService.MemberCount((Group)element)} member(s).");
            }

            if (element.GroupId != ElementId.InvalidElementId)
            {
                string? groupName = null;
                try { groupName = doc.GetElement(element.GroupId)?.Name; } catch { /* courtesy only */ }
                if (!options.Copy)
                {
                    Reject(item, MirrorStatus.InGroup, MoveElementsMath.GroupMemberReason(element.GroupId.Value, groupName, "mirror"));
                    continue;
                }
                item.Notes.Add($"InGroup: member of group {element.GroupId.Value}. Only this member is mirror-copied, not the group — " +
                               $"pass the group id {element.GroupId.Value} to mirror the whole group.");
            }

            if (element.Pinned)
            {
                if (!options.Copy)
                {
                    if (options.SkipPinned)
                    {
                        item.Status = MirrorStatus.Pinned;
                        item.Reason = "Pinned; skipped because skipPinned=true (copy=false would move it).";
                        continue;
                    }
                    Reject(item, MirrorStatus.Pinned, "Pinned, and skipPinned=false. Unpin it in Revit — this tool never unpins elements.");
                    continue;
                }
                item.Notes.Add("The source is pinned. Mirror-copying leaves it where it is.");
            }

            bool canMirror;
            try { canMirror = ElementTransformUtils.CanMirrorElement(doc, element.Id); }
            catch { canMirror = false; }
            if (!canMirror)
            {
                plan.NotAllowed.Add(id);
                Reject(item, MirrorStatus.NotMirrorable, "Revit cannot mirror this element (ElementTransformUtils.CanMirrorElement is false).");
                continue;
            }

            MirrorBatch batch;
            if (ownerView != null)
            {
                if (!byView.TryGetValue(ownerView.Id.Value, out batch!))
                {
                    batch = new MirrorBatch { OwnerView = ownerView };
                    byView[ownerView.Id.Value] = batch;
                    plan.Batches.Add(batch);
                }
            }
            else
            {
                if (modelBatch == null)
                {
                    modelBatch = new MirrorBatch();
                    plan.Batches.Add(modelBatch);
                }
                batch = modelBatch;
            }

            item.CanMirror = true;
            item.BatchIndex = plan.Batches.IndexOf(batch);
            batch.Items.Add(item);
        }

        // Revit's check for each set as a whole: elements that pass alone can still refuse together.
        foreach (var batch in plan.Batches)
        {
            var set = batch.Items.Select(i => MoveElementsService.ToElementId(i.ElementId)).ToList();
            bool ok;
            try { ok = ElementTransformUtils.CanMirrorElements(doc, set); }
            catch { ok = false; }
            if (ok) continue;
            batch.Error = "Revit cannot mirror this set of elements together (ElementTransformUtils.CanMirrorElements is false). " +
                          "Run with atomic=false to mirror element by element.";
            if (!options.Atomic) continue;
            foreach (var item in batch.Items)
            {
                plan.NotAllowed.Add(item.ElementId);
                Reject(item, MirrorStatus.NotMirrorable, batch.Error);
            }
        }

        error = null;
        return plan;
    }

    /// <summary>The Revit plane, in internal units.</summary>
    public static Plane ToPlane(MirrorPlane plane) => Plane.CreateByNormalAndOrigin(
        new XYZ(plane.Normal.X, plane.Normal.Y, plane.Normal.Z),
        new XYZ(MoveElementsMath.MmToFt(plane.Origin.X), MoveElementsMath.MmToFt(plane.Origin.Y), MoveElementsMath.MmToFt(plane.Origin.Z)));

    /// <summary>
    /// Mirrors what can be mirrored. Must run inside a transaction. atomic=true mirrors each batch as one set and throws
    /// on the first refusal, so the caller's transaction rolls back; atomic=false goes element by element in
    /// sub-transactions and keeps what succeeds.
    /// </summary>
    public static MirrorRun Execute(Document doc, MirrorPlan plan, CancellationToken cancellationToken)
    {
        var run = new MirrorRun();
        var revitPlane = ToPlane(plan.Plane);

        foreach (var batch in plan.Batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ready = batch.Items.Where(i => i.CanMirror).ToList();
            if (ready.Count == 0) continue;

            if (plan.Options.Atomic)
            {
                List<ElementId> created;
                try
                {
                    created = MirrorSet(doc, ready, revitPlane, plan.Options.Copy);
                }
                catch (Exception ex)
                {
                    foreach (var item in ready)
                    {
                        item.Status = MirrorStatus.Failed;
                        item.Reason = ready.Count == 1 ? ex.Message
                            : $"Revit refused the set of {ready.Count} elements this one was mirrored with: {ex.Message} " +
                              "Run with atomic=false to see which one is refused.";
                        item.IsFailure = true;
                    }
                    run.Failures += ready.Count;
                    throw new MirrorBatchAbortedException(ex.Message);
                }
                doc.Regenerate();
                Record(doc, plan, ready, created, run);
                continue;
            }

            foreach (var item in ready)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var sub = new SubTransaction(doc);
                sub.Start();
                try
                {
                    var created = MirrorSet(doc, new List<MirrorItem> { item }, revitPlane, plan.Options.Copy);
                    doc.Regenerate();
                    sub.Commit();
                    Record(doc, plan, new List<MirrorItem> { item }, created, run);
                }
                catch (Exception ex)
                {
                    if (sub.GetStatus() == TransactionStatus.Started) sub.RollBack();
                    item.Status = MirrorStatus.Failed;
                    item.Reason = ex.Message;
                    item.IsFailure = true;
                    run.Failures++;
                }
            }
        }

        return run;
    }

    /// <summary>The one place ElementTransformUtils.MirrorElements is called. Returns the new ids (empty when copy=false).</summary>
    private static List<ElementId> MirrorSet(Document doc, List<MirrorItem> items, Plane plane, bool copy)
    {
        var ids = items.Select(i => MoveElementsService.ToElementId(i.ElementId)).ToList();
        // 2024 and 2026 both return the new ids as a collection (IList<ElementId>); empty when mirrorCopies=false.
        var created = ElementTransformUtils.MirrorElements(doc, ids, plane, copy);
        return created?.ToList() ?? new List<ElementId>();
    }

    private static void Record(Document doc, MirrorPlan plan, List<MirrorItem> ready, List<ElementId> createdIds, MirrorRun run)
    {
        if (!plan.Options.Copy)
        {
            foreach (var item in ready)
            {
                item.Status = MirrorStatus.Moved;
                run.Done++;
                var element = doc.GetElement(MoveElementsService.ToElementId(item.ElementId));
                if (element != null) item.ToMm = CopyElementsService.Snapshot(doc, element).Anchor ?? item.ToMm;
            }
            return;
        }

        var created = createdIds.Select(doc.GetElement).Where(e => e != null).Select(e => CopyElementsService.Snapshot(doc, e!)).ToList();
        run.Created.AddRange(created);
        var sources = ready.Where(i => i.Source != null).Select(i => i.Source!).ToList();
        var matches = MirrorMath.MatchMirrored(sources, created, plan.Plane);
        run.Dependencies.AddRange(CopyElementsMath.Dependencies(created, matches));

        foreach (var item in ready)
        {
            item.Status = MirrorStatus.Mirrored;
            run.Done++;
            if (!matches.TryGetValue(item.ElementId, out var match))
            {
                item.Notes.Add("Mirrored, but its copy could not be told apart from the other new elements of the same type — see newIds.");
                continue;
            }
            item.Copy = match.Copy;
            item.Mapping = match.Mapping;
            if (match.Copy.Anchor.HasValue) item.ToMm = match.Copy.Anchor;
        }
    }

    private static void Reject(MirrorItem item, string status, string reason)
    {
        item.Status = status;
        item.Reason = reason;
        item.IsFailure = true;
        item.CanMirror = false;
    }
}

/// <summary>Thrown to abandon an atomic mirror. Escaping the transaction body is what rolls it back.</summary>
internal sealed class MirrorBatchAbortedException : Exception
{
    public MirrorBatchAbortedException(string reason)
        : base($"atomic=true: Revit refused the mirror ({reason}), so the whole batch was rolled back.")
    {
    }
}
