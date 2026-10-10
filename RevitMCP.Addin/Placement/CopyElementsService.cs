using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Placement;

/// <summary>
/// A set of elements that goes to Revit in one copy call: everything owned by one view, or all the
/// model elements. Copying a set together is what keeps relationships between the copies — a tag
/// and its element, a hosted family and its host.
/// </summary>
internal sealed class CopyBatch
{
    public string Operation { get; init; } = CopyOperation.Document;
    public View? SourceView { get; init; }
    public View? DestinationView { get; init; }
    public List<CopyItem> Items { get; } = new();

    /// <summary>The translation in model coordinates, in mm. Null when it could not be resolved.</summary>
    public PointMm? TranslationMm { get; set; }

    /// <summary>Why this batch cannot be copied as requested, when it cannot.</summary>
    public string? Error { get; set; }

    /// <summary>True when the destination is a different view, so positions are not source + translation.</summary>
    public bool CrossView =>
        Operation == CopyOperation.View && SourceView != null && DestinationView != null &&
        SourceView.Id != DestinationView.Id;
}

/// <summary>What one copy run produced.</summary>
internal sealed class CopyRunResult
{
    public List<ElementSnapshot> Created { get; } = new();
    public List<ElementSnapshot> Dependencies { get; } = new();
    public int Copied { get; set; }
    public int Failures { get; set; }
}

/// <summary>
/// The Revit side of the element copy tools (#91). No category whitelist: every element is handed
/// to Revit's own copy operation, and what Revit refuses is reported with Revit's reason. The only
/// things turned away up front are those Revit's Copy command does not handle at all — views,
/// sheets and family types, which have their own duplicate tools.
/// </summary>
internal static class CopyElementsService
{
    public static ElementId ToElementId(long value) => MoveElementsService.ToElementId(value);

    /// <summary>
    /// Resolves the elements and sorts them into batches, touching nothing. Each item ends up Ready,
    /// or with the reason it cannot be copied.
    /// </summary>
    public static (List<CopyItem> Items, List<CopyBatch> Batches) Plan(
        Document doc, CopyRequest request, IReadOnlyList<long> elementIds)
    {
        var items = new List<CopyItem>(elementIds.Count);
        var targetView = request.TargetViewId > 0 ? doc.GetElement(ToElementId(request.TargetViewId)) as View : null;
        var sourceView = request.SourceViewId > 0 ? doc.GetElement(ToElementId(request.SourceViewId)) as View : null;

        var viewSpecific = new Dictionary<long, CopyBatch>();
        CopyBatch? modelBatch = null;
        var batches = new List<CopyBatch>();

        foreach (var id in elementIds)
        {
            var item = new CopyItem { ElementId = id };
            items.Add(item);

            var element = doc.GetElement(ToElementId(id));
            if (element == null)
            {
                Reject(item, CopyStatus.Missing, "No element with this id exists in the active document.");
                continue;
            }

            item.Source = Snapshot(doc, element);

            var route = RouteElsewhere(element);
            if (route != null)
            {
                Reject(item, CopyStatus.UseOtherTool, route);
                continue;
            }

            var ownerView = OwnerView(doc, element);
            item.OwnerViewName = ownerView?.Name;

            CopyBatch batch;
            if (ownerView != null)
            {
                if (!viewSpecific.TryGetValue(ownerView.Id.Value, out batch!))
                {
                    batch = new CopyBatch
                    {
                        Operation = CopyOperation.View,
                        SourceView = ownerView,
                        DestinationView = targetView ?? ownerView
                    };
                    viewSpecific[ownerView.Id.Value] = batch;
                    batches.Add(batch);
                }
            }
            else
            {
                if (modelBatch == null)
                {
                    modelBatch = request.TargetViewId > 0
                        ? new CopyBatch { Operation = CopyOperation.View, SourceView = sourceView, DestinationView = targetView }
                        : new CopyBatch { Operation = CopyOperation.Document, SourceView = sourceView };
                    batches.Add(modelBatch);
                }
                batch = modelBatch;
            }

            item.BatchIndex = batches.IndexOf(batch);
            item.CanCopy = true;
            batch.Items.Add(item);

            if (item.Source.GroupId.HasValue)
                item.Notes.Add($"Member of group {item.Source.GroupId}: only this member is copied, not the group. " +
                               "Copy the group instance to copy the whole group.");
            if (item.Source.Pinned)
                item.Notes.Add("The source is pinned. Copying does not move or unpin it.");
        }

        foreach (var batch in batches)
        {
            Validate(doc, request, batch);
            if (batch.Error == null) continue;
            foreach (var item in batch.Items)
                Reject(item, CopyStatus.Unsupported, batch.Error);
        }

        return (items, batches);
    }

    /// <summary>Views, sheets and types are not copied by Revit's Copy; say which tool does it.</summary>
    private static string? RouteElsewhere(Element element) => element switch
    {
        ViewSheet => "This is a sheet. Sheets are duplicated with revit_duplicate (entity=sheets), not copied by a translation.",
        View => "This is a view. Views are duplicated with revit_duplicate (entity=views), not copied by a translation.",
        ElementType => "This is a type, not a placed element. Family types are duplicated with revit_duplicate " +
                       "(entity=familyTypes); to copy placed instances pass their instance ids.",
        _ => null
    };

    /// <summary>Checks the views and the translation of one batch and resolves the translation.</summary>
    private static void Validate(Document doc, CopyRequest request, CopyBatch batch)
    {
        if (request.TargetViewId > 0 && batch.DestinationView == null)
        {
            batch.Error = $"targetViewId {request.TargetViewId} is not a view in the active document.";
            return;
        }

        if (batch.Operation == CopyOperation.View)
        {
            if (batch.SourceView == null)
            {
                batch.Error = request.SourceViewId > 0
                    ? $"sourceViewId {request.SourceViewId} is not a view in the active document."
                    : "These are model elements, which no view owns. To copy them into targetViewId, also pass " +
                      "sourceViewId — the view they are copied from (for example the plan of their level). " +
                      "Without targetViewId they are copied in the model by the translation alone.";
                return;
            }

            if (batch.DestinationView!.IsTemplate || batch.SourceView.IsTemplate)
            {
                batch.Error = "A view template cannot be the source or destination of a copy.";
                return;
            }

            // Revit's own compatibility check: both views must be 2D graphical views that can hold
            // detail elements (plans, sections, elevations, drafting views).
            try
            {
                ElementTransformUtils.GetTransformFromViewToView(batch.SourceView, batch.DestinationView);
            }
            catch (Exception ex)
            {
                batch.Error = $"Revit cannot copy between view '{batch.SourceView.Name}' ({batch.SourceView.ViewType}) and " +
                              $"view '{batch.DestinationView.Name}' ({batch.DestinationView.ViewType}): {ex.Message}";
                return;
            }
        }

        // The axes for deltaRightMm/deltaUpMm: the destination view's for a view copy, the named
        // sourceViewId's for a model-wide copy.
        var frameView = batch.Operation == CopyOperation.View ? batch.DestinationView : batch.SourceView;
        var frame = MoveElementsService.FrameOf(frameView);
        var delta = request.AsDelta();

        if (delta.HasViewDelta && frame == null)
        {
            batch.Error = frameView == null
                ? "deltaRightMm/deltaUpMm follow a view's axes. These are model elements, so pass sourceViewId to name " +
                  "the view whose right/up directions to use, or give deltaXmm/deltaYmm/deltaZmm."
                : $"View '{frameView.Name}' has no right/up directions, so deltaRightMm/deltaUpMm cannot be used with it.";
            return;
        }

        var translation = MoveElementsMath.TranslationFromDelta(delta, frame, out var error);
        if (translation == null)
        {
            batch.Error = error;
            return;
        }

        batch.TranslationMm = translation;

        if (batch.Operation == CopyOperation.View)
        {
            var destinationFrame = MoveElementsService.FrameOf(batch.DestinationView);
            if (destinationFrame != null)
            {
                var offPlane = MoveElementsMath.OutOfPlaneMm(translation.Value, destinationFrame.Value);
                if (offPlane > MoveElementsMath.OutOfPlaneToleranceMm)
                {
                    var normal = destinationFrame.Value.Normal;
                    batch.Error =
                        $"A copy into view '{batch.DestinationView!.Name}' can only be shifted within that view's plane. " +
                        $"The translation leaves the plane by {MoveElementsMath.Round(offPlane)} mm " +
                        $"(view normal {normal.X:0.###}, {normal.Y:0.###}, {normal.Z:0.###}). " +
                        "Use deltaRightMm/deltaUpMm to shift along the view's own axes.";
                }
            }
        }
    }

    /// <summary>
    /// Copies everything that can be copied. Must run inside a transaction. atomic=true copies each
    /// batch as one set and throws on the first refusal, so the caller's transaction rolls back;
    /// atomic=false copies element by element in sub-transactions and keeps what succeeds.
    /// </summary>
    public static CopyRunResult Execute(
        Document doc,
        List<CopyItem> items,
        List<CopyBatch> batches,
        bool atomic,
        CancellationToken cancellationToken)
    {
        var run = new CopyRunResult();

        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ready = batch.Items.Where(item => item.CanCopy).ToList();
            if (ready.Count == 0 || batch.Error != null) continue;

            if (atomic)
            {
                List<ElementId> createdIds;
                try
                {
                    createdIds = CopySet(doc, batch, ready.Select(item => ToElementId(item.ElementId)).ToList());
                }
                catch (Exception ex)
                {
                    foreach (var item in ready)
                    {
                        item.Status = CopyStatus.Failed;
                        item.Reason = ready.Count == 1
                            ? ex.Message
                            : $"Revit refused the set of {ready.Count} elements this one was copied with: {ex.Message} " +
                              "Run with atomic=false to copy element by element and see which one is refused.";
                        item.IsFailure = true;
                    }
                    run.Failures += ready.Count;
                    throw new CopyBatchAbortedException(ex.Message);
                }

                doc.Regenerate();
                Pair(doc, batch, ready, createdIds, run);
                continue;
            }

            foreach (var item in ready)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var subTransaction = new SubTransaction(doc);
                subTransaction.Start();
                try
                {
                    var createdIds = CopySet(doc, batch, new List<ElementId> { ToElementId(item.ElementId) });
                    doc.Regenerate();
                    subTransaction.Commit();
                    Pair(doc, batch, new List<CopyItem> { item }, createdIds, run);
                }
                catch (Exception ex)
                {
                    if (subTransaction.GetStatus() == TransactionStatus.Started)
                        subTransaction.RollBack();
                    item.Status = CopyStatus.Failed;
                    item.Reason = ex.Message;
                    item.IsFailure = true;
                    run.Failures++;
                }
            }
        }

        return run;
    }

    /// <summary>The one place Revit's copy operations are called.</summary>
    private static List<ElementId> CopySet(Document doc, CopyBatch batch, List<ElementId> ids)
    {
        var t = batch.TranslationMm!.Value;
        var transform = Transform.CreateTranslation(new XYZ(
            MoveElementsMath.MmToFt(t.X), MoveElementsMath.MmToFt(t.Y), MoveElementsMath.MmToFt(t.Z)));

        var options = new CopyPasteOptions();
        options.SetDuplicateTypeNamesHandler(new UseDestinationTypes());

        var created = batch.Operation == CopyOperation.View
            ? ElementTransformUtils.CopyElements(batch.SourceView, ids, batch.DestinationView, transform, options)
            : ElementTransformUtils.CopyElements(doc, ids, doc, transform, options);

        return created?.ToList() ?? new List<ElementId>();
    }

    /// <summary>Works out which created element is the copy of which source, and what else came along.</summary>
    private static void Pair(Document doc, CopyBatch batch, List<CopyItem> ready, List<ElementId> createdIds, CopyRunResult run)
    {
        var created = createdIds
            .Select(doc.GetElement)
            .Where(element => element != null)
            .Select(element => Snapshot(doc, element!))
            .ToList();
        run.Created.AddRange(created);

        // Across views the copies are repositioned by Revit (a different level, a different plane),
        // so position cannot identify them — only kind and order can.
        var sources = ready.Where(item => item.Source != null).Select(item => item.Source!).ToList();
        var matches = CopyElementsMath.MatchCopies(sources, created, batch.CrossView ? null : batch.TranslationMm);
        run.Dependencies.AddRange(CopyElementsMath.Dependencies(created, matches));

        foreach (var item in ready)
        {
            item.Status = CopyStatus.Copied;
            run.Copied++;

            if (!matches.TryGetValue(item.ElementId, out var match))
            {
                item.Notes.Add("Copied, but its copy could not be told apart from the other new elements of the same type — " +
                               "see newElementIds.");
                continue;
            }

            item.Copy = match.Copy;
            item.Mapping = match.Mapping;

            var sourceHost = item.Source?.HostId;
            if (sourceHost.HasValue || match.Copy.HostId.HasValue)
            {
                item.Notes.Add(sourceHost == match.Copy.HostId
                    ? $"Hosted on the same element as the source ({sourceHost})."
                    : $"Host changed: source on {(sourceHost.HasValue ? sourceHost.ToString() : "none")}, " +
                      $"copy on {(match.Copy.HostId.HasValue ? match.Copy.HostId.ToString() : "none")}.");
            }
        }
    }

    public static ElementSnapshot Snapshot(Document doc, Element element)
    {
        var ownerView = OwnerView(doc, element);
        return new ElementSnapshot
        {
            Id = element.Id.Value,
            ClassName = element.GetType().Name,
            Category = Safe(() => element.Category?.Name) ?? string.Empty,
            Name = Safe(() => element.Name) ?? string.Empty,
            TypeId = Safe(() => element.GetTypeId().Value, -1),
            Anchor = Anchor(element, ownerView),
            OwnerViewId = ownerView?.Id.Value,
            HostId = HostId(element),
            GroupId = element.GroupId == ElementId.InvalidElementId ? null : element.GroupId.Value,
            Pinned = Safe(() => element.Pinned, false)
        };
    }

    /// <summary>
    /// A point that identifies where the element is. Used only to recognise a copy at
    /// "source + translation"; nothing is ever placed on it.
    /// </summary>
    private static PointMm? Anchor(Element element, View? ownerView)
    {
        try
        {
            switch (element.Location)
            {
                case LocationPoint point:
                    return MoveElementsMath.PointFromFeet(point.Point.X, point.Point.Y, point.Point.Z);
                case LocationCurve { Curve: { IsBound: true } curve }:
                    var mid = curve.Evaluate(0.5, true);
                    return MoveElementsMath.PointFromFeet(mid.X, mid.Y, mid.Z);
            }

            var box = element.get_BoundingBox(ownerView);
            if (box == null) return null;
            var centre = (box.Min + box.Max) * 0.5;
            return MoveElementsMath.PointFromFeet(centre.X, centre.Y, centre.Z);
        }
        catch
        {
            return null;
        }
    }

    private static long? HostId(Element element)
    {
        try
        {
            return element switch
            {
                FamilyInstance { Host: { } host } => host.Id.Value,
                IndependentTag tag => tag.GetTaggedLocalElementIds().FirstOrDefault()?.Value,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static View? OwnerView(Document doc, Element element)
    {
        try
        {
            var ownerId = element.OwnerViewId;
            return ownerId == ElementId.InvalidElementId ? null : doc.GetElement(ownerId) as View;
        }
        catch
        {
            return null;
        }
    }

    private static void Reject(CopyItem item, string status, string reason)
    {
        item.Status = status;
        item.Reason = reason;
        item.IsFailure = true;
        item.CanCopy = false;
    }

    private static T Safe<T>(Func<T> read, T fallback = default!)
    {
        try { return read(); }
        catch { return fallback; }
    }

    /// <summary>Same document, so a type name can only "clash" with itself: reuse the existing type, never prompt.</summary>
    private sealed class UseDestinationTypes : IDuplicateTypeNamesHandler
    {
        public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args) =>
            DuplicateTypeAction.UseDestinationTypes;
    }
}

/// <summary>Thrown to abandon an atomic copy. Escaping the transaction body is what rolls it back.</summary>
internal sealed class CopyBatchAbortedException : Exception
{
    public CopyBatchAbortedException(string reason)
        : base($"atomic=true: Revit refused a copy ({reason}), so the whole batch was rolled back.")
    {
    }
}
