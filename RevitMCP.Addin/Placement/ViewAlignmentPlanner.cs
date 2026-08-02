namespace RevitMCP.Addin.Placement;

/// <summary>One element and how far it has to slide along the mode's axis.</summary>
internal sealed class ViewAlignmentMove
{
    public ViewAlignmentMove(ViewAlignmentTarget target, double offsetFt)
    {
        Target = target;
        OffsetFt = offsetFt;
    }

    public ViewAlignmentTarget Target { get; }

    /// <summary>Signed slide in feet: positive is toward the view's right or up direction.</summary>
    public double OffsetFt { get; }

    public double OffsetMm => ViewAlignmentRequest.FtToMm(OffsetFt);

    public bool IsNegligible => Math.Abs(OffsetMm) < ViewAlignmentRequest.NegligibleMoveMm;
}

internal sealed class ViewAlignmentPlan
{
    /// <summary>One entry per requested element, in the order they were requested.</summary>
    public List<ViewAlignmentMove> Moves { get; } = new();

    /// <summary>The common coordinate everything lines up on, in feet. Null for the distribute modes.</summary>
    public double? TargetCoordinateFt { get; set; }

    /// <summary>Centre-to-centre distance or clear gap produced by a distribute, in feet.</summary>
    public double? StepFt { get; set; }

    /// <summary>How far out of line the elements were before the move, in feet.</summary>
    public double MisalignmentFt { get; set; }
}

/// <summary>
/// Turns measured elements into the slide each one needs. Shared by the preview and the write tool
/// so the preview is a promise, not an estimate.
/// </summary>
internal static class ViewAlignmentPlanner
{
    public static ViewAlignmentPlan Build(
        IReadOnlyList<ViewAlignmentTarget> targets,
        ViewAlignmentOptions options,
        List<string> warnings)
    {
        var plan = new ViewAlignmentPlan();
        var alignable = targets.Where(t => t.CanAlign).ToList();
        var offsets = new Dictionary<long, double>();

        if (alignable.Count > 0)
        {
            var extents = alignable.Select(t => t.ExtentFor(options.Mode)).ToList();
            var alignedValues = extents.Select(e => ViewAlignmentMath.AlignedValue(e, options.Mode)).ToList();
            plan.MisalignmentFt = alignedValues.Max() - alignedValues.Min();

            if (options.IsDistribute)
                Distribute(plan, options, alignable, extents, offsets, warnings);
            else
                Align(plan, options, targets, alignable, extents, offsets, warnings);
        }

        foreach (var target in targets)
        {
            offsets.TryGetValue(target.ElementId, out var offset);
            plan.Moves.Add(new ViewAlignmentMove(target, target.CanAlign ? offset : 0.0));
        }

        return plan;
    }

    private static void Distribute(
        ViewAlignmentPlan plan,
        ViewAlignmentOptions options,
        List<ViewAlignmentTarget> alignable,
        List<Extent> extents,
        Dictionary<long, double> offsets,
        List<string> warnings)
    {
        if (alignable.Count < 2)
        {
            warnings.Add("Nothing left to distribute once unmeasurable elements were dropped.");
            return;
        }

        if (alignable.Count < 3 && !options.SpacingFt.HasValue)
        {
            warnings.Add(
                "Only 2 elements are left to distribute, and they both define the span — nothing " +
                "moves. Pass spacingMm to lay them out at a fixed distance instead.");
        }

        var computed = ViewAlignmentMath.DistributeOffsets(
            extents, options.Spread, options.SpacingFt, out var step);

        plan.StepFt = step;
        if (options.Spread == ViewAlignmentMath.SpreadGaps && step < 0)
        {
            warnings.Add(
                $"The elements overlap by {Math.Abs(ViewAlignmentRequest.FtToMm(step)):F0} mm each: " +
                "together they are wider than the span they currently occupy. Move an outermost " +
                "element out first, or distribute by centers.");
        }

        for (var i = 0; i < alignable.Count; i++)
            offsets[alignable[i].ElementId] = computed[i];
    }

    private static void Align(
        ViewAlignmentPlan plan,
        ViewAlignmentOptions options,
        IReadOnlyList<ViewAlignmentTarget> all,
        List<ViewAlignmentTarget> alignable,
        List<Extent> extents,
        Dictionary<long, double> offsets,
        List<string> warnings)
    {
        var reference = options.Reference;
        var referenceIndex = 0;

        if (reference == ViewAlignmentMath.ReferenceElement)
        {
            var index = ResolveReferenceIndex(all, alignable, options.ReferenceIndex);
            if (index < 0)
            {
                warnings.Add("The reference element could not be measured; aligning to the outermost element instead.");
                reference = ViewAlignmentMath.ReferenceExtreme;
            }
            else
            {
                referenceIndex = index;
            }
        }

        var coordinate = ViewAlignmentMath.ResolveTarget(extents, options.Mode, reference, referenceIndex);
        plan.TargetCoordinateFt = coordinate;

        for (var i = 0; i < alignable.Count; i++)
            offsets[alignable[i].ElementId] = ViewAlignmentMath.OffsetToTarget(extents[i], options.Mode, coordinate);
    }

    /// <summary>
    /// Maps the reference element's position in the requested list onto its position in the
    /// measurable subset. Returns -1 when the nominated element could not be measured.
    /// </summary>
    private static int ResolveReferenceIndex(
        IReadOnlyList<ViewAlignmentTarget> all,
        List<ViewAlignmentTarget> alignable,
        int requestedIndex)
    {
        if (requestedIndex < 0 || requestedIndex >= all.Count)
            return -1;

        var id = all[requestedIndex].ElementId;
        return alignable.FindIndex(t => t.ElementId == id);
    }
}
