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
            PositionToleranceMm = tolerance
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
        var plans = new List<MovePlan>(moves.Count);
        foreach (var move in moves)
        {
            cancellationToken.ThrowIfCancellationRequested();
            plans.Add(BuildPlan(doc, move, options));
        }
        return plans;
    }

    private static MovePlan BuildPlan(Document doc, MoveRequest move, MoveElementsOptions options)
    {
        var element = doc.GetElement(ToElementId(move.ElementId));
        if (element == null)
            return MoveElementsMath.Missing(move.ElementId);

        if (element is ElementType)
        {
            return Describe(element, MoveElementsMath.UnsupportedLocation(
                move.ElementId,
                "This is a family type, not a placed instance — types have no position in the model."));
        }

        // LocationPoint only. A bounding-box centre would look like an answer and quietly put the
        // element somewhere else: the box covers the whole symbol including its leader, flip
        // handles and 3D body, and its centre is not the insertion point Revit measures from.
        if (element.Location is not LocationPoint locationPoint)
        {
            var reason = element.Location switch
            {
                LocationCurve => "This element is placed on a curve (a wall, pipe, duct, conduit or cable tray). " +
                                 "It has no single insertion point — move it by editing its endpoints instead.",
                null => "This element has no Location at all, so it cannot be moved to a coordinate.",
                _ => $"This element's Location is a {element.Location.GetType().Name}, not a LocationPoint, " +
                     "so it has no insertion point to place on a coordinate."
            };
            return Describe(element, MoveElementsMath.UnsupportedLocation(move.ElementId, reason));
        }

        var point = locationPoint.Point;
        var current = MoveElementsMath.PointFromFeet(point.X, point.Y, point.Z);

        return Describe(element, MoveElementsMath.Build(
            move, current, element.Pinned, options.SkipPinned, options.PositionToleranceMm));
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

    /// <summary>Names the element for the response. Some elements throw on Name; none of that is worth failing a move over.</summary>
    private static MovePlan Describe(Element element, MovePlan plan)
    {
        try { plan.ElementName = element.Name ?? string.Empty; }
        catch { plan.ElementName = string.Empty; }

        try { plan.CategoryName = element.Category?.Name ?? string.Empty; }
        catch { plan.CategoryName = string.Empty; }

        return plan;
    }
}

internal sealed class MoveElementsOptions
{
    /// <summary>All or nothing: any failure undoes the whole batch.</summary>
    public bool Atomic { get; init; } = true;

    public bool SkipPinned { get; init; } = true;

    public double PositionToleranceMm { get; init; } = MoveElementsMath.DefaultPositionToleranceMm;
}
