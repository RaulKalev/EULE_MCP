using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Placement;

/// <summary>One element measured in the view's own left/right and up/down axes.</summary>
internal sealed class ViewAlignmentTarget
{
    public long ElementId { get; set; }
    public string ElementName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;

    /// <summary>True for annotation that lives in this view only; false for model geometry.</summary>
    public bool IsViewSpecific { get; set; }

    public bool Pinned { get; set; }

    /// <summary>Extent along the view's right direction, in feet.</summary>
    public Extent Horizontal { get; set; }

    /// <summary>Extent along the view's up direction, in feet.</summary>
    public Extent Vertical { get; set; }

    /// <summary><c>boundingBox</c> or <c>origin</c> — what was actually measured.</summary>
    public string AnchorUsed { get; set; } = ViewAlignmentRequest.AnchorBoundingBox;

    /// <summary>Why the anchor differs from the one requested, if it does.</summary>
    public string? AnchorNote { get; set; }

    public string? BlockedReason { get; set; }

    public bool CanAlign => BlockedReason == null;

    public Extent ExtentFor(string mode) =>
        ViewAlignmentMath.UsesHorizontalAxis(mode) ? Horizontal : Vertical;
}

/// <summary>
/// Measures elements in the plane of a view: it projects each one onto the view's right and up
/// directions so the alignment maths can work in plain 1D numbers, and picks the anchor — the
/// bounding box, or a single point — that makes sense for the element.
/// </summary>
internal sealed class ViewAlignmentService
{
    private readonly View _view;
    private readonly ViewAlignmentOptions _options;
    private readonly XYZ _right;
    private readonly XYZ _up;

    public ViewAlignmentService(View view, ViewAlignmentOptions options)
    {
        _view = view;
        _options = options;
        _right = view.RightDirection.Normalize();
        _up = view.UpDirection.Normalize();
    }

    /// <summary>The direction an offset for <paramref name="mode"/> is applied along.</summary>
    public XYZ AxisFor(string mode) => ViewAlignmentMath.UsesHorizontalAxis(mode) ? _right : _up;

    public ViewAlignmentTarget Measure(Element element)
    {
        var target = new ViewAlignmentTarget
        {
            ElementId = element.Id.Value,
            ElementName = ElementName(element),
            Category = element.Category?.Name ?? "(no category)",
            IsViewSpecific = element.ViewSpecific,
            Pinned = SafePinned(element)
        };

        var wantsOrigin = _options.Anchor == ViewAlignmentRequest.AnchorOrigin;
        if (_options.Anchor == ViewAlignmentRequest.AnchorAuto && PrefersOrigin(element, out var reason))
        {
            wantsOrigin = true;
            target.AnchorNote =
                $"Measured from its anchor point, not its bounding box: the {reason}, which the box includes.";
        }

        if (!wantsOrigin && TryMeasureBox(element, target))
            return target;

        if (!wantsOrigin)
        {
            // No bounding box in this view means the element is not visible here, or has no
            // graphics at all. Its anchor point is still a usable answer, so say what happened
            // rather than dropping the element.
            target.AnchorNote = $"No graphics in '{_view.Name}'; measured from its anchor point instead.";
        }

        var origin = OriginPoint(element);
        if (origin == null)
        {
            target.BlockedReason =
                $"Could not locate this element in '{_view.Name}' — it has neither graphics in the " +
                "view nor a position to measure.";
            return target;
        }

        target.AnchorUsed = ViewAlignmentRequest.AnchorOrigin;
        target.Horizontal = Extent.Point(origin.DotProduct(_right));
        target.Vertical = Extent.Point(origin.DotProduct(_up));
        return target;
    }

    private bool TryMeasureBox(Element element, ViewAlignmentTarget target)
    {
        BoundingBoxXYZ? box;
        try { box = element.get_BoundingBox(_view); }
        catch { box = null; }

        if (box == null)
            return false;

        var min = box.Min;
        var max = box.Max;
        if (min == null || max == null)
            return false;

        var corners = new[]
        {
            new XYZ(min.X, min.Y, min.Z),
            new XYZ(min.X, min.Y, max.Z),
            new XYZ(min.X, max.Y, min.Z),
            new XYZ(min.X, max.Y, max.Z),
            new XYZ(max.X, min.Y, min.Z),
            new XYZ(max.X, min.Y, max.Z),
            new XYZ(max.X, max.Y, min.Z),
            new XYZ(max.X, max.Y, max.Z)
        };

        var minX = double.MaxValue;
        var maxX = double.MinValue;
        var minY = double.MaxValue;
        var maxY = double.MinValue;
        foreach (var corner in corners)
        {
            // The box is axis-aligned to the model, not to the view, so every corner has to be
            // projected — in a rotated crop or a section, the model axes are not the view's.
            var transformed = box.Transform.OfPoint(corner);
            var x = transformed.DotProduct(_right);
            var y = transformed.DotProduct(_up);
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
        }

        target.AnchorUsed = ViewAlignmentRequest.AnchorBoundingBox;
        target.Horizontal = new Extent(minX, maxX);
        target.Vertical = new Extent(minY, maxY);
        return true;
    }

    /// <summary>
    /// A leader is part of an annotation's bounding box, so a tag two metres from its host reads as
    /// two metres wide and "align left" would line up leader tails rather than tag heads. For those,
    /// the head or insertion point is the thing the user means.
    /// </summary>
    private static bool PrefersOrigin(Element element, out string reason)
    {
        reason = string.Empty;
        switch (element)
        {
            case IndependentTag tag:
                try
                {
                    if (tag.HasLeader)
                    {
                        reason = "tag has a leader";
                        return true;
                    }
                }
                catch { }
                return false;

            case TextNote note:
                try
                {
                    if (note.LeaderCount > 0)
                    {
                        reason = "text note has a leader";
                        return true;
                    }
                }
                catch { }
                return false;

            default:
                return false;
        }
    }

    private XYZ? OriginPoint(Element element)
    {
        try
        {
            switch (element)
            {
                case IndependentTag tag:
                    return tag.TagHeadPosition;
                case TextNote note:
                    return note.Coord;
                case Viewport viewport:
                    return viewport.GetBoxCenter();
            }
        }
        catch { }

        try
        {
            switch (element.Location)
            {
                case LocationPoint point:
                    return point.Point;
                case LocationCurve curve when curve.Curve != null:
                    return curve.Curve.Evaluate(0.5, true);
            }
        }
        catch { }

        try
        {
            var box = element.get_BoundingBox(_view) ?? element.get_BoundingBox(null);
            if (box?.Min != null && box.Max != null)
                return box.Transform.OfPoint((box.Min + box.Max) * 0.5);
        }
        catch { }

        return null;
    }

    private static bool SafePinned(Element element)
    {
        try { return element.Pinned; }
        catch { return false; }
    }

    private static string ElementName(Element element)
    {
        try
        {
            var name = element.Name;
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }
        catch { }
        return element.Category?.Name ?? "Element";
    }
}
