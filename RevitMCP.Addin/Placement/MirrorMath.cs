using System.Globalization;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Placement;

/// <summary>The per-element outcome names of the mirror tools (#108).</summary>
public static class MirrorStatus
{
    public const string Ready = "Ready";

    /// <summary>A mirrored copy was created (copy=true).</summary>
    public const string Mirrored = "Mirrored";

    /// <summary>The element itself was mirrored in place (copy=false).</summary>
    public const string Moved = "Moved";

    public const string Missing = "Missing";

    /// <summary>Pinned, with copy=false: a pinned element cannot be mirrored in place.</summary>
    public const string Pinned = "Pinned";

    /// <summary>ElementTransformUtils.CanMirrorElement said no.</summary>
    public const string NotMirrorable = "NotMirrorable";

    /// <summary>A group member mirrored in place (copy=false): Revit only edits members in group edit mode.</summary>
    public const string InGroup = "InGroup";

    /// <summary>A view, sheet or type; or a view-specific element the plane would take off its view.</summary>
    public const string Unsupported = "Unsupported";

    public const string Failed = "Failed";
    public const string RolledBack = "RolledBack";
    public const string NotAttempted = "NotAttempted";
}

/// <summary>How the mirror plane was given.</summary>
public static class MirrorPlaneMode
{
    /// <summary>fromMm/toMm: a line drawn in a view; the plane contains the line and the view direction.</summary>
    public const string Line = "line";

    /// <summary>axis x|y|z + throughMm: the plane perpendicular to that model axis through the point.</summary>
    public const string Axis = "axis";

    /// <summary>pointMm + normal: an explicit plane.</summary>
    public const string Normal = "normal";
}

/// <summary>The plane argument as it arrived. Coordinates are model millimetres.</summary>
public sealed class MirrorPlaneRequest
{
    public string Mode { get; init; } = MirrorPlaneMode.Line;

    /// <summary>The view the line is drawn in (its direction is the plane's second axis). 0 = a plan (model Z).</summary>
    public long ViewId { get; init; }

    public PointMm? FromMm { get; init; }
    public PointMm? ToMm { get; init; }
    public string? Axis { get; init; }
    public PointMm? ThroughMm { get; init; }
    public PointMm? PointOnPlaneMm { get; init; }
    public PointMm? Normal { get; init; }
}

/// <summary>A resolved mirror plane: a point on it and its unit normal, in model mm.</summary>
public readonly struct MirrorPlane
{
    public MirrorPlane(PointMm origin, PointMm normal, string description)
    {
        Origin = origin;
        Normal = normal;
        Description = description;
    }

    public PointMm Origin { get; }
    public PointMm Normal { get; }

    /// <summary>"a vertical plane through (x, y)–(x, y)" and the like, for messages and approval summaries.</summary>
    public string Description { get; }

    /// <summary>True when the plane stands upright: its normal has no vertical component.</summary>
    public bool IsVertical => Math.Abs(Normal.Z) <= MirrorMath.VerticalTolerance;
}

/// <summary>
/// The Revit-free part of the mirror tools (#108): reading the plane argument, turning it into an origin and a
/// normal, reflecting points to preview where elements land, and pairing sources with their mirrored copies.
/// </summary>
public static class MirrorMath
{
    /// <summary>Upper bound on one request, as for move and copy.</summary>
    public const int MaxElements = 2000;

    /// <summary>|normal.Z| up to this counts as a vertical plane (about 0.006°).</summary>
    public const double VerticalTolerance = 1e-4;

    /// <summary>A line shorter than this (after projection onto the view plane) defines no direction.</summary>
    public const double MinLineLengthMm = 1.0;

    /// <summary>How close a copy must sit to the reflected source point to be paired by position.</summary>
    public const double PairToleranceMm = 1.0;

    /// <summary>Status code when ElementTransformUtils.CanMirrorElement(s) refuses elements.</summary>
    public const string NotAllowedStatus = "mirror_not_allowed";

    // ── Plane ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the <c>plane</c> argument: {viewId?, fromMm {x,y,z?}, toMm {x,y,z?}}, {axis: x|y|z, throughMm {x,y,z}}
    /// or {pointMm {x,y,z}, normal {x,y,z}}. <paramref name="fallbackViewId"/> is the request's top-level viewId.
    /// </summary>
    public static MirrorPlaneRequest? ParsePlane(object? raw, long fallbackViewId, out string? error)
    {
        error = null;
        var fields = ToFieldMap(raw);
        if (fields == null)
        {
            error = "Provide 'plane': {viewId?, fromMm {x,y}, toMm {x,y}} (a line in a view — the mirror plane contains it " +
                    "and the view direction), {axis: x|y|z, throughMm {x,y,z}} or {pointMm {x,y,z}, normal {x,y,z}}.";
            return null;
        }

        var viewId = ReadLong(fields, "viewId");
        if (viewId <= 0) viewId = fallbackViewId;

        var from = ReadPoint(fields, "fromMm", "from");
        var to = ReadPoint(fields, "toMm", "to");
        var axis = ReadString(fields, "axis");
        var through = ReadPoint(fields, "throughMm", "through");
        var point = ReadPoint(fields, "pointMm", "point", "originMm");
        var normal = ReadPoint(fields, "normal");

        var modes = new List<string>();
        if (from.HasValue || to.HasValue) modes.Add(MirrorPlaneMode.Line);
        if (!string.IsNullOrWhiteSpace(axis)) modes.Add(MirrorPlaneMode.Axis);
        if (normal.HasValue || point.HasValue) modes.Add(MirrorPlaneMode.Normal);

        if (modes.Count == 0)
        {
            error = "'plane' names no plane. Give fromMm and toMm, or axis and throughMm, or pointMm and normal.";
            return null;
        }
        if (modes.Count > 1)
        {
            error = $"'plane' mixes {string.Join(" and ", modes)} forms. Give exactly one: fromMm/toMm, axis/throughMm, or pointMm/normal.";
            return null;
        }

        switch (modes[0])
        {
            case MirrorPlaneMode.Line:
                if (!from.HasValue || !to.HasValue)
                {
                    error = "A mirror line needs both fromMm and toMm ({x, y} in model mm; z optional).";
                    return null;
                }
                return new MirrorPlaneRequest { Mode = MirrorPlaneMode.Line, ViewId = viewId, FromMm = from, ToMm = to };

            case MirrorPlaneMode.Axis:
                var normalized = axis!.Trim().ToLowerInvariant();
                if (normalized is not ("x" or "y" or "z"))
                {
                    error = $"plane.axis must be x, y or z, not '{axis}'.";
                    return null;
                }
                if (!through.HasValue)
                {
                    error = "plane.axis needs throughMm {x, y, z}: the point the mirror plane passes through.";
                    return null;
                }
                return new MirrorPlaneRequest { Mode = MirrorPlaneMode.Axis, ViewId = viewId, Axis = normalized, ThroughMm = through };

            default:
                if (!point.HasValue || !normal.HasValue)
                {
                    error = "An explicit plane needs both pointMm and normal.";
                    return null;
                }
                return new MirrorPlaneRequest { Mode = MirrorPlaneMode.Normal, ViewId = viewId, PointOnPlaneMm = point, Normal = normal };
        }
    }

    /// <summary>
    /// Turns the request into an origin and a unit normal. A line uses <paramref name="viewFrame"/>: the plane contains
    /// the line (projected onto the view plane) and the view direction — in a plan that is a vertical plane through the
    /// line, which is what Revit's Mirror – Draw Axis does. Without a view the line is taken as drawn in plan.
    /// </summary>
    public static MirrorPlane? Resolve(MirrorPlaneRequest request, ViewFrame? viewFrame, string? viewName, out string? error)
    {
        error = null;
        switch (request.Mode)
        {
            case MirrorPlaneMode.Line:
            {
                var from = request.FromMm!.Value;
                var to = request.ToMm!.Value;
                var viewNormal = viewFrame?.Normal ?? new PointMm(0, 0, 1);
                var direction = to.Minus(from);
                direction = direction.Minus(viewNormal.Times(direction.Dot(viewNormal)));
                if (direction.Length < MinLineLengthMm)
                {
                    error = viewFrame == null
                        ? "fromMm and toMm are the same point in plan, so they define no mirror line."
                        : $"fromMm and toMm are the same point as seen in view '{viewName}', so they define no mirror line.";
                    return null;
                }

                var normal = Normalize(Cross(direction, viewNormal));
                var inView = viewName != null ? $" in view '{viewName}'" : string.Empty;
                var kind = Math.Abs(normal.Z) <= VerticalTolerance ? "a vertical plane" : "a plane";
                return new MirrorPlane(from, normal,
                    $"{kind} through ({Mm(from.X)}, {Mm(from.Y)})–({Mm(to.X)}, {Mm(to.Y)}){inView}");
            }

            case MirrorPlaneMode.Axis:
            {
                var through = request.ThroughMm!.Value;
                return request.Axis switch
                {
                    "x" => new MirrorPlane(through, new PointMm(1, 0, 0), $"the vertical plane x = {Mm(through.X)} mm"),
                    "y" => new MirrorPlane(through, new PointMm(0, 1, 0), $"the vertical plane y = {Mm(through.Y)} mm"),
                    _ => new MirrorPlane(through, new PointMm(0, 0, 1), $"the horizontal plane z = {Mm(through.Z)} mm")
                };
            }

            default:
            {
                var normal = request.Normal!.Value;
                if (normal.Length < 1e-9)
                {
                    error = "plane.normal is a zero vector.";
                    return null;
                }
                var unit = Normalize(normal);
                var point = request.PointOnPlaneMm!.Value;
                return new MirrorPlane(point, unit,
                    $"the plane through ({Mm(point.X)}, {Mm(point.Y)}, {Mm(point.Z)}) with normal " +
                    $"({unit.X.ToString("0.###", CultureInfo.InvariantCulture)}, {unit.Y.ToString("0.###", CultureInfo.InvariantCulture)}, " +
                    $"{unit.Z.ToString("0.###", CultureInfo.InvariantCulture)})");
            }
        }
    }

    /// <summary>
    /// The horizontal-plane guard: a plane that is not vertical flips elements upside down, which for a device is almost
    /// always a mistake. Returns the refusal, or null when the plane may be used.
    /// </summary>
    public static string? HorizontalPlaneRefusal(MirrorPlane plane, bool allowHorizontalPlane) =>
        plane.IsVertical || allowHorizontalPlane
            ? null
            : $"The mirror plane is not vertical (its normal has a vertical component of " +
              $"{plane.Normal.Z.ToString("0.###", CultureInfo.InvariantCulture)}), so mirroring would turn elements upside down. " +
              "Pass allowHorizontalPlane=true if that is really intended.";

    /// <summary>The mirror image of a point.</summary>
    public static PointMm Reflect(PointMm point, MirrorPlane plane)
    {
        var distance = point.Minus(plane.Origin).Dot(plane.Normal);
        return point.Minus(plane.Normal.Times(2 * distance));
    }

    /// <summary>
    /// Whether a view-specific element can be mirrored in its view: the plane must contain the view direction, or the
    /// mirror image would leave the view's plane. True when the plane normal is perpendicular to the view normal.
    /// </summary>
    public static bool KeepsViewPlane(MirrorPlane plane, ViewFrame ownerFrame) =>
        Math.Abs(plane.Normal.Dot(ownerFrame.Normal)) <= VerticalTolerance;

    /// <summary>
    /// Pairs sources with the mirrored copies MirrorElements created: same class, category and type, sitting on the
    /// reflected source point; what is left of a kind is paired in id order when the counts agree. Unpaired created
    /// elements are dependents (see <see cref="CopyElementsMath.Dependencies"/>).
    /// </summary>
    public static Dictionary<long, (ElementSnapshot Copy, string Mapping)> MatchMirrored(
        IReadOnlyList<ElementSnapshot> sources, IReadOnlyList<ElementSnapshot> created, MirrorPlane plane)
    {
        var result = new Dictionary<long, (ElementSnapshot, string)>();
        var used = new HashSet<long>();

        foreach (var source in sources)
        {
            if (!source.Anchor.HasValue) continue;
            var expected = Reflect(source.Anchor.Value, plane);
            var hits = created.Where(c => !used.Contains(c.Id) && c.SameKindAs(source) && c.Anchor.HasValue &&
                                          c.Anchor.Value.Minus(expected).Length <= PairToleranceMm).ToList();
            if (hits.Count != 1) continue;
            result[source.Id] = (hits[0], CopyMapping.Position);
            used.Add(hits[0].Id);
        }

        foreach (var kind in sources.Where(s => !result.ContainsKey(s.Id)).GroupBy(s => (s.ClassName, s.Category, s.TypeId)))
        {
            var kindSources = kind.OrderBy(s => s.Id).ToList();
            var kindCopies = created.Where(c => !used.Contains(c.Id) && c.SameKindAs(kindSources[0])).OrderBy(c => c.Id).ToList();
            if (kindCopies.Count != kindSources.Count) continue;
            var mapping = sources.Count == 1 ? CopyMapping.Single : CopyMapping.Order;
            for (var i = 0; i < kindSources.Count; i++)
            {
                result[kindSources[i].Id] = (kindCopies[i], mapping);
                used.Add(kindCopies[i].Id);
            }
        }

        return result;
    }

    // ── Vector helpers ───────────────────────────────────────────────────────

    public static PointMm Cross(PointMm a, PointMm b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    public static PointMm Normalize(PointMm v)
    {
        var length = v.Length;
        return length < 1e-12 ? v : v.Times(1.0 / length);
    }

    private static string Mm(double value) => Math.Round(value, 1).ToString("0.#", CultureInfo.InvariantCulture);

    // ── Argument reading (shared with ArrayMath and GroupSupport) ───────────

    internal static Dictionary<string, JToken>? ToFieldMap(object? raw)
    {
        JObject? entry = raw switch
        {
            null => null,
            JObject o => o,
            string text when !string.IsNullOrWhiteSpace(text) => TryParseObject(text),
            JValue { Type: JTokenType.String } jv => TryParseObject(jv.Value<string>() ?? string.Empty),
            JToken => null,
            _ => TryFromObject(raw)
        };
        if (entry == null) return null;
        var map = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in entry.Properties()) map[property.Name] = property.Value;
        return map;
    }

    private static JObject? TryParseObject(string text)
    {
        try { return JToken.Parse(text) as JObject; }
        catch { return null; }
    }

    private static JObject? TryFromObject(object raw)
    {
        try { return JObject.FromObject(raw); }
        catch { return null; }
    }

    /// <summary>Reads {x, y, z?} (or [x, y, z?]). A missing z is 0; a missing x or y means no point.</summary>
    internal static PointMm? ReadPoint(Dictionary<string, JToken> fields, params string[] names)
    {
        foreach (var name in names)
        {
            if (!fields.TryGetValue(name, out var token) || token.Type is JTokenType.Null or JTokenType.Undefined) continue;
            var point = PointFromToken(token, out _);
            if (point.HasValue) return point;
        }
        return null;
    }

    /// <summary>A point token; <paramref name="hasZ"/> says whether z was given.</summary>
    internal static PointMm? PointFromToken(JToken token, out bool hasZ)
    {
        hasZ = false;
        try
        {
            if (token.Type == JTokenType.String)
                token = JToken.Parse(token.Value<string>() ?? string.Empty);
            if (token is JArray array && array.Count >= 2)
            {
                hasZ = array.Count >= 3;
                return new PointMm(array[0].Value<double>(), array[1].Value<double>(), hasZ ? array[2].Value<double>() : 0);
            }
            if (token is JObject obj)
            {
                var map = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in obj.Properties()) map[property.Name] = property.Value;
                var x = ReadDouble(map, "x", "xMm");
                var y = ReadDouble(map, "y", "yMm");
                var z = ReadDouble(map, "z", "zMm");
                if (!x.HasValue || !y.HasValue) return null;
                hasZ = z.HasValue;
                return new PointMm(x.Value, y.Value, z ?? 0);
            }
        }
        catch
        {
            // not a point
        }
        return null;
    }

    internal static double? ReadDouble(Dictionary<string, JToken> fields, params string[] names)
    {
        foreach (var name in names)
        {
            if (!fields.TryGetValue(name, out var token) || token.Type is JTokenType.Null or JTokenType.Undefined) continue;
            try
            {
                var value = token.Value<double>();
                if (!double.IsNaN(value) && !double.IsInfinity(value)) return value;
            }
            catch
            {
                // not a number: treated as absent
            }
        }
        return null;
    }

    internal static long ReadLong(Dictionary<string, JToken> fields, string name)
    {
        if (!fields.TryGetValue(name, out var token) || token.Type is JTokenType.Null or JTokenType.Undefined) return 0;
        try { return token.Value<long>(); }
        catch { return 0; }
    }

    internal static string? ReadString(Dictionary<string, JToken> fields, string name)
    {
        if (!fields.TryGetValue(name, out var token) || token.Type is JTokenType.Null or JTokenType.Undefined) return null;
        try { return token.Type == JTokenType.String ? token.Value<string>() : token.ToString(); }
        catch { return null; }
    }
}
