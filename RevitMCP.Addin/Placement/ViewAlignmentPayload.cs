namespace RevitMCP.Addin.Placement;

/// <summary>
/// Shapes an in-view alignment plan into the response payload. Shared so the preview reports
/// exactly what the write tool will do.
/// </summary>
internal static class ViewAlignmentPayload
{
    public static string Axis(string mode) =>
        ViewAlignmentMath.UsesHorizontalAxis(mode) ? "horizontal" : "vertical";

    /// <summary>Which way an element slides, named the way it reads on screen.</summary>
    public static string Direction(string mode, double offsetMm)
    {
        if (Math.Abs(offsetMm) < ViewAlignmentRequest.NegligibleMoveMm)
            return "none";
        return ViewAlignmentMath.UsesHorizontalAxis(mode)
            ? offsetMm > 0 ? "right" : "left"
            : offsetMm > 0 ? "up" : "down";
    }

    public static object Describe(
        ViewAlignmentMove move,
        ViewAlignmentOptions options,
        bool willMove,
        string? outcome = null)
    {
        var target = move.Target;
        var offsetMm = move.OffsetMm;

        return new
        {
            elementId = target.ElementId,
            elementName = target.ElementName,
            category = target.Category,
            viewSpecific = target.IsViewSpecific,
            anchor = target.AnchorUsed,
            anchorNote = target.AnchorNote,
            canAlign = target.CanAlign,
            moveMm = target.CanAlign ? Math.Round(offsetMm, 2) : (double?)null,
            direction = target.CanAlign ? Direction(options.Mode, offsetMm) : null,
            willMove = willMove && target.CanAlign && !move.IsNegligible,
            outcome,
            reason = target.BlockedReason ?? Reason(move, options)
        };
    }

    private static string Reason(ViewAlignmentMove move, ViewAlignmentOptions options)
    {
        if (move.IsNegligible)
            return options.IsDistribute ? "Already evenly spaced." : $"Already aligned {options.Mode}.";

        var offsetMm = move.OffsetMm;
        return $"Slide {Math.Abs(offsetMm):F1} mm {Direction(options.Mode, offsetMm)}.";
    }

    /// <summary>A one-line description of what the whole call does, for messages and approval text.</summary>
    public static string Summarise(ViewAlignmentOptions options, int elementCount)
    {
        var noun = elementCount == 1 ? "element" : "elements";

        if (options.IsDistribute)
        {
            var axis = options.Mode == ViewAlignmentMath.ModeDistributeHorizontal ? "horizontally" : "vertically";
            var by = options.Spread == ViewAlignmentMath.SpreadGaps
                ? "equal gaps between them"
                : "equal centre-to-centre spacing";
            var at = options.SpacingFt.HasValue
                ? $" at {ViewAlignmentRequest.FtToMm(options.SpacingFt.Value):F0} mm"
                : string.Empty;
            return $"Spread {elementCount} {noun} {axis} with {by}{at}";
        }

        var edge = options.Mode switch
        {
            ViewAlignmentMath.ModeCenterVertical => "their centres onto a common vertical line",
            ViewAlignmentMath.ModeCenterHorizontal => "their centres onto a common horizontal line",
            _ => $"their {options.Mode} edges"
        };

        var reference = options.Reference switch
        {
            ViewAlignmentMath.ReferenceElement => "a nominated element",
            ViewAlignmentMath.ReferenceMin => "the lowest of them",
            ViewAlignmentMath.ReferenceMax => "the highest of them",
            ViewAlignmentMath.ReferenceAverage => "their average",
            _ => options.Mode is ViewAlignmentMath.ModeCenterVertical or ViewAlignmentMath.ModeCenterHorizontal
                ? "their average"
                : "the outermost element"
        };

        return $"Align {elementCount} {noun} by {edge}, to {reference}";
    }
}
