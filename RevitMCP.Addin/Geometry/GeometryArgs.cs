using System.Globalization;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Placement;
using RevitMCP.Core.Models;
using RevitMCP.Core.Safety;

namespace RevitMCP.Addin.Geometry;

/// <summary>Status codes of the geometry tools (#107).</summary>
public static class GeometryStatus
{
    /// <summary>The call has no scope (no ids, box, point or category) or exceeds a cap.</summary>
    public const string QueryTooBroad = "geometry_query_too_broad";

    /// <summary>A ray tool found no 3D view to cast in.</summary>
    public const string No3DView = "no_3d_view_for_raycast";

    /// <summary>A linkInstanceId does not name a loaded link.</summary>
    public const string LinkNotFound = "link_not_found";
}

/// <summary>
/// Refuses geometry queries that are not scoped (#107), in the shape of <see cref="QueryGuard.ValidateGeometryQuery"/>:
/// <c>Data</c> carries <c>error</c> (<see cref="ToolErrorCodes.GeometryQueryTooBroad"/>), <c>message</c> and
/// <c>suggestedActions</c>; <c>Status</c> is <see cref="GeometryStatus.QueryTooBroad"/>. Pure; unit tested.
/// </summary>
public static class GeometryQueryGuard
{
    /// <summary>Most element ids one geometry read takes (<see cref="QueryLimits.MaxGeometryElements"/>).</summary>
    public static int MaxElementIds => QueryLimits.Default.MaxGeometryElements;

    /// <summary>Result rows a collector returns by default, and the most it returns without a category filter.</summary>
    public const int DefaultMaxResults = 200;

    /// <summary>Hard cap on collector rows.</summary>
    public const int MaxResults = 2000;

    /// <summary>
    /// Null when the query is scoped: it names element ids, a category, or a spatial scope. Otherwise the refusal,
    /// with <paramref name="suggestedActions"/> naming what this tool accepts.
    /// </summary>
    public static McpToolResult? Validate(
        string requestId, bool hasCategoryFilter, bool hasElementIds, bool hasSpatialScope,
        string message, params string[] suggestedActions)
    {
        var verdict = QueryGuard.ValidateGeometryQuery(
            includeGeometry: true, hasCategoryFilter: hasCategoryFilter, hasElementIds: hasElementIds, hasViewFilter: hasSpatialScope);
        return verdict == null ? null : Refuse(requestId, message, suggestedActions, verdict);
    }

    /// <summary>The refusal for a call that exceeds a cap (too many ids, too many rows without a category).</summary>
    public static McpToolResult Refuse(string requestId, string message, params string[] suggestedActions)
        => Refuse(requestId, message, suggestedActions, null);

    private static McpToolResult Refuse(string requestId, string message, string[] suggestedActions, SafeToolResult? verdict)
    {
        var actions = suggestedActions is { Length: > 0 } ? suggestedActions : verdict?.SuggestedActions ?? Array.Empty<string>();
        return new McpToolResult
        {
            RequestId = requestId,
            Success = false,
            Status = GeometryStatus.QueryTooBroad,
            Message = message,
            Data = new
            {
                success = false,
                error = ToolErrorCodes.GeometryQueryTooBroad,
                message,
                suggestedActions = actions,
                limits = new
                {
                    maxElementIds = MaxElementIds,
                    maxResults = MaxResults,
                    maxResultsWithoutCategory = DefaultMaxResults,
                    maxDirections = ClearanceMath.MaxDirections
                }
            }
        };
    }

    /// <summary>
    /// The row cap of a collector: <paramref name="requested"/> clamped to 1…<see cref="MaxResults"/>, default
    /// <see cref="DefaultMaxResults"/>. Without a category filter only up to <see cref="DefaultMaxResults"/> is
    /// allowed; above that <paramref name="tooBroad"/> is true.
    /// </summary>
    public static int ResolveMaxResults(int requested, bool hasCategoryFilter, List<string> warnings, out bool tooBroad)
    {
        tooBroad = false;
        var max = requested <= 0 ? DefaultMaxResults : requested;
        if (max > MaxResults)
        {
            warnings.Add($"maxResults {max} exceeds the cap; using {MaxResults}.");
            max = MaxResults;
        }
        if (!hasCategoryFilter && max > DefaultMaxResults) tooBroad = true;
        return max;
    }
}

/// <summary>
/// Reads the geometry tools' structured arguments (#107): points <c>{x,y,z}</c> or <c>[x,y,z]</c> in mm, boxes
/// <c>{min:{x,y,z}, max:{x,y,z}}</c> or <c>{minX … maxZ}</c>, also when they arrive as JSON text. Pure; unit tested.
/// </summary>
public static class GeometryArgs
{
    /// <summary>
    /// The argument that keeps the pre-#107 payload fields (<c>boundingBoxMm</c>, flat <c>bbox.minX … maxZ</c>) next to
    /// the shared DTOs. True by default for one release; see docs/geometry-queries.md.
    /// </summary>
    public const string LegacyArgument = "legacy";

    /// <summary>Whether the legacy payload fields are wanted (default true).</summary>
    public static bool Legacy(Dictionary<string, object?> args)
    {
        if (!args.TryGetValue(LegacyArgument, out var value) || value == null) return true;
        return value switch
        {
            bool b => b,
            JValue { Type: JTokenType.Boolean } v => v.Value<bool>(),
            JValue { Type: JTokenType.String } s => !string.Equals(s.Value<string>(), "false", StringComparison.OrdinalIgnoreCase),
            string text => !string.Equals(text, "false", StringComparison.OrdinalIgnoreCase),
            _ => true
        };
    }

    /// <summary>True when the argument is present and not empty (null, "", [] and {} count as absent).</summary>
    public static bool IsGiven(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value == null) return false;
        return value switch
        {
            string s => s.Trim().Length > 0 && s.Trim() != "{}" && s.Trim() != "[]",
            JValue v => v.Type != JTokenType.Null && !(v.Type == JTokenType.String && string.IsNullOrWhiteSpace(v.Value<string>())),
            JContainer c => c.Count > 0,
            _ => true
        };
    }

    /// <summary>Reads a point in mm. False with <paramref name="error"/> set when the value is not a point.</summary>
    public static bool TryReadPoint(object? raw, string name, out Vec3 point, out string? error)
    {
        point = default;
        error = null;
        var token = ToToken(raw);
        if (token == null)
        {
            error = $"{name} must be a point {{x, y, z}} in mm.";
            return false;
        }
        if (!TryReadVector(token, out point, out var vectorError))
        {
            error = $"{name}: {vectorError}";
            return false;
        }
        return true;
    }

    /// <summary>Reads a vector from <c>{x,y,z}</c> or <c>[x,y,z]</c>; a missing z in an object is 0.</summary>
    public static bool TryReadVector(JToken token, out Vec3 vector, out string? error)
    {
        vector = default;
        error = null;
        if (token is JArray array)
        {
            if (array.Count != 3 || !array.All(IsNumber))
            {
                error = "expected [x, y, z] with three numbers.";
                return false;
            }
            vector = new Vec3(array[0].Value<double>(), array[1].Value<double>(), array[2].Value<double>());
            return IsFinite(vector, out error);
        }
        if (token is JObject obj)
        {
            var x = Field(obj, "x");
            var y = Field(obj, "y");
            var z = Field(obj, "z");
            if (x == null || y == null)
            {
                error = "expected {x, y, z} with numbers.";
                return false;
            }
            vector = new Vec3(x.Value, y.Value, z ?? 0);
            return IsFinite(vector, out error);
        }
        error = "expected {x, y, z} or [x, y, z].";
        return false;
    }

    /// <summary>Reads a box in mm: <c>{min:{x,y,z}, max:{x,y,z}}</c> or <c>{minX, minY, minZ, maxX, maxY, maxZ}</c>; corners in any order.</summary>
    public static bool TryReadBox(object? raw, string name, out BoxMm box, out string? error)
    {
        box = default;
        error = null;
        if (ToToken(raw) is not JObject obj)
        {
            error = $"{name} must be {{min: {{x, y, z}}, max: {{x, y, z}}}} in mm.";
            return false;
        }

        Vec3 min = default, max = default;
        var minToken = Property(obj, "min");
        var maxToken = Property(obj, "max");
        if (minToken != null || maxToken != null)
        {
            var ok = minToken != null && maxToken != null &&
                     TryReadVector(minToken, out min, out _) && TryReadVector(maxToken, out max, out _);
            if (!ok)
            {
                error = $"{name} needs both min and max as {{x, y, z}} in mm.";
                return false;
            }
        }
        else
        {
            var values = new[] { "minX", "minY", "minZ", "maxX", "maxY", "maxZ" }.Select(k => Field(obj, k)).ToArray();
            if (values.Any(v => v == null))
            {
                error = $"{name} must be {{min: {{x, y, z}}, max: {{x, y, z}}}} (or minX … maxZ) in mm.";
                return false;
            }
            min = new Vec3(values[0]!.Value, values[1]!.Value, values[2]!.Value);
            max = new Vec3(values[3]!.Value, values[4]!.Value, values[5]!.Value);
        }

        if (!IsFinite(min, out error) || !IsFinite(max, out error))
        {
            error = $"{name}: {error}";
            return false;
        }
        box = BoxMath.FromCorners(min, max);
        return true;
    }

    private static JToken? ToToken(object? raw)
    {
        switch (raw)
        {
            case null: return null;
            case JValue { Type: JTokenType.String } s: return ToToken(s.Value<string>());
            case JToken token: return token.Type == JTokenType.Null ? null : token;
            case string text:
                text = text.Trim();
                if (text.Length == 0) return null;
                try { return JToken.Parse(text); }
                catch { return null; }
            case double[] doubles: return new JArray(doubles.Cast<object>().ToArray());
            default:
                try { return JToken.FromObject(raw); }
                catch { return null; }
        }
    }

    private static JToken? Property(JObject obj, string key) =>
        obj.Properties().FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase))?.Value;

    private static double? Field(JObject obj, string key)
    {
        var token = Property(obj, key);
        if (token == null || token.Type == JTokenType.Null) return null;
        if (IsNumber(token)) return token.Value<double>();
        if (token.Type == JTokenType.String &&
            double.TryParse(token.Value<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return null;
    }

    private static bool IsNumber(JToken token) => token.Type is JTokenType.Integer or JTokenType.Float;

    private static bool IsFinite(Vec3 v, out string? error)
    {
        error = null;
        if (double.IsNaN(v.X) || double.IsNaN(v.Y) || double.IsNaN(v.Z) ||
            double.IsInfinity(v.X) || double.IsInfinity(v.Y) || double.IsInfinity(v.Z))
        {
            error = "coordinates must be finite numbers.";
            return false;
        }
        return true;
    }
}
