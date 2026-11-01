using System.Globalization;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Query;

namespace RevitMCP.Addin.FilledRegions;

/// <summary>Status codes of the filled region tools (docs/filled-regions.md).</summary>
public static class RegionCodes
{
    public const string InvalidSpec = "invalid_spec";
    public const string ViewNotFound = "view_not_found";
    public const string ViewNotSupported = "view_not_supported";
    public const string TypeNotFound = "type_not_found";
    public const string LineStyleNotFound = "line_style_not_found";
    public const string LoopEmpty = "loop_empty";
    public const string LoopOpen = "loop_open";
    public const string LoopBranching = "loop_branching";
    public const string LoopTooFewPoints = "loop_too_few_points";
    public const string LoopSegmentTooShort = "loop_segment_too_short";
    public const string LoopSelfIntersecting = "loop_self_intersecting";
    public const string LoopDegenerate = "loop_degenerate";
    public const string LoopsIntersect = "loops_intersect";
    public const string HoleOutsideOuter = "hole_outside_outer";
    public const string PaddingCollapsed = "padding_collapsed";
    public const string CoordinateZRequired = "coordinate_z_required";
    public const string BoundaryNotInViewPlane = "boundary_not_in_view_plane";
    public const string RoomNotFound = "room_not_found";
    public const string RoomNotBounded = "room_not_bounded";
    public const string RoomAmbiguous = "room_ambiguous";
    public const string LinkNotFound = "link_not_found";
    public const string LineNotFound = "line_not_found";
    public const string ElementNotFound = "element_not_found";
    public const string NoBoundingBox = "no_bounding_box";
    public const string NoRoomBoundary = "no_room_boundary";
    public const string CategoryNotFound = "category_not_found";
    public const string ParameterNotFound = "parameter_not_found";
    public const string ValueEmpty = "value_empty";
    public const string ValueNotMapped = "value_not_mapped";
    public const string RegionNotFound = "region_not_found";
    public const string NotAFilledRegion = "not_a_filled_region";
    public const string RegionInGroup = "region_in_group";
    public const string RegionOwnedByOther = "region_owned_by_other";
    public const string ParameterNotSettable = "parameter_not_settable";
    public const string TypeNameExists = "type_name_exists";
    public const string PatternNotFound = "pattern_not_found";
    public const string PatternNotValid = "pattern_not_valid";
    public const string LineWeightNotValid = "line_weight_not_valid";
    public const string MaskingNotValid = "masking_not_valid";
    public const string RevitRefused = "revit_refused";
    public const string TooManyRegions = "too_many_regions";

    /// <summary>A code about one source of a rule, not about the rule: the source is skipped and the rest goes on.</summary>
    public static bool IsSourceProblem(string? code) => code is
        LoopEmpty or LoopOpen or LoopBranching or LoopTooFewPoints or LoopSegmentTooShort or LoopSelfIntersecting or LoopDegenerate or
        LoopsIntersect or HoleOutsideOuter or PaddingCollapsed or BoundaryNotInViewPlane or RoomNotFound or RoomNotBounded or
        ElementNotFound or NoBoundingBox or NoRoomBoundary;
}

/// <summary>What happens or happened to one region, type or edit.</summary>
public static class RegionStatus
{
    public const string WillCreate = "willCreate";
    public const string WillEdit = "willEdit";
    public const string Invalid = "invalid";
    public const string Skipped = "skipped";
    public const string Created = "created";
    public const string Edited = "edited";
    public const string Failed = "failed";
    public const string RolledBack = "rolledBack";
    public const string NotAttempted = "notAttempted";
}

public enum RegionShapeKind
{
    None,
    Loops,
    Rectangle,
    Room,
    DetailLines,
    ElementBox
}

/// <summary>A point of a spec: model mm (x, y, z) or view plane mm (u, v), and for a loop point the arc to the next point.</summary>
public sealed class ShapePoint
{
    public double? X { get; init; }
    public double? Y { get; init; }
    public double? Z { get; init; }
    public double? U { get; init; }
    public double? V { get; init; }

    /// <summary>The segment from this point to the next is an arc through this point; null = a straight line.</summary>
    public ShapePoint? ArcMid { get; init; }
}

/// <summary>Which of the four input kinds a region's shape is, with its data. Used by create specs, rules and reshapes.</summary>
public sealed class RegionShapeSpec
{
    public RegionShapeKind Kind { get; set; }

    /// <summary>false (default): points are model mm {x, y, z}. true: view plane mm {u, v}.</summary>
    public bool ViewSpace { get; set; }

    public List<List<ShapePoint>> Loops { get; } = new();
    public bool CloseLoops { get; set; }

    public ShapePoint? From { get; set; }
    public ShapePoint? To { get; set; }
    public ShapePoint? Center { get; set; }
    public double? WidthMm { get; set; }
    public double? HeightMm { get; set; }

    public long RoomId { get; set; }
    public string? RoomNumber { get; set; }
    public string? RoomLevel { get; set; }
    public long LinkInstanceId { get; set; }
    public string BoundaryLocation { get; set; } = "finish";

    public List<long> DetailLineIds { get; } = new();
    public long ElementId { get; set; }

    public double PaddingMm { get; set; }
}

/// <summary>One region to create: given directly, or expanded from a rule.</summary>
public sealed class RegionSpec
{
    public int Index { get; set; }
    public string? View { get; set; }
    public string? Type { get; set; }
    public string? LineStyle { get; set; }
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public RegionShapeSpec Shape { get; set; } = new();
    public List<string> Errors { get; } = new();

    /// <summary>Set for a region a rule made: the rule's index, and the source it came from.</summary>
    public int? RuleIndex { get; set; }
    public long? SourceId { get; set; }
    public string? SourceLabel { get; set; }
}

/// <summary>A whole revit_create_filled_regions request.</summary>
public sealed class RegionCreateRequest
{
    public List<RegionSpec> Regions { get; } = new();
    public List<RegionRule> Rules { get; } = new();
    public bool Atomic { get; set; }
    public bool IncludeLoops { get; set; }
}

/// <summary>
/// Reads the arguments of the filled region tools. Pure: JSON in, specs out, so the same code serves the preview, the
/// write and the approval summary. A problem in one spec lands in that spec's Errors; only a request that cannot be read
/// at all returns null.
/// </summary>
public static class FilledRegionSpecParser
{
    public const int MaxRegions = 500;

    public static RegionCreateRequest? ParseCreate(IDictionary<string, object?> args, out string? error)
    {
        error = null;
        var request = new RegionCreateRequest
        {
            Atomic = ReadBool(args, "atomic"),
            IncludeLoops = ReadBool(args, "includeLoops")
        };

        var regions = ReadArray(args, "regions", out var regionsError);
        var rules = ReadArray(args, "rules", out var rulesError);
        if (regionsError != null || rulesError != null)
        {
            error = regionsError ?? rulesError;
            return null;
        }
        if ((regions == null || regions.Count == 0) && (rules == null || rules.Count == 0))
        {
            error = "Provide 'regions' (direct specs), 'rules', or both.";
            return null;
        }
        if (regions != null && regions.Count > MaxRegions)
        {
            error = $"{regions.Count} regions were given; the limit is {MaxRegions} per call.";
            return null;
        }

        var index = 0;
        foreach (var token in regions ?? new JArray())
            request.Regions.Add(ParseRegion(token, index++));
        index = 0;
        foreach (var token in rules ?? new JArray())
            request.Rules.Add(ParseRule(token, index++));
        return request;
    }

    public static RegionSpec ParseRegion(JToken token, int index)
    {
        var spec = new RegionSpec { Index = index };
        if (token is not JObject o)
        {
            spec.Errors.Add("A region spec must be an object.");
            return spec;
        }

        spec.View = Text(o["view"]) ?? Text(o["viewId"]);
        if (string.IsNullOrWhiteSpace(spec.View)) spec.Errors.Add("'view' (name or id) is required.");
        spec.Type = Text(o["type"]);
        if (string.IsNullOrWhiteSpace(spec.Type)) spec.Errors.Add("'type' (filled region type name or id) is required.");
        spec.LineStyle = Text(o["lineStyle"]);
        spec.Parameters = ParseParameters(o["parameters"], spec.Errors);
        spec.Shape = ParseShape(o, spec.Errors);
        if (spec.Shape.Kind == RegionShapeKind.None && !spec.Errors.Any(e => e.Contains("shape")))
            spec.Errors.Add("Give one shape: 'loops', 'rectangle', 'room', 'detailLines' or 'element'.");
        return spec;
    }

    /// <summary>The shape fields of an object: a region spec, or the 'boundary' of an edit.</summary>
    public static RegionShapeSpec ParseShape(JObject o, List<string> errors)
    {
        var shape = new RegionShapeSpec
        {
            CloseLoops = o["closeLoops"]?.Type == JTokenType.Boolean && o.Value<bool>("closeLoops"),
            PaddingMm = Number(o["paddingMm"]) ?? 0
        };

        var space = Text(o["space"])?.Trim().ToLowerInvariant();
        if (space == "view") shape.ViewSpace = true;
        else if (!string.IsNullOrEmpty(space) && space != "model") errors.Add($"space must be 'model' or 'view' (got '{space}').");

        var kinds = new[] { "loops", "rectangle", "room", "detailLines", "element" }.Where(k => o[k] != null && o[k]!.Type != JTokenType.Null).ToList();
        if (kinds.Count > 1)
        {
            errors.Add($"Give one shape only; got {string.Join(", ", kinds)}.");
            return shape;
        }
        if (kinds.Count == 0) return shape;

        switch (kinds[0])
        {
            case "loops": ParseLoops(o["loops"]!, shape, errors); break;
            case "rectangle": ParseRectangle(o["rectangle"]!, shape, errors); break;
            case "room": ParseRoom(o["room"]!, o, shape, errors); break;
            case "detailLines": ParseDetailLines(o["detailLines"]!, shape, errors); break;
            default: ParseElement(o["element"]!, shape, errors); break;
        }
        return shape;
    }

    private static void ParseLoops(JToken token, RegionShapeSpec shape, List<string> errors)
    {
        shape.Kind = RegionShapeKind.Loops;
        if (token is not JArray loops || loops.Count == 0)
        {
            errors.Add("'loops' must be a non-empty array of point arrays: the outline, then any holes.");
            return;
        }
        // A single flat list of points is one loop, not a list of loops.
        var loopTokens = loops[0] is JArray ? loops.ToList() : new List<JToken> { loops };
        foreach (var loopToken in loopTokens)
        {
            if (loopToken is not JArray points)
            {
                errors.Add("Each loop must be an array of points.");
                return;
            }
            var loop = new List<ShapePoint>();
            foreach (var pointToken in points)
            {
                var point = ParsePoint(pointToken, shape.ViewSpace, errors);
                if (point == null) return;
                loop.Add(point);
            }
            shape.Loops.Add(loop);
        }
    }

    private static void ParseRectangle(JToken token, RegionShapeSpec shape, List<string> errors)
    {
        shape.Kind = RegionShapeKind.Rectangle;
        if (token is not JObject r)
        {
            errors.Add("'rectangle' must be {from, to} or {center, widthMm, heightMm}.");
            return;
        }
        if (r["from"] != null || r["to"] != null)
        {
            shape.From = ParsePoint(r["from"], shape.ViewSpace, errors);
            shape.To = ParsePoint(r["to"], shape.ViewSpace, errors);
            return;
        }
        shape.Center = ParsePoint(r["center"], shape.ViewSpace, errors);
        shape.WidthMm = Number(r["widthMm"]);
        shape.HeightMm = Number(r["heightMm"]);
        if (shape.WidthMm is not > 0 || shape.HeightMm is not > 0)
            errors.Add("rectangle {center, widthMm, heightMm}: widthMm and heightMm must be positive.");
    }

    private static void ParseRoom(JToken token, JObject owner, RegionShapeSpec shape, List<string> errors)
    {
        shape.Kind = RegionShapeKind.Room;
        shape.LinkInstanceId = Long(owner["linkInstanceId"]);
        var location = Text(owner["boundaryLocation"]);
        if (!string.IsNullOrWhiteSpace(location)) shape.BoundaryLocation = location!.Trim();

        if (token is JObject r)
        {
            shape.RoomId = Long(r["id"]);
            shape.RoomNumber = Text(r["number"]);
            shape.RoomLevel = Text(r["level"]);
            if (r["linkInstanceId"] != null) shape.LinkInstanceId = Long(r["linkInstanceId"]);
        }
        else
        {
            shape.RoomId = Long(token);
        }
        if (shape.RoomId == 0 && string.IsNullOrWhiteSpace(shape.RoomNumber))
            errors.Add("'room' must be an id, {id, linkInstanceId} or {number, level, linkInstanceId}.");

        if (!IsBoundaryLocation(shape.BoundaryLocation))
            errors.Add($"boundaryLocation must be finish, center, coreCenter or coreBoundary (got '{shape.BoundaryLocation}').");
    }

    public static bool IsBoundaryLocation(string location) =>
        location.Equals("finish", StringComparison.OrdinalIgnoreCase) || location.Equals("center", StringComparison.OrdinalIgnoreCase) ||
        location.Equals("coreCenter", StringComparison.OrdinalIgnoreCase) || location.Equals("coreBoundary", StringComparison.OrdinalIgnoreCase);

    private static void ParseDetailLines(JToken token, RegionShapeSpec shape, List<string> errors)
    {
        shape.Kind = RegionShapeKind.DetailLines;
        if (token is JArray ids) shape.DetailLineIds.AddRange(ids.Select(Long).Where(id => id != 0));
        if (shape.DetailLineIds.Count == 0) errors.Add("'detailLines' must be a non-empty array of line element ids.");
    }

    private static void ParseElement(JToken token, RegionShapeSpec shape, List<string> errors)
    {
        shape.Kind = RegionShapeKind.ElementBox;
        if (token is JObject e)
        {
            shape.ElementId = Long(e["id"]);
            shape.LinkInstanceId = Long(e["linkInstanceId"]);
        }
        else
        {
            shape.ElementId = Long(token);
        }
        if (shape.ElementId == 0) errors.Add("'element' must be an id or {id, linkInstanceId}.");
    }

    public static ShapePoint? ParsePoint(JToken? token, bool viewSpace, List<string> errors)
    {
        if (token is not JObject p)
        {
            errors.Add(viewSpace ? "A point must be {u, v} in mm." : "A point must be {x, y, z} in mm.");
            return null;
        }
        ShapePoint? arcMid = null;
        if (p["arcMid"] is { Type: not JTokenType.Null } arcToken)
        {
            arcMid = ParsePoint(arcToken, viewSpace, errors);
            if (arcMid == null) return null;
        }
        if (viewSpace)
        {
            var u = Number(p["u"]);
            var v = Number(p["v"]);
            if (u == null || v == null)
            {
                errors.Add("space=view: a point must be {u, v} in mm.");
                return null;
            }
            return new ShapePoint { U = u, V = v, ArcMid = arcMid };
        }

        var x = Number(p["x"]);
        var y = Number(p["y"]);
        if (x == null || y == null)
        {
            errors.Add("A model point must have x and y in mm (and z outside plan-like views). For view plane coordinates pass space=\"view\" and {u, v}.");
            return null;
        }
        return new ShapePoint { X = x, Y = y, Z = Number(p["z"]), ArcMid = arcMid };
    }

    // ── Rules ─────────────────────────────────────────────────────────────────

    public static RegionRule ParseRule(JToken token, int index)
    {
        var rule = new RegionRule { Index = index };
        if (token is not JObject o)
        {
            rule.Errors.Add("A rule must be an object.");
            return rule;
        }

        rule.View = Text(o["view"]) ?? Text(o["viewId"]);
        if (string.IsNullOrWhiteSpace(rule.View)) rule.Errors.Add("'view' (name or id) is required.");
        rule.LineStyle = Text(o["lineStyle"]);
        rule.Parameters = ParseParameters(o["parameters"], rule.Errors);

        if (o["source"] is JObject source)
        {
            var kind = (Text(source["kind"]) ?? "rooms").Trim().ToLowerInvariant();
            if (kind is "rooms" or "room") rule.SourceKind = RuleSourceKind.Rooms;
            else if (kind is "elements" or "element") rule.SourceKind = RuleSourceKind.Elements;
            else rule.Errors.Add($"source.kind must be 'rooms' or 'elements' (got '{kind}').");

            rule.Categories.AddRange(Strings(source["categories"]));
            if (Text(source["category"]) is { Length: > 0 } one) rule.Categories.Add(one);
            rule.InView = Text(source["inView"]);
            rule.Level = Text(source["level"]);
            rule.LinkInstanceId = Long(source["linkInstanceId"]);
            if (source["elementIds"] is JArray ids) rule.ElementIds.AddRange(ids.Select(Long).Where(id => id != 0));
            rule.Limit = (int)Math.Max(1, Math.Min(MaxRegions, Number(source["limit"]) ?? MaxRegions));
            if (source["filters"] is JArray filters)
                foreach (var filter in filters)
                    rule.Filters.Add(new ParameterFilterDto
                    {
                        ParameterName = Text(filter["parameterName"]) ?? string.Empty,
                        MatchMode = Text(filter["matchMode"]) ?? "ContainsNormalized",
                        Operator = Text(filter["operator"]) ?? "equals",
                        Value = Text(filter["value"]) ?? string.Empty,
                        Scope = Text(filter["scope"]) ?? "InstanceAndType"
                    });
            if (rule.Filters.Any(f => f.ParameterName.Length == 0)) rule.Errors.Add("Each source filter needs parameterName.");
            if (rule.SourceKind == RuleSourceKind.Elements && rule.Categories.Count == 0 && rule.ElementIds.Count == 0 && string.IsNullOrWhiteSpace(rule.InView))
                rule.Errors.Add("source.kind=elements needs categories, elementIds or inView, so the rule does not take every element of the model.");
        }
        else
        {
            rule.Errors.Add("'source' is required: {kind, categories, filters, inView, level, linkInstanceId, elementIds}.");
        }

        var shape = o["shape"] as JObject;
        var shapeKind = (Text(shape?["kind"]) ?? (rule.SourceKind == RuleSourceKind.Rooms ? "roomBoundary" : "boundingBox")).Trim().ToLowerInvariant();
        if (shapeKind == "roomboundary") rule.ShapeKind = RegionShapeKind.Room;
        else if (shapeKind == "boundingbox") rule.ShapeKind = RegionShapeKind.ElementBox;
        else rule.Errors.Add($"shape.kind must be 'roomBoundary' or 'boundingBox' (got '{shapeKind}').");
        rule.PaddingMm = Number(shape?["paddingMm"]) ?? 0;
        var location = Text(shape?["boundaryLocation"]);
        if (!string.IsNullOrWhiteSpace(location))
        {
            rule.BoundaryLocation = location!.Trim();
            if (!IsBoundaryLocation(rule.BoundaryLocation))
                rule.Errors.Add($"shape.boundaryLocation must be finish, center, coreCenter or coreBoundary (got '{rule.BoundaryLocation}').");
        }

        rule.Type = Text(o["type"]);
        if (o["typeMap"] is JObject map)
        {
            rule.TypeMap = TypeMap.Parse(map, rule.Errors);
            if (!string.IsNullOrWhiteSpace(rule.Type)) rule.Errors.Add("Give 'type' or 'typeMap', not both.");
        }
        else if (string.IsNullOrWhiteSpace(rule.Type))
        {
            rule.Errors.Add("Give 'type' (one type for every source) or 'typeMap' {parameter, map}.");
        }
        return rule;
    }

    // ── Shared readers ────────────────────────────────────────────────────────

    public static Dictionary<string, string> ParseParameters(JToken? token, List<string> errors)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (token == null || token.Type == JTokenType.Null) return result;
        if (token is not JObject o)
        {
            errors.Add("'parameters' must be an object {name: value}.");
            return result;
        }
        foreach (var property in o.Properties())
            result[property.Name] = Text(property.Value) ?? string.Empty;
        return result;
    }

    public static string? Text(JToken? token)
    {
        if (token == null || token.Type == JTokenType.Null) return null;
        if (token.Type == JTokenType.Float) return token.Value<double>().ToString("0.####", CultureInfo.InvariantCulture);
        return token.Type is JTokenType.Object or JTokenType.Array ? null : token.ToString();
    }

    public static double? Number(JToken? token)
    {
        if (token == null) return null;
        if (token.Type is JTokenType.Integer or JTokenType.Float) return token.Value<double>();
        return token.Type == JTokenType.String && double.TryParse((string?)token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    public static long Long(JToken? token)
    {
        if (token == null) return 0;
        if (token.Type == JTokenType.Integer) return token.Value<long>();
        return token.Type == JTokenType.String && long.TryParse((string?)token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private static IEnumerable<string> Strings(JToken? token) =>
        token is JArray array ? array.Select(Text).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!) : Enumerable.Empty<string>();

    public static bool ReadBool(IDictionary<string, object?> args, string key, bool defaultValue = false)
    {
        if (!args.TryGetValue(key, out var value) || value == null) return defaultValue;
        return value switch
        {
            bool b => b,
            JValue { Type: JTokenType.Boolean } j => j.Value<bool>(),
            _ => bool.TryParse(value.ToString(), out var parsed) ? parsed : defaultValue
        };
    }

    /// <summary>An array argument: a JArray, or a JSON string holding one. Null when absent.</summary>
    public static JArray? ReadArray(IDictionary<string, object?> args, string key, out string? error)
    {
        error = null;
        if (!args.TryGetValue(key, out var value) || value == null) return null;
        switch (value)
        {
            case JArray array:
                return array;
            case JValue { Type: JTokenType.Null }:
                return null;
            default:
                var text = value is JValue j ? j.ToString(CultureInfo.InvariantCulture) : value.ToString() ?? string.Empty;
                if (text.Trim().Length == 0) return null;
                try
                {
                    return JArray.Parse(text);
                }
                catch (Exception ex) when (ex is Newtonsoft.Json.JsonException)
                {
                    error = $"'{key}' is not a JSON array: {ex.Message}";
                    return null;
                }
        }
    }
}
