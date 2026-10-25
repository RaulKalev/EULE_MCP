using System.Globalization;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Mep;

/// <summary>The two containment kinds the MEP routing tools draw (#106).</summary>
public static class MepKind
{
    public const string CableTray = "cableTray";
    public const string Conduit = "conduit";

    /// <summary>"cableTray" or "conduit" for the accepted spellings, null otherwise.</summary>
    public static string? Normalize(string? value)
    {
        var key = (value ?? string.Empty).Trim().Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty)
            .ToLowerInvariant();
        return key switch
        {
            "cabletray" or "tray" or "cabletrays" or "kaabelrenn" => CableTray,
            "conduit" or "conduits" or "kaitsetoru" => Conduit,
            _ => null
        };
    }

    public static string Label(string kind) => kind == CableTray ? "cable tray" : "conduit";
}

/// <summary>Status codes of the MEP routing tools, also used as result statuses.</summary>
public static class MepStatus
{
    public const string ValidationFailed = "validation_failed";
    public const string PolylineInvalid = "polyline_invalid";
    public const string TypeNotFound = "mep_type_not_found";
    public const string RoutingPreferenceMissing = "routing_preference_missing";
    public const string FittingFailed = "fitting_failed";
    public const string SizeNotStandard = "size_not_standard";

    // Per bend / per connection outcomes.
    public const string Created = "created";
    public const string Skipped = "skipped";
    public const string Connected = "connected";
    public const string AlreadyConnected = "already_connected";
    public const string SkewLines = "skew_lines";
    public const string TooFar = "too_far";
    public const string NoCandidate = "no_candidate";
    public const string NoOpenEnd = "no_open_end";
    public const string Failed = "failed";
}

/// <summary>One point of the polyline as given: x and y always, z only when it is not taken from level + offset.</summary>
public sealed class MepPointInput
{
    public double X { get; init; }
    public double Y { get; init; }
    public double? Z { get; init; }
}

/// <summary>How a run end is joined to existing containment.</summary>
public enum MepEndMode
{
    /// <summary>Left open.</summary>
    None,

    /// <summary>Search for an open connector of the same kind within snapMm of the end point.</summary>
    Auto,

    /// <summary>Join to the open connector of the named element nearest the end point.</summary>
    Element
}

public sealed class MepEndConnection
{
    public MepEndMode Mode { get; init; }
    public long ElementId { get; init; }

    public static MepEndConnection None { get; } = new() { Mode = MepEndMode.None };

    public string Describe() => Mode switch
    {
        MepEndMode.Auto => "auto",
        MepEndMode.Element => ElementId.ToString(CultureInfo.InvariantCulture),
        _ => "none"
    };
}

/// <summary>
/// The arguments of revit_preview_create_mep_run / revit_create_mep_run (#106), parsed and checked without Revit.
/// Lengths are millimetres in model coordinates.
/// </summary>
public sealed class MepRunSpec
{
    public const double DefaultSnapMm = 50;
    public const double DefaultMinSegmentMm = 100;
    public const int MaxPoints = 500;
    public const double MaxSnapMm = 5000;

    public string Kind { get; init; } = MepKind.CableTray;

    /// <summary>The curve type: a name or a numeric element id.</summary>
    public string Type { get; init; } = string.Empty;

    public List<MepPointInput> Points { get; init; } = new();

    /// <summary>Level name or numeric id; empty = the level nearest the first point.</summary>
    public string Level { get; init; } = string.Empty;

    /// <summary>Height above the level for points given without z.</summary>
    public double? OffsetMm { get; init; }

    public double? WidthMm { get; init; }
    public double? HeightMm { get; init; }
    public double? DiameterMm { get; init; }

    /// <summary>"auto" (an elbow at every bend) or "none".</summary>
    public string Fittings { get; init; } = "auto";

    public MepEndConnection ConnectStart { get; init; } = MepEndConnection.None;
    public MepEndConnection ConnectEnd { get; init; } = MepEndConnection.None;

    public double SnapMm { get; init; } = DefaultSnapMm;
    public double MinSegmentMm { get; init; } = DefaultMinSegmentMm;

    /// <summary>Workset name or numeric id; empty = the active workset.</summary>
    public string Workset { get; init; } = string.Empty;

    public bool Atomic { get; init; } = true;

    public bool AutoFittings => Fittings == "auto";

    /// <summary>True when some point has no z, so the level elevation and offsetMm decide its height.</summary>
    public bool NeedsLevelElevation => Points.Any(p => !p.Z.HasValue);

    /// <summary>
    /// The points with every missing z filled in as level elevation + offsetMm. Points that carry their own z keep it,
    /// so a riser is drawn by giving the top and bottom points explicit heights.
    /// </summary>
    public List<MepPoint> ResolvePoints(double levelElevationMm) =>
        Points.Select(p => new MepPoint(p.X, p.Y, p.Z ?? levelElevationMm + (OffsetMm ?? 0))).ToList();

    /// <summary>The size label: "300x60" for a tray, "Ø25" for a conduit, null when no size was given.</summary>
    public string? SizeLabel => Kind == MepKind.CableTray
        ? WidthMm.HasValue && HeightMm.HasValue ? $"{MepMath.FormatMm(WidthMm.Value)}x{MepMath.FormatMm(HeightMm.Value)}" : null
        : DiameterMm.HasValue ? $"Ø{MepMath.FormatMm(DiameterMm.Value)}" : null;

    /// <summary>
    /// Reads the arguments. Returns null when they cannot describe a run, with every problem in
    /// <paramref name="errors"/> and the status (validation_failed or polyline_invalid) in <paramref name="status"/>.
    /// </summary>
    public static MepRunSpec? Parse(IDictionary<string, object?> args, List<string> errors, out string? status)
    {
        status = null;
        var polylineErrors = new List<string>();

        var kindText = MepArgs.String(args, "kind");
        var kind = MepKind.Normalize(kindText);
        if (kind == null)
            errors.Add(kindText.Length == 0
                ? "kind is required: 'cableTray' or 'conduit'."
                : $"kind '{kindText}' is not known: use 'cableTray' or 'conduit'.");

        var type = MepArgs.String(args, "type").Trim();
        if (type.Length == 0)
            errors.Add("type is required: a cable tray or conduit type name or id (revit_list_mep_curve_types lists them).");

        var points = ParsePoints(args, polylineErrors);

        var level = MepArgs.String(args, "level").Trim();
        var offset = MepArgs.Double(args, "offsetMm");
        if (points.Any(p => !p.Z.HasValue) && (level.Length == 0 || !offset.HasValue))
            polylineErrors.Add("Some points have no z: give level and offsetMm so every such point gets z = level elevation + offsetMm, " +
                               "or give z on every point.");

        var width = MepArgs.Double(args, "widthMm");
        var height = MepArgs.Double(args, "heightMm");
        var diameter = MepArgs.Double(args, "diameterMm");
        foreach (var (name, value) in new[] { ("widthMm", width), ("heightMm", height), ("diameterMm", diameter) })
            if (value.HasValue && (value.Value <= 0 || !MepMath.IsFinite(value.Value) || value.Value > 10_000))
                errors.Add($"{name} must be a size in mm above 0 and at most 10000 (got {value.Value.ToString(CultureInfo.InvariantCulture)}).");
        if (kind == MepKind.CableTray && diameter.HasValue)
            errors.Add("diameterMm is a conduit size; a cable tray takes widthMm and heightMm.");
        if (kind == MepKind.Conduit && (width.HasValue || height.HasValue))
            errors.Add("widthMm/heightMm are cable tray sizes; a conduit takes diameterMm.");
        if (kind == MepKind.CableTray && width.HasValue != height.HasValue)
            errors.Add("Give both widthMm and heightMm for a cable tray, or neither (Revit's default size is used then).");

        var fittings = MepArgs.String(args, "fittings", "auto").Trim().ToLowerInvariant();
        if (fittings.Length == 0) fittings = "auto";
        if (fittings != "auto" && fittings != "none")
            errors.Add("fittings must be 'auto' (an elbow at every bend) or 'none'.");

        var connectStart = ParseEnd(args, "connectStart", errors);
        var connectEnd = ParseEnd(args, "connectEnd", errors);

        var snap = MepArgs.Double(args, "snapMm") ?? DefaultSnapMm;
        if (snap <= 0 || snap > MaxSnapMm || !MepMath.IsFinite(snap))
            errors.Add($"snapMm must be above 0 and at most {MaxSnapMm.ToString(CultureInfo.InvariantCulture)} mm (default 50).");

        var minSegment = MepArgs.Double(args, "minSegmentMm") ?? DefaultMinSegmentMm;
        if (minSegment < 0 || !MepMath.IsFinite(minSegment))
            errors.Add("minSegmentMm must be 0 or more (default 100).");

        if (errors.Count == 0 && polylineErrors.Count > 0)
            status = MepStatus.PolylineInvalid;
        else if (errors.Count > 0)
            status = MepStatus.ValidationFailed;
        errors.AddRange(polylineErrors);
        if (errors.Count > 0) return null;

        return new MepRunSpec
        {
            Kind = kind!,
            Type = type,
            Points = points,
            Level = level,
            OffsetMm = offset,
            WidthMm = width,
            HeightMm = height,
            DiameterMm = diameter,
            Fittings = fittings,
            ConnectStart = connectStart,
            ConnectEnd = connectEnd,
            SnapMm = snap,
            MinSegmentMm = minSegment,
            Workset = MepArgs.String(args, "workset").Trim(),
            Atomic = MepArgs.Bool(args, "atomic", true)
        };
    }

    private static List<MepPointInput> ParsePoints(IDictionary<string, object?> args, List<string> errors)
    {
        var result = new List<MepPointInput>();
        var token = MepArgs.Token(args, "points");
        if (token is JValue { Type: JTokenType.String } text)
        {
            try { token = JToken.Parse((string?)text ?? string.Empty); }
            catch (Exception ex)
            {
                errors.Add($"points could not be read as a JSON array: {ex.Message}");
                return result;
            }
        }

        if (token is not JArray array)
        {
            errors.Add("points is required: a JSON array of at least 2 points {x, y, z} in mm (model coordinates).");
            return result;
        }

        for (var i = 0; i < array.Count; i++)
        {
            var point = ParsePoint(array[i]);
            if (point == null)
            {
                errors.Add($"points[{i}] is not a point: give {{x, y, z}} or [x, y, z] in mm (z may be left out with level + offsetMm).");
                continue;
            }
            result.Add(point);
        }

        if (array.Count < 2)
            errors.Add($"points needs at least 2 points (got {array.Count}).");
        if (array.Count > MaxPoints)
            errors.Add($"points holds {array.Count} points; at most {MaxPoints} per run.");
        return result;
    }

    private static MepPointInput? ParsePoint(JToken token)
    {
        double? x, y, z;
        switch (token)
        {
            case JObject obj:
                x = MepArgs.Number(obj["x"] ?? obj["X"] ?? obj["xMm"]);
                y = MepArgs.Number(obj["y"] ?? obj["Y"] ?? obj["yMm"]);
                z = MepArgs.Number(obj["z"] ?? obj["Z"] ?? obj["zMm"]);
                break;
            case JArray arr when arr.Count is 2 or 3:
                x = MepArgs.Number(arr[0]);
                y = MepArgs.Number(arr[1]);
                z = arr.Count == 3 ? MepArgs.Number(arr[2]) : null;
                break;
            default:
                return null;
        }

        if (!x.HasValue || !y.HasValue || !MepMath.IsFinite(x.Value) || !MepMath.IsFinite(y.Value)) return null;
        if (z.HasValue && !MepMath.IsFinite(z.Value)) return null;
        return new MepPointInput { X = x.Value, Y = y.Value, Z = z };
    }

    /// <summary>
    /// connectStart / connectEnd: "auto", "none" (or empty), an element id (number or digits), or {"elementId": id}
    /// (also as a JSON string). {"elementId": "auto"} means auto.
    /// </summary>
    internal static MepEndConnection ParseEnd(IDictionary<string, object?> args, string key, List<string> errors)
    {
        var token = MepArgs.Token(args, key);
        if (token == null || token.Type == JTokenType.Null) return MepEndConnection.None;

        if (token is JValue { Type: JTokenType.String } text)
        {
            var value = ((string?)text ?? string.Empty).Trim();
            if (value.StartsWith("{", StringComparison.Ordinal))
            {
                try { token = JToken.Parse(value); }
                catch
                {
                    errors.Add($"{key} could not be read: give 'auto', 'none', an element id or {{\"elementId\": id}}.");
                    return MepEndConnection.None;
                }
            }
            else
            {
                return FromScalar(value, key, errors);
            }
        }

        if (token is JObject obj)
        {
            var id = obj["elementId"] ?? obj["id"];
            if (id == null || id.Type == JTokenType.Null)
            {
                errors.Add($"{key} is an object without elementId: give {{\"elementId\": id}}.");
                return MepEndConnection.None;
            }
            return FromScalar(id.Type == JTokenType.String ? ((string?)id ?? string.Empty).Trim() : id.ToString(), key, errors);
        }

        if (token.Type is JTokenType.Integer or JTokenType.Float)
            return FromScalar(token.ToString(), key, errors);
        if (token.Type == JTokenType.Boolean)
            return (bool)token ? new MepEndConnection { Mode = MepEndMode.Auto } : MepEndConnection.None;

        errors.Add($"{key} could not be read: give 'auto', 'none', an element id or {{\"elementId\": id}}.");
        return MepEndConnection.None;
    }

    private static MepEndConnection FromScalar(string value, string key, List<string> errors)
    {
        if (value.Length == 0 || value.Equals("none", StringComparison.OrdinalIgnoreCase) || value.Equals("false", StringComparison.OrdinalIgnoreCase))
            return MepEndConnection.None;
        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase))
            return new MepEndConnection { Mode = MepEndMode.Auto };
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0)
            return new MepEndConnection { Mode = MepEndMode.Element, ElementId = id };
        errors.Add($"{key} '{value}' is not 'auto', 'none' or an element id.");
        return MepEndConnection.None;
    }
}

/// <summary>Argument reading for the pure MEP logic: values arrive as JSON tokens through the pipe, or as plain values in tests.</summary>
internal static class MepArgs
{
    public static JToken? Token(IDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value == null) return null;
        if (value is JToken token) return token;
        try { return JToken.FromObject(value); }
        catch { return null; }
    }

    public static string String(IDictionary<string, object?> args, string key, string fallback = "")
    {
        var token = Token(args, key);
        if (token == null || token.Type == JTokenType.Null) return fallback;
        return token.Type == JTokenType.String ? (string?)token ?? fallback : token.ToString();
    }

    public static double? Double(IDictionary<string, object?> args, string key) => Number(Token(args, key));

    public static bool Bool(IDictionary<string, object?> args, string key, bool fallback)
    {
        var token = Token(args, key);
        if (token == null || token.Type == JTokenType.Null) return fallback;
        if (token.Type == JTokenType.Boolean) return (bool)token;
        var text = token.ToString().Trim();
        return bool.TryParse(text, out var parsed) ? parsed : fallback;
    }

    public static long Long(IDictionary<string, object?> args, string key)
    {
        var number = Number(Token(args, key));
        return number.HasValue ? (long)Math.Round(number.Value) : 0;
    }

    public static string[] Strings(IDictionary<string, object?> args, string key)
    {
        var token = Token(args, key);
        if (token is JValue { Type: JTokenType.String } text)
        {
            var value = ((string?)text ?? string.Empty).Trim();
            if (value.StartsWith("[", StringComparison.Ordinal))
            {
                try { token = JToken.Parse(value); }
                catch { return new[] { value }; }
            }
            else
            {
                return value.Length == 0
                    ? Array.Empty<string>()
                    : value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            }
        }
        if (token is not JArray array) return Array.Empty<string>();
        return array.Where(t => t.Type != JTokenType.Null)
            .Select(t => t.Type == JTokenType.String ? ((string?)t ?? string.Empty).Trim() : t.ToString())
            .Where(s => s.Length > 0).ToArray();
    }

    public static double? Number(JToken? token)
    {
        if (token == null) return null;
        switch (token.Type)
        {
            case JTokenType.Integer:
            case JTokenType.Float:
                return token.Value<double>();
            case JTokenType.String:
                var text = ((string?)token ?? string.Empty).Trim();
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
            default:
                return null;
        }
    }
}
