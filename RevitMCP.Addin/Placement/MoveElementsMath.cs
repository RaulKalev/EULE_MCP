using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Placement;

/// <summary>
/// A model coordinate in millimetres. The move tools speak millimetres at their edges — that is
/// what the CAD and query tools return — and convert to Revit's internal feet only at the point
/// the translation vector is built.
/// </summary>
public readonly struct PointMm
{
    public PointMm(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public double X { get; }
    public double Y { get; }
    public double Z { get; }

    public PointMm Minus(PointMm other) => new(X - other.X, Y - other.Y, Z - other.Z);

    public PointMm Plus(PointMm other) => new(X + other.X, Y + other.Y, Z + other.Z);

    public PointMm Times(double factor) => new(X * factor, Y * factor, Z * factor);

    public double Dot(PointMm other) => X * other.X + Y * other.Y + Z * other.Z;

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

    public override string ToString() => $"({X:F2}, {Y:F2}, {Z:F2})";
}

/// <summary>
/// The axes of a view expressed in model coordinates, as unit vectors: <see cref="Right"/> and
/// <see cref="Up"/> span the view plane (what is horizontal and vertical on screen and on paper),
/// <see cref="Normal"/> points out of the view towards the viewer. In a floor plan Right/Up are
/// model X/Y; in a section or elevation Up is model Z and Right runs along the cut.
/// </summary>
public readonly struct ViewFrame
{
    public ViewFrame(PointMm right, PointMm up, PointMm normal)
    {
        Right = right;
        Up = up;
        Normal = normal;
    }

    public PointMm Right { get; }
    public PointMm Up { get; }
    public PointMm Normal { get; }
}

/// <summary>
/// One requested move, exactly as it arrived. Every coordinate is optional and stays nullable all
/// the way through: an omitted <see cref="TargetZMm"/> means "leave the elevation alone", which is
/// not the same request as "move to Z = 0".
/// </summary>
public sealed class MoveRequest
{
    /// <summary>Kept as a long from the wire to the ElementId. Revit 2026 issues ids above int range.</summary>
    public long ElementId { get; init; }

    public double? TargetXMm { get; init; }
    public double? TargetYMm { get; init; }
    public double? TargetZMm { get; init; }

    public double? ExpectedXMm { get; init; }
    public double? ExpectedYMm { get; init; }
    public double? ExpectedZMm { get; init; }

    /// <summary>Translation along the model axes. An omitted axis is zero — a displacement has no "keep".</summary>
    public double? DeltaXMm { get; init; }
    public double? DeltaYMm { get; init; }
    public double? DeltaZMm { get; init; }

    /// <summary>Translation along the view's own axes: right and up as seen in the view.</summary>
    public double? DeltaRightMm { get; init; }
    public double? DeltaUpMm { get; init; }

    public bool HasTarget => TargetXMm.HasValue || TargetYMm.HasValue || TargetZMm.HasValue;

    public bool HasModelDelta => DeltaXMm.HasValue || DeltaYMm.HasValue || DeltaZMm.HasValue;

    public bool HasViewDelta => DeltaRightMm.HasValue || DeltaUpMm.HasValue;

    /// <summary>True when the entry is a displacement rather than an absolute target.</summary>
    public bool HasDelta => HasModelDelta || HasViewDelta;

    public bool HasExpected => ExpectedXMm.HasValue || ExpectedYMm.HasValue || ExpectedZMm.HasValue;
}

/// <summary>
/// The per-element outcome names shared by the preview and the write tool. The preview only ever
/// reports the first six; the rest are set while the transaction runs.
/// </summary>
public static class MoveStatus
{
    /// <summary>The element can be moved and is not already there.</summary>
    public const string Ready = "Ready";

    /// <summary>Already within <see cref="MoveElementsMath.NegligibleMoveMm"/> of the target.</summary>
    public const string AlreadyThere = "AlreadyThere";

    /// <summary>The current location disagrees with the supplied expected coordinates.</summary>
    public const string Stale = "Stale";

    public const string Missing = "Missing";
    public const string Pinned = "Pinned";

    /// <summary>The element has no LocationPoint — there is no insertion point to move to a coordinate.</summary>
    public const string UnsupportedLocation = "UnsupportedLocation";

    /// <summary>The element is a member of a group; Revit only moves group members inside group edit mode.</summary>
    public const string InGroup = "InGroup";

    /// <summary>
    /// The element belongs to a view and the requested move would take it off that view's plane.
    /// </summary>
    public const string OutOfViewPlane = "OutOfViewPlane";

    public const string Moved = "Moved";
    public const string Failed = "Failed";

    /// <summary>Moved during the transaction, then undone because atomic=true and something failed.</summary>
    public const string RolledBack = "RolledBack";

    /// <summary>Movable, but never attempted — atomic=true and the batch was rejected up front.</summary>
    public const string NotAttempted = "NotAttempted";
}

/// <summary>How a move entry says where the element goes.</summary>
public static class MoveMode
{
    /// <summary>targetXmm/Ymm/Zmm: put the insertion point on a coordinate. Needs a LocationPoint.</summary>
    public const string Absolute = "Absolute";

    /// <summary>deltaXmm/Ymm/Zmm or deltaRightMm/deltaUpMm: shift by a displacement. Works without a LocationPoint.</summary>
    public const string Translation = "Translation";
}

/// <summary>
/// What one element would do, or did. Everything except <see cref="Status"/> is decided before the
/// transaction opens, so the preview and the write tool report identical numbers for the same model.
/// </summary>
public sealed class MovePlan
{
    public long ElementId { get; init; }
    public string ElementName { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;

    /// <summary><see cref="MoveMode.Absolute"/> or <see cref="MoveMode.Translation"/>.</summary>
    public string Mode { get; init; } = MoveMode.Absolute;

    /// <summary>Point, Curve, Other or None — what kind of Location the element has.</summary>
    public string LocationKind { get; set; } = string.Empty;

    /// <summary>The view that owns the element, when it is view-specific (detail items, detail lines, text).</summary>
    public long? OwnerViewId { get; set; }
    public string? OwnerViewName { get; set; }

    /// <summary>The view axes used for the plane check and for deltaRightMm/deltaUpMm, when a view applies.</summary>
    public ViewFrame? ViewFrame { get; set; }
    public long? FrameViewId { get; set; }
    public string? FrameViewName { get; set; }

    /// <summary>The group the element belongs to, when it is a group member.</summary>
    public long? GroupId { get; set; }

    /// <summary>Insertion point after the move, read back from the model. Point-located elements only.</summary>
    public PointMm? ResultPointMm { get; set; }

    /// <summary>
    /// How far the element actually travelled, measured after the move: from the insertion point when
    /// there is one, otherwise from its bounding box. Differs from the request when Revit constrained it.
    /// </summary>
    public PointMm? ActualTranslationMm { get; set; }

    /// <summary>Null when the element is missing or has no LocationPoint.</summary>
    public PointMm? CurrentPointMm { get; init; }

    public PointMm? TargetPointMm { get; init; }
    public PointMm? TranslationMm { get; init; }
    public double DistanceMm { get; init; }

    public bool Pinned { get; init; }

    /// <summary>True only for a plan the write tool will hand to ElementTransformUtils.</summary>
    public bool CanMove { get; init; }

    /// <summary>
    /// True when this outcome is a failure rather than a benign skip. Only these trigger the
    /// rollback under atomic=true — an element already at its target, or a pinned one skipped
    /// because skipPinned=true, is not a failure.
    /// </summary>
    public bool IsFailure { get; init; }

    /// <summary>The largest per-axis disagreement with the expected coordinates, when they were given.</summary>
    public double? StaleDeviationMm { get; init; }

    public string? Reason { get; set; }

    public string Status { get; set; } = MoveStatus.Ready;
}

/// <summary>Element ids grouped by outcome. Every element lands in exactly one list.</summary>
public sealed class MoveSummary
{
    public List<long> Moved { get; } = new();

    /// <summary>Nothing to do — already within the negligible distance of the target.</summary>
    public List<long> Skipped { get; } = new();

    public List<long> Stale { get; } = new();
    public List<long> Missing { get; } = new();
    public List<long> Pinned { get; } = new();
    public List<long> Unsupported { get; } = new();

    /// <summary>Group members and moves that would leave the owner view's plane.</summary>
    public List<long> Constrained { get; } = new();

    public List<long> Failed { get; } = new();
    public List<long> RolledBack { get; } = new();
    public List<long> NotAttempted { get; } = new();

    /// <summary>Everything that did not end up where it was asked to go.</summary>
    public int ProblemCount =>
        Stale.Count + Missing.Count + Pinned.Count + Unsupported.Count + Constrained.Count +
        Failed.Count + RolledBack.Count + NotAttempted.Count;
}

/// <summary>
/// The maths and bookkeeping behind "put these elements on these exact coordinates": unit
/// conversion, where each element has to travel, whether the caller's picture of the model is
/// still current, and which outcomes count as a failure.
///
/// Deliberately free of the Revit API. The tools read <c>LocationPoint.Point</c>, hand the
/// coordinates here as plain numbers, and turn the resulting translation back into an XYZ.
/// </summary>
public static class MoveElementsMath
{
    /// <summary>
    /// Revit's internal length unit is the decimal foot, and the international foot is defined as
    /// exactly 304.8 mm. This is the same number
    /// <c>UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters)</c> applies, kept here as a
    /// constant so the conversion can be tested without Revit.
    /// </summary>
    public const double MmPerFoot = 304.8;

    /// <summary>Default agreement required between the supplied expected point and the model.</summary>
    public const double DefaultPositionToleranceMm = 1.0;

    public const double MinPositionToleranceMm = 0.001;
    public const double MaxPositionToleranceMm = 10000.0;

    /// <summary>
    /// Below this the translation is numerical noise, not a move. Fixed rather than derived from
    /// positionToleranceMm: a loose staleness tolerance must not start silently swallowing real moves.
    /// </summary>
    public const double NegligibleMoveMm = 0.1;

    /// <summary>
    /// Upper bound on one request. Well past the 500 the DWG-alignment workflow needs, and low
    /// enough that a runaway caller cannot pin Revit's UI thread for minutes.
    /// </summary>
    public const int MaxMoves = 2000;

    public static double MmToFt(double mm) => mm / MmPerFoot;

    public static double FtToMm(double ft) => ft * MmPerFoot;

    public static PointMm PointFromFeet(double xFt, double yFt, double zFt) =>
        new(FtToMm(xFt), FtToMm(yFt), FtToMm(zFt));

    /// <summary>Rounds a millimetre value for reporting. Never used for anything that gets moved.</summary>
    public static double Round(double valueMm) => Math.Round(valueMm, 2);

    public static double ClampTolerance(double requestedMm)
    {
        if (double.IsNaN(requestedMm) || requestedMm <= 0)
            return DefaultPositionToleranceMm;
        if (requestedMm < MinPositionToleranceMm) return MinPositionToleranceMm;
        if (requestedMm > MaxPositionToleranceMm) return MaxPositionToleranceMm;
        return requestedMm;
    }

    /// <summary>
    /// The point the element ends up at. An omitted axis keeps its current value — that is how a
    /// fixture keeps the elevation its family and level gave it while its plan position is corrected.
    /// </summary>
    public static PointMm ResolveTarget(PointMm current, MoveRequest request) => new(
        request.TargetXMm ?? current.X,
        request.TargetYMm ?? current.Y,
        request.TargetZMm ?? current.Z);

    public static PointMm Translation(PointMm current, PointMm target) => target.Minus(current);

    /// <summary>
    /// How far the model disagrees with the caller's expected point, measured per axis and reported
    /// as the worst axis. Null when no expected coordinates were supplied — the check is opt-in.
    /// Axes the caller did not pin down are not checked.
    /// </summary>
    public static double? ExpectedDeviationMm(PointMm current, MoveRequest request)
    {
        if (!request.HasExpected)
            return null;

        var worst = 0.0;
        if (request.ExpectedXMm.HasValue) worst = Math.Max(worst, Math.Abs(current.X - request.ExpectedXMm.Value));
        if (request.ExpectedYMm.HasValue) worst = Math.Max(worst, Math.Abs(current.Y - request.ExpectedYMm.Value));
        if (request.ExpectedZMm.HasValue) worst = Math.Max(worst, Math.Abs(current.Z - request.ExpectedZMm.Value));
        return worst;
    }

    /// <summary>
    /// Works out what happens to one element that exists and has an insertion point. The staleness
    /// check comes first: if the model has moved on since the caller measured it, the target it
    /// calculated is meaningless and nothing else about the request can be trusted either.
    /// </summary>
    public static MovePlan Build(
        MoveRequest request,
        PointMm current,
        bool pinned,
        bool skipPinned,
        double positionToleranceMm)
    {
        var target = ResolveTarget(current, request);
        var translation = Translation(current, target);
        var distance = translation.Length;
        var deviation = ExpectedDeviationMm(current, request);

        MovePlan Plan(string status, bool canMove, bool isFailure, string? reason) => new()
        {
            ElementId = request.ElementId,
            CurrentPointMm = current,
            TargetPointMm = target,
            TranslationMm = translation,
            DistanceMm = distance,
            Pinned = pinned,
            StaleDeviationMm = deviation,
            CanMove = canMove,
            IsFailure = isFailure,
            Status = status,
            Reason = reason
        };

        if (deviation.HasValue && deviation.Value > positionToleranceMm)
        {
            return Plan(MoveStatus.Stale, false, true,
                $"The element is {Round(deviation.Value)} mm from the expected point, which is more than the " +
                $"{Round(positionToleranceMm)} mm tolerance. Something moved it since the targets were worked out — " +
                "re-read the positions and recalculate.");
        }

        if (pinned)
        {
            return skipPinned
                ? Plan(MoveStatus.Pinned, false, false, "Pinned; skipped because skipPinned=true.")
                : Plan(MoveStatus.Pinned, false, true,
                    "Pinned, and skipPinned=false. Unpin it in Revit and run again — this tool never unpins elements.");
        }

        if (distance < NegligibleMoveMm)
        {
            return Plan(MoveStatus.AlreadyThere, false, false,
                request.HasTarget
                    ? null
                    : "No target coordinate was given, so there is nothing to move to.");
        }

        return Plan(MoveStatus.Ready, true, false, null);
    }

    /// <summary>
    /// A move off the view plane smaller than this is rounding, not intent. Revit's own tolerance for
    /// "in the plane" is far looser than a hundredth of a millimetre.
    /// </summary>
    public const double OutOfPlaneToleranceMm = 0.01;

    /// <summary>
    /// The displacement a delta entry asks for, in model coordinates. deltaXmm/Ymm/Zmm are model axes;
    /// deltaRightMm/deltaUpMm are the view's axes and need <paramref name="frame"/>. Returns null with
    /// <paramref name="error"/> set when view axes were asked for and no view defines them.
    /// </summary>
    public static PointMm? TranslationFromDelta(MoveRequest request, ViewFrame? frame, out string? error)
    {
        error = null;
        var translation = new PointMm(request.DeltaXMm ?? 0, request.DeltaYMm ?? 0, request.DeltaZMm ?? 0);
        if (!request.HasViewDelta)
            return translation;

        if (frame == null)
        {
            error = "deltaRightMm/deltaUpMm are measured along a view's axes, and this element is not owned by a view. " +
                    "Pass viewId to name the view whose right/up directions to use, or use deltaXmm/deltaYmm/deltaZmm.";
            return null;
        }

        return translation
            .Plus(frame.Value.Right.Times(request.DeltaRightMm ?? 0))
            .Plus(frame.Value.Up.Times(request.DeltaUpMm ?? 0));
    }

    /// <summary>How far a translation leaves the view plane: its component along the view normal, in mm.</summary>
    public static double OutOfPlaneMm(PointMm translation, ViewFrame frame) => Math.Abs(translation.Dot(frame.Normal));

    /// <summary>
    /// Works out what happens to one element moved by a displacement. Unlike <see cref="Build"/> this
    /// does not need an insertion point: <paramref name="current"/> is null for curves, text and other
    /// elements without a LocationPoint, and then only the displacement is reported.
    /// </summary>
    public static MovePlan BuildTranslation(
        MoveRequest request,
        PointMm? current,
        PointMm translation,
        bool pinned,
        bool skipPinned,
        double positionToleranceMm)
    {
        var distance = translation.Length;
        var deviation = current.HasValue ? ExpectedDeviationMm(current.Value, request) : null;

        MovePlan Plan(string status, bool canMove, bool isFailure, string? reason) => new()
        {
            ElementId = request.ElementId,
            Mode = MoveMode.Translation,
            CurrentPointMm = current,
            TargetPointMm = current?.Plus(translation),
            TranslationMm = translation,
            DistanceMm = distance,
            Pinned = pinned,
            StaleDeviationMm = deviation,
            CanMove = canMove,
            IsFailure = isFailure,
            Status = status,
            Reason = reason
        };

        if (request.HasExpected && !current.HasValue)
        {
            return Plan(MoveStatus.UnsupportedLocation, false, true,
                "expectedXmm/Ymm/Zmm compare against the insertion point, and this element has no LocationPoint. " +
                "Leave the expected coordinates out to move it by the displacement alone.");
        }

        if (deviation.HasValue && deviation.Value > positionToleranceMm)
        {
            return Plan(MoveStatus.Stale, false, true,
                $"The element is {Round(deviation.Value)} mm from the expected point, which is more than the " +
                $"{Round(positionToleranceMm)} mm tolerance. Something moved it since the displacement was worked out — " +
                "re-read the positions and recalculate.");
        }

        if (pinned)
        {
            return skipPinned
                ? Plan(MoveStatus.Pinned, false, false, "Pinned; skipped because skipPinned=true.")
                : Plan(MoveStatus.Pinned, false, true,
                    "Pinned, and skipPinned=false. Unpin it in Revit and run again — this tool never unpins elements.");
        }

        if (distance < NegligibleMoveMm)
            return Plan(MoveStatus.AlreadyThere, false, false, "The displacement is zero, so there is nothing to move.");

        return Plan(MoveStatus.Ready, true, false, null);
    }

    /// <summary>A plan that cannot go ahead for a reason found outside the maths: group membership, the view plane.</summary>
    public static MovePlan Blocked(MovePlan plan, string status, string reason) => new()
    {
        ElementId = plan.ElementId,
        Mode = plan.Mode,
        CurrentPointMm = plan.CurrentPointMm,
        TargetPointMm = plan.TargetPointMm,
        TranslationMm = plan.TranslationMm,
        DistanceMm = plan.DistanceMm,
        Pinned = plan.Pinned,
        StaleDeviationMm = plan.StaleDeviationMm,
        CanMove = false,
        IsFailure = true,
        Status = status,
        Reason = reason
    };

    public static MovePlan Missing(long elementId) => new()
    {
        ElementId = elementId,
        CanMove = false,
        IsFailure = true,
        Status = MoveStatus.Missing,
        Reason = "No element with this id exists in the active document."
    };

    public static MovePlan UnsupportedLocation(long elementId, string reason) => new()
    {
        ElementId = elementId,
        CanMove = false,
        IsFailure = true,
        Status = MoveStatus.UnsupportedLocation,
        Reason = reason
    };

    /// <summary>
    /// Whether a batch that produced <paramref name="failureCount"/> failures has to be undone.
    /// atomic=false keeps whatever succeeded; atomic=true means the request only makes sense whole.
    /// </summary>
    public static bool ShouldRollBack(bool atomic, int failureCount) => atomic && failureCount > 0;

    public static int CountFailures(IEnumerable<MovePlan> plans) => plans.Count(plan => plan.IsFailure);

    /// <summary>Buckets the finished plans by status, one list per outcome.</summary>
    public static MoveSummary Summarise(IEnumerable<MovePlan> plans)
    {
        var summary = new MoveSummary();
        foreach (var plan in plans)
        {
            var bucket = plan.Status switch
            {
                MoveStatus.Moved => summary.Moved,
                MoveStatus.AlreadyThere => summary.Skipped,
                MoveStatus.Stale => summary.Stale,
                MoveStatus.Missing => summary.Missing,
                MoveStatus.Pinned => summary.Pinned,
                MoveStatus.UnsupportedLocation => summary.Unsupported,
                MoveStatus.InGroup => summary.Constrained,
                MoveStatus.OutOfViewPlane => summary.Constrained,
                MoveStatus.RolledBack => summary.RolledBack,
                MoveStatus.NotAttempted => summary.NotAttempted,
                _ => summary.Failed
            };
            bucket.Add(plan.ElementId);
        }
        return summary;
    }

    // ── Request parsing ──────────────────────────────────────────────────────

    /// <summary>
    /// Reads the <c>moves</c> array. Returns null with <paramref name="error"/> set when the request
    /// is malformed enough that no part of it can be trusted; per-move oddities that still have a
    /// defined meaning are appended to <paramref name="warnings"/>.
    /// </summary>
    public static List<MoveRequest>? ParseMoves(object? raw, List<string> warnings, out string? error)
    {
        error = null;

        var array = ToJArray(raw);
        if (array == null)
        {
            error = "Provide 'moves': a JSON array of " +
                    "{elementId, targetXmm, targetYmm, targetZmm} (absolute) or " +
                    "{elementId, deltaXmm, deltaYmm, deltaZmm} / {elementId, deltaRightMm, deltaUpMm} (displacement), " +
                    "each optionally with expectedXmm, expectedYmm, expectedZmm.";
            return null;
        }

        if (array.Count == 0)
        {
            error = "'moves' is empty — there is nothing to move.";
            return null;
        }

        if (array.Count > MaxMoves)
        {
            error = $"'moves' holds {array.Count} entries; the limit is {MaxMoves} per request. " +
                    "Split the batch — silently dropping moves would leave the model half-aligned.";
            return null;
        }

        var moves = new List<MoveRequest>(array.Count);
        var seen = new Dictionary<long, int>();

        for (var index = 0; index < array.Count; index++)
        {
            var fields = ToFieldMap(array[index]);
            if (fields == null)
            {
                error = $"moves[{index}] is not a JSON object.";
                return null;
            }

            // Read straight to long: Revit 2026 hands out element ids above int.MaxValue, and a
            // detour through int or double would quietly land on the wrong element.
            var elementId = NullableLong(fields, "elementId");
            if (elementId is not > 0)
            {
                error = $"moves[{index}] has no usable 'elementId'.";
                return null;
            }

            if (seen.TryGetValue(elementId.Value, out var firstIndex))
            {
                error = $"Element {elementId.Value} appears at moves[{firstIndex}] and moves[{index}]. " +
                        "An element can only be given one destination per request.";
                return null;
            }
            seen[elementId.Value] = index;

            var move = new MoveRequest
            {
                ElementId = elementId.Value,
                TargetXMm = NullableDouble(fields, "targetXmm", "targetX", "x"),
                TargetYMm = NullableDouble(fields, "targetYmm", "targetY", "y"),
                TargetZMm = NullableDouble(fields, "targetZmm", "targetZ", "z"),
                ExpectedXMm = NullableDouble(fields, "expectedXmm", "expectedX"),
                ExpectedYMm = NullableDouble(fields, "expectedYmm", "expectedY"),
                ExpectedZMm = NullableDouble(fields, "expectedZmm", "expectedZ"),
                DeltaXMm = NullableDouble(fields, "deltaXmm", "deltaX", "dx"),
                DeltaYMm = NullableDouble(fields, "deltaYmm", "deltaY", "dy"),
                DeltaZMm = NullableDouble(fields, "deltaZmm", "deltaZ", "dz"),
                DeltaRightMm = NullableDouble(fields, "deltaRightMm", "deltaRight"),
                DeltaUpMm = NullableDouble(fields, "deltaUpMm", "deltaUp")
            };

            if (move.HasTarget && move.HasDelta)
            {
                error = $"moves[{index}] (element {move.ElementId}) gives both a target and a delta. " +
                        "Use target* to place the insertion point on a coordinate, or delta* to shift by a displacement — not both.";
                return null;
            }

            if (move.HasModelDelta && move.HasViewDelta)
            {
                error = $"moves[{index}] (element {move.ElementId}) mixes model-axis deltas (deltaXmm/Ymm/Zmm) with " +
                        "view-axis deltas (deltaRightMm/deltaUpMm). Give the displacement in one set of axes.";
                return null;
            }

            if (!move.HasTarget && !move.HasDelta)
            {
                warnings.Add(
                    $"moves[{index}] (element {move.ElementId}) has no target coordinate and no delta — " +
                    "it is reported as already there and nothing moves.");
            }

            moves.Add(move);
        }

        return moves;
    }

    private static JArray? ToJArray(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case JArray array:
                return array;
            case string text:
                if (string.IsNullOrWhiteSpace(text)) return null;
                try { return JToken.Parse(text) as JArray; }
                catch { return null; }
            default:
                try { return JArray.FromObject(value); }
                catch { return null; }
        }
    }

    /// <summary>
    /// Flattens one move into a case-insensitive field map. Callers write <c>targetXmm</c>,
    /// <c>targetXMm</c> and <c>targetXMM</c> interchangeably, and a silently ignored target
    /// coordinate would leave an element behind with no error.
    /// </summary>
    private static Dictionary<string, JToken>? ToFieldMap(JToken? token)
    {
        if (token is not JObject entry)
            return null;

        var map = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in entry.Properties())
            map[property.Name] = property.Value;
        return map;
    }

    private static double? NullableDouble(Dictionary<string, JToken> fields, params string[] names)
    {
        foreach (var name in names)
        {
            if (!fields.TryGetValue(name, out var token) || token.Type is JTokenType.Null or JTokenType.Undefined)
                continue;
            try
            {
                var value = token.Value<double>();
                if (!double.IsNaN(value) && !double.IsInfinity(value))
                    return value;
            }
            catch
            {
                // A coordinate that will not read as a number is treated as absent, which for a
                // target means "leave this axis alone" — never as zero.
            }
        }
        return null;
    }

    private static long? NullableLong(Dictionary<string, JToken> fields, string name)
    {
        if (!fields.TryGetValue(name, out var token) || token.Type is JTokenType.Null or JTokenType.Undefined)
            return null;
        try { return token.Value<long>(); }
        catch { return null; }
    }
}
