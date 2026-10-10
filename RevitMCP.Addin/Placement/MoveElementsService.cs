using Autodesk.Revit.DB;
using RevitMCP.Addin.Tools;

namespace RevitMCP.Addin.Placement;

/// <summary>
/// The Revit side of the bulk-move tools: reading where each element currently sits, and applying
/// the translation the maths worked out. Shared by the preview and the write tool so both resolve
/// the same elements, the same insertion points, and the same options.
/// </summary>
internal static class MoveElementsService
{
    /// <summary>
    /// Builds an ElementId from a wire value. Element ids have been 64-bit since Revit 2024, so
    /// the long overload is the only one used — passing an int would bind ambiguously across the
    /// 2024 and 2026 reference assemblies.
    /// </summary>
    public static ElementId ToElementId(long value) =>
        value > 0 ? new ElementId(value) : ElementId.InvalidElementId;

    /// <summary>Reads the options every move shares.</summary>
    public static MoveElementsOptions ParseOptions(Dictionary<string, object?> arguments, List<string> warnings)
    {
        var requested = ToolArguments.GetDouble(
            arguments, "positionToleranceMm", MoveElementsMath.DefaultPositionToleranceMm);
        var tolerance = MoveElementsMath.ClampTolerance(requested);
        if (Math.Abs(tolerance - requested) > 1e-9)
        {
            warnings.Add(
                $"positionToleranceMm {requested} is outside " +
                $"{MoveElementsMath.MinPositionToleranceMm}-{MoveElementsMath.MaxPositionToleranceMm}; " +
                $"using {tolerance}.");
        }

        return new MoveElementsOptions
        {
            Atomic = ToolArguments.GetBool(arguments, "atomic", true),
            SkipPinned = ToolArguments.GetBool(arguments, "skipPinned", true),
            PositionToleranceMm = tolerance,
            ViewId = ToolArguments.GetLong(arguments, "viewId")
        };
    }

    /// <summary>
    /// Works out what would happen to every requested element, touching nothing. Called by both
    /// tools, and by the write tool <em>before</em> it opens its transaction: every element is
    /// measured against the model as the caller last saw it, not against a model half-way through
    /// being rearranged.
    /// </summary>
    public static List<MovePlan> BuildPlans(
        Document doc,
        IReadOnlyList<MoveRequest> moves,
        MoveElementsOptions options,
        CancellationToken cancellationToken)
    {
        // The request-level view only supplies axes for deltaRightMm/deltaUpMm on elements that no
        // view owns. A view-specific element always uses its own view.
        var requestView = options.ViewId > 0 ? doc.GetElement(ToElementId(options.ViewId)) as View : null;

        var plans = new List<MovePlan>(moves.Count);
        foreach (var move in moves)
        {
            cancellationToken.ThrowIfCancellationRequested();
            plans.Add(BuildPlan(doc, move, options, requestView));
        }
        return plans;
    }

    private static MovePlan BuildPlan(Document doc, MoveRequest move, MoveElementsOptions options, View? requestView)
    {
        var element = doc.GetElement(ToElementId(move.ElementId));
        if (element == null)
            return MoveElementsMath.Missing(move.ElementId);

        if (element is ElementType)
        {
            return Describe(doc, element, null, MoveElementsMath.UnsupportedLocation(
                move.ElementId,
                "This is a family type, not a placed instance — types have no position in the model."));
        }

        var ownerView = OwnerView(doc, element);
        var frameView = ownerView ?? (move.HasViewDelta ? requestView : null);
        var frame = FrameOf(frameView);

        var plan = move.HasDelta
            ? BuildTranslationPlan(element, move, options, frame, ownerView, requestView)
            : BuildAbsolutePlan(element, move, options);

        // Checked after the maths so the response still shows where the element is and how far it
        // was asked to go. A stale, missing or unsupported entry keeps its own, more basic, reason.
        if (plan.CanMove)
            plan = ApplyConstraints(doc, element, plan, ownerView, FrameOf(ownerView));

        return Describe(doc, element, frameView, plan, frame);
    }

    private static MovePlan BuildAbsolutePlan(Element element, MoveRequest move, MoveElementsOptions options)
    {
        // LocationPoint only. A bounding-box centre would look like an answer and quietly put the
        // element somewhere else: the box covers the whole symbol including its leader, flip
        // handles and 3D body, and its centre is not the insertion point Revit measures from.
        if (element.Location is not LocationPoint locationPoint)
        {
            const string useDelta =
                " To shift it by a known distance instead, give deltaXmm/deltaYmm/deltaZmm (or deltaRightMm/deltaUpMm " +
                "for an element owned by a view) — a displacement needs no insertion point.";
            var reason = element.Location switch
            {
                LocationCurve => "This element is placed on a curve (a wall, pipe, duct, conduit, cable tray, a detail or " +
                                 "model line), so it has no single insertion point to put on a coordinate." + useDelta,
                null => "This element has no Location at all, so it cannot be moved to a coordinate." + useDelta,
                _ => $"This element's Location is a {element.Location.GetType().Name}, not a LocationPoint, " +
                     "so it has no insertion point to place on a coordinate." + useDelta
            };
            return MoveElementsMath.UnsupportedLocation(move.ElementId, reason);
        }

        var point = locationPoint.Point;
        var current = MoveElementsMath.PointFromFeet(point.X, point.Y, point.Z);

        return MoveElementsMath.Build(
            move, current, element.Pinned, options.SkipPinned, options.PositionToleranceMm);
    }

    private static MovePlan BuildTranslationPlan(
        Element element,
        MoveRequest move,
        MoveElementsOptions options,
        ViewFrame? frame,
        View? ownerView,
        View? requestView)
    {
        var translation = MoveElementsMath.TranslationFromDelta(move, frame, out var error);
        if (translation == null)
        {
            if (ownerView == null && options.ViewId > 0 && requestView == null)
                error = $"viewId {options.ViewId} is not a view in the active document, so deltaRightMm/deltaUpMm have no axes to follow.";
            else if (frame == null && (ownerView ?? requestView) != null)
                error = $"View '{(ownerView ?? requestView)!.Name}' has no right/up directions (it is not a graphical view), " +
                        "so deltaRightMm/deltaUpMm cannot be used with it. Use deltaXmm/deltaYmm/deltaZmm.";

            return new MovePlan
            {
                ElementId = move.ElementId,
                Mode = MoveMode.Translation,
                CanMove = false,
                IsFailure = true,
                Status = MoveStatus.Failed,
                Reason = error
            };
        }

        PointMm? current = null;
        if (element.Location is LocationPoint locationPoint)
        {
            var point = locationPoint.Point;
            current = MoveElementsMath.PointFromFeet(point.X, point.Y, point.Z);
        }

        return MoveElementsMath.BuildTranslation(
            move, current, translation.Value, element.Pinned, options.SkipPinned, options.PositionToleranceMm);
    }

    /// <summary>
    /// The two things Revit refuses that are known before the transaction: a group member cannot be
    /// moved outside group edit mode, and an element owned by a view lives on that view's plane.
    /// </summary>
    private static MovePlan ApplyConstraints(Document doc, Element element, MovePlan plan, View? ownerView, ViewFrame? ownerFrame)
    {
        if (element.GroupId != ElementId.InvalidElementId)
        {
            var groupName = string.Empty;
            try { groupName = doc.GetElement(element.GroupId)?.Name ?? string.Empty; }
            catch { /* the name is a courtesy */ }

            var blocked = MoveElementsMath.Blocked(plan, MoveStatus.InGroup,
                $"This element is a member of group '{groupName}' (id {element.GroupId.Value}). Revit only moves group " +
                "members in group edit mode — move the group instance itself, or ungroup first.");
            blocked.GroupId = element.GroupId.Value;
            return blocked;
        }

        if (ownerView != null && ownerFrame != null && plan.TranslationMm.HasValue)
        {
            var offPlane = MoveElementsMath.OutOfPlaneMm(plan.TranslationMm.Value, ownerFrame.Value);
            if (offPlane > MoveElementsMath.OutOfPlaneToleranceMm)
            {
                var normal = ownerFrame.Value.Normal;
                return MoveElementsMath.Blocked(plan, MoveStatus.OutOfViewPlane,
                    $"This element belongs to view '{ownerView.Name}' and can only move within that view's plane. " +
                    $"The requested move leaves the plane by {MoveElementsMath.Round(offPlane)} mm " +
                    $"(view normal {normal.X:0.###}, {normal.Y:0.###}, {normal.Z:0.###}). " +
                    "Use deltaRightMm/deltaUpMm to move along the view's own axes.");
            }
        }

        return plan;
    }

    /// <summary>Applies one plan. The translation is the only thing converted back to internal units.</summary>
    public static void Move(Document doc, Element element, MovePlan plan)
    {
        var translationMm = plan.TranslationMm!.Value;
        var translation = new XYZ(
            MoveElementsMath.MmToFt(translationMm.X),
            MoveElementsMath.MmToFt(translationMm.Y),
            MoveElementsMath.MmToFt(translationMm.Z));

        ElementTransformUtils.MoveElement(doc, element.Id, translation);
    }

    /// <summary>
    /// A point that travels with the element, for measuring how far it really went: the insertion
    /// point when there is one, otherwise the bounding-box minimum (in the owner view for a
    /// view-specific element). Only ever compared with itself, before and after — never moved to.
    /// </summary>
    public static PointMm? TrackingPoint(Document doc, Element element)
    {
        try
        {
            if (element.Location is LocationPoint locationPoint)
            {
                var point = locationPoint.Point;
                return MoveElementsMath.PointFromFeet(point.X, point.Y, point.Z);
            }

            var box = element.get_BoundingBox(OwnerView(doc, element));
            return box == null ? null : MoveElementsMath.PointFromFeet(box.Min.X, box.Min.Y, box.Min.Z);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads back where a moved element ended up. Returns a warning when Revit moved it somewhere
    /// other than requested (a host, a constraint or a work plane pulled it), null otherwise.
    /// </summary>
    public static string? RecordResult(Document doc, MovePlan plan, PointMm? before)
    {
        var element = doc.GetElement(ToElementId(plan.ElementId));
        if (element == null)
            return null;

        if (element.Location is LocationPoint locationPoint)
        {
            var point = locationPoint.Point;
            plan.ResultPointMm = MoveElementsMath.PointFromFeet(point.X, point.Y, point.Z);
        }

        var after = TrackingPoint(doc, element);
        if (!before.HasValue || !after.HasValue || !plan.TranslationMm.HasValue)
            return null;

        var actual = after.Value.Minus(before.Value);
        plan.ActualTranslationMm = actual;

        var difference = actual.Minus(plan.TranslationMm.Value).Length;
        return difference > MoveElementsMath.NegligibleMoveMm
            ? $"Element {plan.ElementId} travelled {actual} mm instead of the requested {plan.TranslationMm.Value} mm — " +
              "a host, constraint or work plane held it. Check the result."
            : null;
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

    /// <summary>The view's axes in model coordinates, or null for views without them (schedules, sheets lists).</summary>
    public static ViewFrame? FrameOf(View? view)
    {
        if (view == null)
            return null;
        try
        {
            var right = view.RightDirection;
            var up = view.UpDirection;
            var normal = view.ViewDirection;
            if (right == null || up == null || normal == null || right.IsZeroLength() || up.IsZeroLength())
                return null;
            return new ViewFrame(
                new PointMm(right.X, right.Y, right.Z),
                new PointMm(up.X, up.Y, up.Z),
                new PointMm(normal.X, normal.Y, normal.Z));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Names the element for the response. Some elements throw on Name; none of that is worth failing a move over.</summary>
    private static MovePlan Describe(Document doc, Element element, View? frameView, MovePlan plan, ViewFrame? frame = null)
    {
        try { plan.ElementName = element.Name ?? string.Empty; }
        catch { plan.ElementName = string.Empty; }

        try { plan.CategoryName = element.Category?.Name ?? string.Empty; }
        catch { plan.CategoryName = string.Empty; }

        plan.LocationKind = element.Location switch
        {
            LocationPoint => "Point",
            LocationCurve => "Curve",
            null => "None",
            _ => "Other"
        };

        var ownerView = OwnerView(doc, element);
        if (ownerView != null)
        {
            plan.OwnerViewId = ownerView.Id.Value;
            plan.OwnerViewName = ownerView.Name;
        }

        // Shown whenever a view's axes mattered: the owner view's for a view-specific element, or
        // the request's viewId when deltaRightMm/deltaUpMm were resolved against it.
        if (frame != null && frameView != null)
        {
            plan.ViewFrame = frame;
            plan.FrameViewId = frameView.Id.Value;
            plan.FrameViewName = frameView.Name;
        }

        if (plan.GroupId == null && element.GroupId != ElementId.InvalidElementId)
            plan.GroupId = element.GroupId.Value;

        return plan;
    }
}

internal sealed class MoveElementsOptions
{
    /// <summary>All or nothing: any failure undoes the whole batch.</summary>
    public bool Atomic { get; init; } = true;

    public bool SkipPinned { get; init; } = true;

    public double PositionToleranceMm { get; init; } = MoveElementsMath.DefaultPositionToleranceMm;

    /// <summary>The view whose axes deltaRightMm/deltaUpMm follow for elements no view owns. 0 = none.</summary>
    public long ViewId { get; init; }
}
