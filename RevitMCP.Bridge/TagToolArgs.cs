namespace RevitMCP.Bridge;

/// <summary>
/// Pipe arguments for the tag tools. Pure — unit tested in RevitMCP.Tests. The add-in treats a
/// present key as "the caller asked for this", so optional per-tag settings must stay out of the
/// arguments when the caller did not give them.
/// </summary>
internal static class TagToolArgs
{
    /// <summary>
    /// Arguments for revit_preview_retag / revit_retag. direction, addLeader, leaderEndCondition and
    /// orientation are sent only when given: an omitted leader or orientation keeps each tag's own,
    /// and an omitted direction lets a collision-free tag keep its head.
    /// </summary>
    public static Dictionary<string, object?> BuildRetagArgs(
        long viewId,
        long[]? tagIds,
        long[]? elementIds,
        string? direction,
        string anchorPoint,
        double attachedLengthMm,
        double freeLengthMm,
        bool? addLeader,
        string? leaderEndCondition,
        string? orientation,
        double rotationDegrees,
        bool detectElementRotation,
        bool enableCollisionDetection,
        double collisionGapMm,
        double minimumOffsetMm)
    {
        var args = new Dictionary<string, object?>
        {
            ["viewId"] = viewId,
            ["tagIds"] = tagIds ?? [],
            ["elementIds"] = elementIds ?? [],
            ["anchorPoint"] = anchorPoint,
            ["attachedLengthMm"] = attachedLengthMm,
            ["freeLengthMm"] = freeLengthMm,
            ["rotationDegrees"] = rotationDegrees,
            ["detectElementRotation"] = detectElementRotation,
            ["enableCollisionDetection"] = enableCollisionDetection,
            ["collisionGapMm"] = collisionGapMm,
            ["minimumOffsetMm"] = minimumOffsetMm
        };
        if (!string.IsNullOrWhiteSpace(direction))
            args["direction"] = direction;
        if (addLeader.HasValue)
            args["addLeader"] = addLeader.Value;
        if (!string.IsNullOrWhiteSpace(leaderEndCondition))
            args["leaderEndCondition"] = leaderEndCondition;
        if (!string.IsNullOrWhiteSpace(orientation))
            args["orientation"] = orientation;
        return args;
    }
}
