using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Placement;

namespace RevitMCP.Addin.Geometry;

/// <summary>A ray direction with the label the result shows for it (<c>+x</c>, <c>up</c>, <c>front</c>, <c>45°</c>, <c>custom</c>).</summary>
public sealed class NamedDirection
{
    public NamedDirection(string label, Vec3 direction)
    {
        Label = label;
        Direction = direction.Normalized();
    }

    public string Label { get; }

    /// <summary>Unit vector.</summary>
    public Vec3 Direction { get; }

    public override string ToString() => $"{Label} {Direction}";
}

/// <summary>
/// Pure maths of the ray tools (#107): the direction sets (<c>axes</c>, <c>horizontal</c>, <c>up</c>, <c>down</c>,
/// <c>all</c>, <c>local</c>, single labels and explicit vectors), where a clearance ray starts on an element's
/// bounding box, and the smallest free distance. No Revit API; unit tested.
/// </summary>
public static class ClearanceMath
{
    /// <summary>Most directions one ray tool call may cast.</summary>
    public const int MaxDirections = 32;

    public const string SetAxes = "axes";
    public const string SetHorizontal = "horizontal";
    public const string SetUp = "up";
    public const string SetDown = "down";
    public const string SetAll = "all";
    public const string SetLocal = "local";

    /// <summary>Two directions closer than this (dot product) count as the same and are cast once.</summary>
    private const double SameDirectionDot = 0.99999;

    /// <summary>+X, -X, +Y, -Y, up, down.</summary>
    public static List<NamedDirection> AxisDirections() => new()
    {
        new("+x", new Vec3(1, 0, 0)),
        new("-x", new Vec3(-1, 0, 0)),
        new("+y", new Vec3(0, 1, 0)),
        new("-y", new Vec3(0, -1, 0)),
        new("up", new Vec3(0, 0, 1)),
        new("down", new Vec3(0, 0, -1))
    };

    /// <summary>
    /// The compass ring: <paramref name="samples"/> horizontal directions from +X counter-clockwise, labelled by
    /// their angle from +X in degrees (<see cref="AlignmentMath.HorizontalRing"/>).
    /// </summary>
    public static List<NamedDirection> Horizontal(int samples)
    {
        var ring = AlignmentMath.HorizontalRing(samples);
        var result = new List<NamedDirection>(ring.Count);
        for (var i = 0; i < ring.Count; i++)
        {
            var degrees = 360.0 * i / ring.Count;
            result.Add(new NamedDirection(degrees.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "°", ring[i]));
        }
        return result;
    }

    /// <summary>
    /// The element's own frame: front (facing), back, right (hand), left, up, down. Null when the facing direction
    /// has no horizontal part. The hand direction is rebuilt from the facing when it is missing or not perpendicular.
    /// </summary>
    public static List<NamedDirection>? Local(Vec3? facing, Vec3? hand)
    {
        if (!facing.HasValue) return null;
        var front = new Vec3(facing.Value.X, facing.Value.Y, 0).Normalized();
        if (front.Length < 0.5) return null;

        Vec3 right;
        var h = hand.HasValue ? new Vec3(hand.Value.X, hand.Value.Y, 0).Normalized() : new Vec3(0, 0, 0);
        if (h.Length > 0.5 && Math.Abs(h.Dot(front)) < 0.1)
            right = h;
        else
            right = front.Cross(AlignmentMath.Up).Normalized();   // facing × up points to the right of the facing

        return new List<NamedDirection>
        {
            new("front", front),
            new("back", front.Negated()),
            new("right", right),
            new("left", right.Negated()),
            new("up", AlignmentMath.Up),
            new("down", AlignmentMath.Up.Negated())
        };
    }

    /// <summary>
    /// Resolves the <c>directions</c> argument: a set name or label (<c>axes</c>, <c>horizontal</c>, <c>up</c>,
    /// <c>down</c>, <c>all</c>, <c>local</c>, <c>+x</c> … <c>-z</c>, <c>front</c>/<c>back</c>/<c>left</c>/<c>right</c>),
    /// several of them comma-separated or as an array, or explicit vectors <c>[{x,y,z}]</c> / <c>[[x,y,z]]</c>
    /// (normalised). Empty means <paramref name="defaultSet"/>. Duplicates are cast once; more than
    /// <see cref="MaxDirections"/> is an error, as is a zero vector or an unknown name.
    /// </summary>
    public static List<NamedDirection>? Resolve(
        object? raw, string defaultSet, int horizontalSamples, Vec3? facing, Vec3? hand, out string? error)
    {
        error = null;
        var result = new List<NamedDirection>();
        var tokens = Tokens(raw);
        if (tokens == null)
        {
            error = "directions must be a set name (axes, horizontal, up, down, all, local), labels such as \"+x,up\", " +
                    "or an array of vectors [{x,y,z}].";
            return null;
        }
        if (tokens.Count == 0) tokens.Add(JValue.CreateString(defaultSet));

        foreach (var token in tokens)
        {
            if (token.Type == JTokenType.String)
            {
                foreach (var name in (token.Value<string>() ?? string.Empty).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var named = Named(name.Trim().ToLowerInvariant(), horizontalSamples, facing, hand, out error);
                    if (named == null) return null;
                    foreach (var direction in named) Add(result, direction);
                }
                continue;
            }

            if (!GeometryArgs.TryReadVector(token, out var vector, out var vectorError))
            {
                error = "directions: " + vectorError;
                return null;
            }
            if (vector.Length < 1e-9)
            {
                error = "directions: a zero-length vector has no direction.";
                return null;
            }
            Add(result, new NamedDirection("custom", vector));
        }

        if (result.Count > MaxDirections)
        {
            error = $"{result.Count} directions requested; at most {MaxDirections} per call.";
            return null;
        }
        return result;
    }

    /// <summary>
    /// Where a clearance ray starts: the point where a ray from the box centre along <paramref name="direction"/>
    /// leaves the box. For the six axis directions that is the centre of the box face, so the free distance is the
    /// gap between the element and what it faces.
    /// </summary>
    public static Vec3 ExitPoint(BoxMm box, Vec3 direction)
    {
        var d = direction.Normalized();
        var half = box.HalfExtents;
        var t = double.MaxValue;
        if (Math.Abs(d.X) > 1e-12) t = Math.Min(t, half.X / Math.Abs(d.X));
        if (Math.Abs(d.Y) > 1e-12) t = Math.Min(t, half.Y / Math.Abs(d.Y));
        if (Math.Abs(d.Z) > 1e-12) t = Math.Min(t, half.Z / Math.Abs(d.Z));
        if (t == double.MaxValue) t = 0;
        return box.Center.Plus(d.Scaled(t));
    }

    /// <summary>
    /// Free distance from a ray hit: the ray started <paramref name="backOff"/> behind the start point, so that much
    /// is taken off; something overlapping the start point gives 0, never a negative gap.
    /// </summary>
    public static double FreeDistance(double hitDistance, double backOff) => Math.Max(0, hitDistance - backOff);

    /// <summary>The direction with the smallest free distance among those that hit something; null when none did.</summary>
    public static (string Label, double FreeMm)? Minimum(IEnumerable<(string Label, double? FreeMm)> directions)
    {
        (string Label, double FreeMm)? best = null;
        foreach (var (label, free) in directions)
        {
            if (!free.HasValue) continue;
            if (best == null || free.Value < best.Value.FreeMm) best = (label, free.Value);
        }
        return best;
    }

    private static List<NamedDirection>? Named(string name, int horizontalSamples, Vec3? facing, Vec3? hand, out string? error)
    {
        error = null;
        switch (name)
        {
            case SetAxes: return AxisDirections();
            case SetHorizontal: return Horizontal(horizontalSamples);
            case SetUp: case "+z": return new() { new("up", AlignmentMath.Up) };
            case SetDown: case "-z": return new() { new("down", AlignmentMath.Up.Negated()) };
            case SetAll:
                var all = Horizontal(horizontalSamples);
                all.Add(new NamedDirection("up", AlignmentMath.Up));
                all.Add(new NamedDirection("down", AlignmentMath.Up.Negated()));
                return all;
            case "+x": case "-x": case "+y": case "-y":
                return AxisDirections().Where(d => d.Label == name).ToList();
            case SetLocal: case "front": case "back": case "left": case "right":
                var local = Local(facing, hand);
                if (local == null)
                {
                    error = $"directions '{name}' needs an element with a horizontal facing direction (a family instance); use axes or explicit vectors.";
                    return null;
                }
                return name == SetLocal ? local : local.Where(d => d.Label == name).ToList();
            default:
                error = $"Unknown direction '{name}'. Valid: axes, horizontal, up, down, all, local, +x, -x, +y, -y, +z, -z, " +
                        "front, back, left, right, or vectors [{x,y,z}].";
                return null;
        }
    }

    private static void Add(List<NamedDirection> list, NamedDirection direction)
    {
        if (list.Any(d => d.Direction.Dot(direction.Direction) > SameDirectionDot)) return;
        list.Add(direction);
    }

    /// <summary>The argument as a list of tokens: one string, an array of strings/vectors, or one vector object.</summary>
    private static List<JToken>? Tokens(object? raw)
    {
        switch (raw)
        {
            case null:
                return new List<JToken>();
            case string s:
                s = s.Trim();
                if (s.Length == 0) return new List<JToken>();
                if (s.StartsWith("[", StringComparison.Ordinal) || s.StartsWith("{", StringComparison.Ordinal))
                {
                    try { return Tokens(JToken.Parse(s)); }
                    catch { return null; }
                }
                return new List<JToken> { JValue.CreateString(s) };
            case JValue { Type: JTokenType.Null }:
                return new List<JToken>();
            case JValue { Type: JTokenType.String } value:
                return Tokens(value.Value<string>());
            case JArray array:
                // [x, y, z] of numbers is one vector, not three directions.
                if (array.Count == 3 && array.All(t => t.Type is JTokenType.Integer or JTokenType.Float))
                    return new List<JToken> { array };
                return array.ToList();
            case JToken token:
                return new List<JToken> { token };
            default:
                try { return Tokens(JToken.FromObject(raw)); }
                catch { return null; }
        }
    }
}
