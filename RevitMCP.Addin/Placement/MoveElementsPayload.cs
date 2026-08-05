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
        currentPointMm = Point(plan.CurrentPointMm),
        targetPointMm = Point(plan.TargetPointMm),
        translationMm = Point(plan.TranslationMm),
        distanceMm = MoveElementsMath.Round(plan.DistanceMm),
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
        failed = summary.Failed,
        rolledBack = summary.RolledBack,
        notAttempted = summary.NotAttempted
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
