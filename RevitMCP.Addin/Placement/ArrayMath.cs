using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Placement;

/// <summary>Linear or radial (#108).</summary>
public static class ArrayKind
{
    public const string Linear = "linear";
    public const string Radial = "radial";
}

/// <summary>The array request as it arrived, in model mm and degrees.</summary>
public sealed class ArrayRequest
{
    public List<long> ElementIds { get; init; } = new();
    public string Kind { get; init; } = ArrayKind.Linear;

    /// <summary>Members of the array: with includeOriginal=true the source counts as one of them.</summary>
    public int Count { get; init; }

    /// <summary>Linear: displacement from one member to the next, model axes.</summary>
    public PointMm? SpacingMm { get; init; }

    /// <summary>Linear: displacement from the original to the last copy, model axes.</summary>
    public PointMm? TotalMm { get; init; }

    /// <summary>Linear: displacement per step along a view's right and up.</summary>
    public double? DeltaRightMm { get; init; }
    public double? DeltaUpMm { get; init; }

    /// <summary>Radial: the point the axis passes through.</summary>
    public PointMm? CenterMm { get; init; }

    /// <summary>Radial: x, y or z (default z, a vertical axis).</summary>
    public string Axis { get; init; } = "z";

    /// <summary>Radial: angle per step, counter-clockwise about the axis.</summary>
    public double? AngleDeg { get; init; }

    /// <summary>Radial: angle from the original to the last copy; 360 spreads the members evenly round a full circle.</summary>
    public double? TotalAngleDeg { get; init; }

    /// <summary>Radial: turn each copy with the array (true, default) or keep its orientation and only move it round.</summary>
    public bool RotateElements { get; init; } = true;

    /// <summary>false (default): independent copies. true: a Revit array group (LinearArray / RadialArray).</summary>
    public bool AsGroup { get; init; }

    public bool IncludeOriginal { get; init; } = true;
    public bool Atomic { get; init; } = true;

    /// <summary>The view whose axes deltaRightMm/deltaUpMm follow, and the view a model array group is created in.</summary>
    public long ViewId { get; init; }

    public bool HasViewDelta => DeltaRightMm.HasValue || DeltaUpMm.HasValue;

    /// <summary>How many new copies: count minus the original when it is included.</summary>
    public int Copies => ArrayMath.CopiesFor(Count, IncludeOriginal);
}

/// <summary>One step of the array: copy number <see cref="Index"/> (1-based) and how it is reached from the source.</summary>
public sealed class ArrayStep
{
    public int Index { get; init; }

    /// <summary>Translation from the source, model mm. Zero for a radial step with rotateElements=true.</summary>
    public PointMm TranslationMm { get; init; }

    /// <summary>Radial: rotation about the array axis from the source, degrees (0 for a linear step).</summary>
    public double RotationDeg { get; init; }
}

/// <summary>
/// The Revit-free part of the array tools (#108): request reading, the count rule (with or without the original) and
/// the transform of each step — a translation for a linear array, a rotation about an axis for a radial one.
/// </summary>
public static class ArrayMath
{
    public const int MinCount = 2;
    public const int MaxCount = 200;

    /// <summary>Revit's RadialArray needs at least three members in a project.</summary>
    public const int MinRadialGroupCount = 3;

    public const string CountOutOfRangeStatus = "array_count_out_of_range";

    public static int CopiesFor(int count, bool includeOriginal) => includeOriginal ? count - 1 : count;

    /// <summary>
    /// Reads the request. Returns null with <paramref name="error"/> (and <paramref name="status"/> for a count out of
    /// range) when it cannot be acted on.
    /// </summary>
    public static ArrayRequest? Parse(Dictionary<string, object?> arguments, out string? error, out string? status)
    {
        error = null;
        status = null;
        var fields = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in arguments)
        {
            if (pair.Value == null) continue;
            try { fields[pair.Key] = pair.Value as JToken ?? JToken.FromObject(pair.Value); }
            catch { /* unreadable argument: treated as absent */ }
        }

        var ids = ReadIds(fields, "elementIds");
        if (ids == null || ids.Count == 0)
        {
            error = "Provide elementIds: the elements to array.";
            return null;
        }
        if (ids.Count > CopyElementsMath.MaxElements)
        {
            error = $"'elementIds' holds {ids.Count} entries; the limit is {CopyElementsMath.MaxElements} per request.";
            return null;
        }
        var duplicate = ids.GroupBy(id => id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
        {
            error = $"Element {duplicate.Key} appears {duplicate.Count()} times in elementIds. List each element once.";
            return null;
        }

        var kind = (MirrorMath.ReadString(fields, "kind") ?? ArrayKind.Linear).Trim().ToLowerInvariant();
        if (kind is not (ArrayKind.Linear or ArrayKind.Radial))
        {
            error = $"kind must be 'linear' or 'radial', not '{kind}'.";
            return null;
        }

        var count = (int)(MirrorMath.ReadDouble(fields, "count") ?? 0);
        var asGroup = ReadBool(fields, "asGroup", false);
        var includeOriginal = ReadBool(fields, "includeOriginal", true);
        var minimum = asGroup && kind == ArrayKind.Radial ? MinRadialGroupCount : MinCount;
        if (count < minimum || count > MaxCount)
        {
            status = CountOutOfRangeStatus;
            error = $"count must be between {minimum} and {MaxCount}{(asGroup && kind == ArrayKind.Radial ? " for a Revit radial array" : string.Empty)}, " +
                    $"not {count}. With includeOriginal=true (default) the original is one of the {Math.Max(count, 0)} members.";
            return null;
        }
        if (!includeOriginal && count + 1 > MaxCount)
        {
            status = CountOutOfRangeStatus;
            error = $"count {count} copies plus the original exceeds {MaxCount} members.";
            return null;
        }

        if (asGroup && !includeOriginal)
        {
            error = "asGroup=true makes a Revit array, whose count always includes the original. Pass includeOriginal=true " +
                    "(count = members including the original) or asGroup=false for plain copies.";
            return null;
        }

        var request = new ArrayRequest
        {
            ElementIds = ids,
            Kind = kind,
            Count = count,
            SpacingMm = MirrorMath.ReadPoint(fields, "spacingMm"),
            TotalMm = MirrorMath.ReadPoint(fields, "totalMm"),
            DeltaRightMm = MirrorMath.ReadDouble(fields, "deltaRightMm"),
            DeltaUpMm = MirrorMath.ReadDouble(fields, "deltaUpMm"),
            CenterMm = MirrorMath.ReadPoint(fields, "centerMm"),
            Axis = (MirrorMath.ReadString(fields, "axis") ?? "z").Trim().ToLowerInvariant(),
            AngleDeg = MirrorMath.ReadDouble(fields, "angleDeg"),
            TotalAngleDeg = MirrorMath.ReadDouble(fields, "totalAngleDeg"),
            RotateElements = ReadBool(fields, "rotateElements", true),
            AsGroup = asGroup,
            IncludeOriginal = includeOriginal,
            Atomic = ReadBool(fields, "atomic", true),
            ViewId = MirrorMath.ReadLong(fields, "viewId")
        };

        error = request.Kind == ArrayKind.Linear ? CheckLinear(request) : CheckRadial(request);
        return error == null ? request : null;
    }

    private static string? CheckLinear(ArrayRequest request)
    {
        var forms = (request.SpacingMm.HasValue ? 1 : 0) + (request.TotalMm.HasValue ? 1 : 0) + (request.HasViewDelta ? 1 : 0);
        if (forms == 0)
            return "A linear array needs spacingMm {x,y,z} (member to member), totalMm {x,y,z} (original to last copy) " +
                   "or deltaRightMm/deltaUpMm (member to member along a view's axes).";
        if (forms > 1)
            return "Give the linear spacing one way: spacingMm, totalMm, or deltaRightMm/deltaUpMm — not several.";
        return null;
    }

    private static string? CheckRadial(ArrayRequest request)
    {
        if (!request.CenterMm.HasValue)
            return "A radial array needs centerMm {x,y,z}: the point its axis passes through.";
        if (request.Axis is not ("x" or "y" or "z"))
            return $"axis must be x, y or z, not '{request.Axis}'.";
        if (request.AngleDeg.HasValue == request.TotalAngleDeg.HasValue)
            return "A radial array needs exactly one of angleDeg (per step) or totalAngleDeg (original to last copy; 360 = full circle).";
        if (Math.Abs(StepAngleDeg(request)) < 1e-6)
            return "The angle per step is zero: every copy would land on the original.";
        return null;
    }

    /// <summary>
    /// The angle between consecutive members. totalAngleDeg runs from the original to the last copy, except a full
    /// circle (±360), which spreads all members evenly so the last copy does not land on the original.
    /// </summary>
    public static double StepAngleDeg(ArrayRequest request)
    {
        if (request.AngleDeg.HasValue) return request.AngleDeg.Value;
        var total = request.TotalAngleDeg ?? 0;
        var copies = Math.Max(request.Copies, 1);
        return IsFullCircle(total) ? total / (copies + 1) : total / copies;
    }

    public static bool IsFullCircle(double angleDeg) => Math.Abs(Math.Abs(angleDeg) - 360.0) < 1e-6;

    /// <summary>The displacement between consecutive members of a linear array, in model mm.</summary>
    public static PointMm? LinearStep(ArrayRequest request, ViewFrame? frame, out string? error)
    {
        error = null;
        if (request.SpacingMm.HasValue) return request.SpacingMm.Value;
        if (request.TotalMm.HasValue) return request.TotalMm.Value.Times(1.0 / Math.Max(request.Copies, 1));
        if (frame == null)
        {
            error = "deltaRightMm/deltaUpMm follow a view's axes: pass viewId, or array view-specific elements (their owner view is used).";
            return null;
        }
        return frame.Value.Right.Times(request.DeltaRightMm ?? 0).Plus(frame.Value.Up.Times(request.DeltaUpMm ?? 0));
    }

    /// <summary>
    /// The N steps of the array, one per copy. Linear: copy k sits k × step from the source. Radial with
    /// rotateElements: copy k is the source turned k × angle about the axis (no translation; the copy is rotated).
    /// Radial without rotateElements: copy k is moved so <paramref name="referencePointMm"/> (the centre of the source
    /// set) lands on its rotated position, keeping its orientation.
    /// </summary>
    public static List<ArrayStep> Steps(ArrayRequest request, PointMm? linearStep, PointMm referencePointMm)
    {
        var steps = new List<ArrayStep>(request.Copies);
        for (var k = 1; k <= request.Copies; k++)
        {
            if (request.Kind == ArrayKind.Linear)
            {
                steps.Add(new ArrayStep { Index = k, TranslationMm = linearStep!.Value.Times(k) });
                continue;
            }

            var angle = StepAngleDeg(request) * k;
            steps.Add(request.RotateElements
                ? new ArrayStep { Index = k, TranslationMm = new PointMm(0, 0, 0), RotationDeg = angle }
                : new ArrayStep
                {
                    Index = k,
                    TranslationMm = RotatePoint(referencePointMm, request.CenterMm!.Value, request.Axis, angle).Minus(referencePointMm)
                });
        }
        return steps;
    }

    /// <summary>Where a point of the source ends up in copy <paramref name="step"/>.</summary>
    public static PointMm PositionAfter(ArrayStep step, PointMm point, ArrayRequest request) =>
        Math.Abs(step.RotationDeg) > 0 && request.CenterMm.HasValue
            ? RotatePoint(point, request.CenterMm.Value, request.Axis, step.RotationDeg)
            : point.Plus(step.TranslationMm);

    /// <summary>The unit vector of a model axis.</summary>
    public static PointMm AxisVector(string axis) => axis switch
    {
        "x" => new PointMm(1, 0, 0),
        "y" => new PointMm(0, 1, 0),
        _ => new PointMm(0, 0, 1)
    };

    /// <summary>Rotates a point counter-clockwise (right-hand rule) about the model axis through <paramref name="center"/>.</summary>
    public static PointMm RotatePoint(PointMm point, PointMm center, string axis, double angleDeg)
    {
        var a = angleDeg * Math.PI / 180.0;
        var cos = Math.Cos(a);
        var sin = Math.Sin(a);
        var p = point.Minus(center);
        var rotated = axis switch
        {
            "x" => new PointMm(p.X, p.Y * cos - p.Z * sin, p.Y * sin + p.Z * cos),
            "y" => new PointMm(p.X * cos + p.Z * sin, p.Y, -p.X * sin + p.Z * cos),
            _ => new PointMm(p.X * cos - p.Y * sin, p.X * sin + p.Y * cos, p.Z)
        };
        return rotated.Plus(center);
    }

    /// <summary>The centre of a set of points (the reference a non-rotating radial array moves round).</summary>
    public static PointMm Centroid(IReadOnlyCollection<PointMm> points)
    {
        if (points.Count == 0) return new PointMm(0, 0, 0);
        var sum = points.Aggregate(new PointMm(0, 0, 0), (acc, p) => acc.Plus(p));
        return sum.Times(1.0 / points.Count);
    }

    private static bool ReadBool(Dictionary<string, JToken> fields, string name, bool fallback)
    {
        if (!fields.TryGetValue(name, out var token) || token.Type != JTokenType.Boolean) return fallback;
        return token.Value<bool>();
    }

    private static List<long>? ReadIds(Dictionary<string, JToken> fields, string name)
    {
        if (!fields.TryGetValue(name, out var token)) return new List<long>();
        if (token.Type == JTokenType.String)
        {
            try { token = JToken.Parse(token.Value<string>() ?? "[]"); }
            catch { return null; }
        }
        if (token is not JArray array) return null;
        var ids = new List<long>(array.Count);
        foreach (var item in array)
        {
            try { ids.Add(item.Value<long>()); }
            catch { return null; }
        }
        return ids;
    }
}
