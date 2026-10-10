namespace RevitMCP.Addin.Placement;

/// <summary>
/// Shapes move plans into the response both tools return, so a preview entry and the matching
/// result entry read the same and can be diffed by eye.
/// </summary>
internal static class MoveElementsPayload
{
    public static object Describe(MovePlan plan) => new
    {
        elementId = plan.ElementId,
        elementName = plan.ElementName,
        category = plan.CategoryName,
        mode = plan.Mode,
        locationKind = plan.LocationKind,
        ownerViewId = plan.OwnerViewId,
        ownerViewName = plan.OwnerViewName,
        viewAxes = Axes(plan),
        groupId = plan.GroupId,
        currentPointMm = Point(plan.CurrentPointMm),
        targetPointMm = Point(plan.TargetPointMm),
        translationMm = Point(plan.TranslationMm),
        distanceMm = MoveElementsMath.Round(plan.DistanceMm),
        resultPointMm = Point(plan.ResultPointMm),
        actualTranslationMm = Point(plan.ActualTranslationMm),
        pinned = plan.Pinned,
        canMove = plan.CanMove,
        status = plan.Status,
        staleDeviationMm = plan.StaleDeviationMm.HasValue
            ? MoveElementsMath.Round(plan.StaleDeviationMm.Value)
            : (double?)null,
        reason = plan.Reason
    };

    public static object Summarise(MoveSummary summary) => new
    {
        moved = summary.Moved,
        skipped = summary.Skipped,
        stale = summary.Stale,
        missing = summary.Missing,
        pinned = summary.Pinned,
        unsupportedLocation = summary.Unsupported,
        constrained = summary.Constrained,
        failed = summary.Failed,
        rolledBack = summary.RolledBack,
        notAttempted = summary.NotAttempted
    };

    /// <summary>
    /// The view's axes as unit vectors in model coordinates, so a caller can turn "right 500 mm in
    /// this section" into model X/Y/Z and back. Null when no view was involved.
    /// </summary>
    private static object? Axes(MovePlan plan) => plan.ViewFrame.HasValue
        ? new
        {
            viewId = plan.FrameViewId,
            viewName = plan.FrameViewName,
            right = Unit(plan.ViewFrame.Value.Right),
            up = Unit(plan.ViewFrame.Value.Up),
            normal = Unit(plan.ViewFrame.Value.Normal)
        }
        : null;

    private static object Unit(PointMm vector) => new
    {
        x = Math.Round(vector.X, 6),
        y = Math.Round(vector.Y, 6),
        z = Math.Round(vector.Z, 6)
    };

    private static object? Point(PointMm? point) => point.HasValue
        ? new
        {
            x = MoveElementsMath.Round(point.Value.X),
            y = MoveElementsMath.Round(point.Value.Y),
            z = MoveElementsMath.Round(point.Value.Z)
        }
        : null;
}
