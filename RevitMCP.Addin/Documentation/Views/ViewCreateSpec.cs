using System.Globalization;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Documentation.Views;

/// <summary>A point or vector in millimetres (host model internal coordinates). No Revit types.</summary>
public readonly struct Vec3
{
    public Vec3(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public double X { get; }
    public double Y { get; }
    public double Z { get; }

    public static readonly Vec3 Zero = new(0, 0, 0);
    public static readonly Vec3 UnitZ = new(0, 0, 1);

    public Vec3 Plus(Vec3 o) => new(X + o.X, Y + o.Y, Z + o.Z);
    public Vec3 Minus(Vec3 o) => new(X - o.X, Y - o.Y, Z - o.Z);
    public Vec3 Scaled(double k) => new(X * k, Y * k, Z * k);
    public Vec3 Negated() => new(-X, -Y, -Z);
    public double Dot(Vec3 o) => X * o.X + Y * o.Y + Z * o.Z;
    public Vec3 Cross(Vec3 o) => new(Y * o.Z - Z * o.Y, Z * o.X - X * o.Z, X * o.Y - Y * o.X);
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public Vec3 Normalized() => Length < 1e-12 ? Zero : Scaled(1.0 / Length);

    /// <summary>Rounded to 0.1 mm, for results.</summary>
    public object ToMm() => new { x = Math.Round(X, 1), y = Math.Round(Y, 1), z = Math.Round(Z, 1) };

    /// <summary>Rounded to 6 decimals, for unit vectors in results.</summary>
    public object ToUnit() => new { x = Math.Round(X, 6), y = Math.Round(Y, 6), z = Math.Round(Z, 6) };

    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "({0:0.#}, {1:0.#}, {2:0.#})", X, Y, Z);
}

/// <summary>An axis-aligned box in model millimetres.</summary>
public sealed class BoxMm
{
    public BoxMm(Vec3 min, Vec3 max)
    {
        Min = min;
        Max = max;
    }

    public Vec3 Min { get; }
    public Vec3 Max { get; }

    public Vec3 Center => new((Min.X + Max.X) / 2, (Min.Y + Max.Y) / 2, (Min.Z + Max.Z) / 2);
    public Vec3 Size => Max.Minus(Min);

    public object ToMm() => new { min = Min.ToMm(), max = Max.ToMm() };
}

/// <summary>The view kinds revit_create_views understands.</summary>
public static class ViewKinds
{
    public const string FloorPlan = "floorPlan";
    public const string CeilingPlan = "ceilingPlan";
    public const string StructuralPlan = "structuralPlan";
    public const string AreaPlan = "areaPlan";
    public const string Section = "section";
    public const string Callout = "callout";
    public const string ThreeD = "threeD";
    public const string Drafting = "drafting";
    public const string Legend = "legend";

    public static readonly string[] All =
        { FloorPlan, CeilingPlan, StructuralPlan, AreaPlan, Section, Callout, ThreeD, Drafting, Legend };

    /// <summary>The canonical kind for a spelling (case and separators ignored, a few aliases), or null.</summary>
    public static string? Normalize(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return null;
        var key = new string(kind!.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return key switch
        {
            "floorplan" or "plan" => FloorPlan,
            "ceilingplan" or "rcp" or "reflectedceilingplan" => CeilingPlan,
            "structuralplan" or "engineeringplan" => StructuralPlan,
            "areaplan" => AreaPlan,
            "section" => Section,
            "callout" or "detailcallout" => Callout,
            "threed" or "3d" or "view3d" => ThreeD,
            "drafting" or "draftingview" => Drafting,
            "legend" => Legend,
            _ => null
        };
    }

    public static bool IsPlan(string kind) => kind is FloorPlan or CeilingPlan or StructuralPlan or AreaPlan;

    /// <summary>A plural label for the approval summary and messages.</summary>
    public static string Label(string kind, int count) => kind switch
    {
        FloorPlan => count == 1 ? "floor plan" : "floor plans",
        CeilingPlan => count == 1 ? "ceiling plan" : "ceiling plans",
        StructuralPlan => count == 1 ? "structural plan" : "structural plans",
        AreaPlan => count == 1 ? "area plan" : "area plans",
        Section => count == 1 ? "section" : "sections",
        Callout => count == 1 ? "callout" : "callouts",
        ThreeD => count == 1 ? "3D view" : "3D views",
        Drafting => count == 1 ? "drafting view" : "drafting views",
        Legend => count == 1 ? "legend" : "legends",
        _ => count == 1 ? "view" : "views"
    };
}

/// <summary>Machine-readable per-view status codes (also used as McpToolResult.Status when nothing can be made).</summary>
public static class ViewCreateCodes
{
    public const string InvalidSpec = "invalid_spec";
    public const string LevelNotFound = "level_not_found";
    public const string ViewNameExists = "view_name_exists";
    public const string ViewFamilyTypeIncompatible = "view_family_type_incompatible";
    public const string ViewFamilyTypeNotFound = "view_family_type_not_found";
    public const string LegendSourceRequired = "legend_source_required";
    public const string TemplateNotFound = "template_not_found";
    public const string ScopeBoxNotFound = "scope_box_not_found";
    public const string SheetNotFound = "sheet_not_found";
    public const string ParentViewNotFound = "parent_view_not_found";
    public const string AreaSchemeRequired = "area_scheme_required";
    public const string PhaseNotFound = "phase_not_found";
    public const string ExtentNotFound = "extent_not_found";
    public const string RevitRefused = "revit_refused";
}

/// <summary>Per-view outcome names shared by the preview and the write tool.</summary>
public static class ViewCreateStatus
{
    public const string WillCreate = "willCreate";
    public const string Skipped = "skipped";
    public const string Invalid = "invalid";
    public const string Created = "created";
    public const string Failed = "failed";
    public const string RolledBack = "rolledBack";
    public const string NotAttempted = "notAttempted";
}

/// <summary>What to do when the requested name is taken.</summary>
public enum NameConflictMode
{
    Suffix,
    Skip,
    Fail
}

/// <summary>Where a view's extent comes from: a box, elements, a room or a scope box, plus padding.</summary>
public sealed class RegionSpec
{
    public BoxMm? Box { get; set; }
    public List<long> ElementIds { get; set; } = new();
    public long? RoomId { get; set; }

    /// <summary>The link the room lives in; 0 = host model.</summary>
    public long RoomLinkInstanceId { get; set; }

    /// <summary>Scope box name or id (set_view_extent and cropTo only).</summary>
    public string? ScopeBox { get; set; }

    public double? PaddingMm { get; set; }

    public bool IsEmpty => Box == null && ElementIds.Count == 0 && RoomId == null && string.IsNullOrWhiteSpace(ScopeBox);

    /// <summary>How many sources were given (exactly one is expected).</summary>
    public int SourceCount =>
        (Box != null ? 1 : 0) + (ElementIds.Count > 0 ? 1 : 0) + (RoomId != null ? 1 : 0) + (string.IsNullOrWhiteSpace(ScopeBox) ? 0 : 1);

    public string Describe()
    {
        if (Box != null) return $"box {Box.Min}–{Box.Max} mm";
        if (ElementIds.Count > 0) return $"{ElementIds.Count} element(s)";
        if (RoomId != null) return RoomLinkInstanceId != 0 ? $"room {RoomId} in link {RoomLinkInstanceId}" : $"room {RoomId}";
        if (!string.IsNullOrWhiteSpace(ScopeBox)) return $"scope box '{ScopeBox}'";
        return "nothing";
    }
}

/// <summary>Plan view range offsets in mm; each plane keeps the level it is measured from.</summary>
public sealed class ViewRangeSpec
{
    public double? TopOffsetMm { get; set; }
    public double? CutOffsetMm { get; set; }
    public double? BottomOffsetMm { get; set; }
    public double? DepthOffsetMm { get; set; }

    public bool IsEmpty => TopOffsetMm == null && CutOffsetMm == null && BottomOffsetMm == null && DepthOffsetMm == null;
}

/// <summary>A 3D view orientation: a preset name or eye/forward/up.</summary>
public sealed class OrientationSpec
{
    public string? Preset { get; set; }
    public Vec3? EyeMm { get; set; }
    public Vec3? Forward { get; set; }
    public Vec3? Up { get; set; }
}

/// <summary>Where to put the new view: a sheet (number, name or id) and a point on it in mm.</summary>
public sealed class SheetPlacementSpec
{
    /// <summary>Sheet number, name or id. May contain {level} and {n}.</summary>
    public string? Sheet { get; set; }

    /// <summary>One sheet per fanned-out level, in the order of the levels.</summary>
    public List<string> Sheets { get; set; } = new();

    public double? XMm { get; set; }
    public double? YMm { get; set; }
}

/// <summary>One entry of <c>views[]</c> as it arrived.</summary>
public sealed class ViewCreateSpec
{
    public int Index { get; set; }
    public string RawKind { get; set; } = string.Empty;
    public string? Kind { get; set; }

    // Common part
    public string? Name { get; set; }
    public string? ViewFamilyType { get; set; }
    public string? Template { get; set; }
    public int? Scale { get; set; }
    public string? DetailLevel { get; set; }
    public string? Discipline { get; set; }
    public string? Phase { get; set; }
    public string? ScopeBox { get; set; }
    public SheetPlacementSpec? PlaceOnSheet { get; set; }

    // Plans
    public string? Level { get; set; }
    public List<string> Levels { get; set; } = new();
    public bool AllLevels { get; set; }
    public string? AreaScheme { get; set; }
    public ViewRangeSpec? ViewRange { get; set; }
    public RegionSpec? CropTo { get; set; }

    // Section and callout corners
    public Vec3? From { get; set; }
    public Vec3? To { get; set; }
    public double DepthMm { get; set; } = SectionBoxMath.DefaultDepthMm;
    public double? HeightMm { get; set; }
    public double? BottomMm { get; set; }
    public bool Flip { get; set; }

    // Callout
    public string? ParentView { get; set; }
    public string? ReferenceView { get; set; }

    /// <summary>Callout and 3D: box, aroundElements or aroundRoom, with paddingMm.</summary>
    public RegionSpec? Region { get; set; }

    // 3D
    public bool Isometric { get; set; } = true;
    public OrientationSpec? Orientation { get; set; }

    // Legend
    public string? SourceLegend { get; set; }

    /// <summary>Problems found while reading the entry.</summary>
    public List<string> Errors { get; } = new();
}

/// <summary>The whole revit_create_views request.</summary>
public sealed class ViewCreateRequest
{
    public List<ViewCreateSpec> Views { get; set; } = new();
    public NameConflictMode IfNameExists { get; set; } = NameConflictMode.Suffix;
    public bool Atomic { get; set; }
}

/// <summary>
/// Reads revit_create_views arguments and the revit_set_view_extent region (#104). Pure: JSON in, DTOs out, so the
/// specs are unit tested without Revit. References (levels, types, templates, sheets, …) stay strings: a name, or an
/// element id written as a number.
/// </summary>
public static class ViewCreateSpecParser
{
    /// <summary>Upper bound on the views one call may create, after fanning out per level.</summary>
    public const int MaxViews = 100;

    public static ViewCreateRequest? Parse(Dictionary<string, object?> arguments, out string? error)
    {
        error = null;
        arguments.TryGetValue("views", out var raw);
        var array = ToArray(raw);
        if (array == null)
        {
            error = "'views' is required: an array of view specs, each with a 'kind' (" + string.Join(", ", ViewKinds.All) + ").";
            return null;
        }
        if (array.Count == 0)
        {
            error = "'views' is empty: give at least one view spec.";
            return null;
        }
        if (array.Count > MaxViews)
        {
            error = $"'views' holds {array.Count} specs; the limit is {MaxViews} views per call. Split the request.";
            return null;
        }

        var mode = ReadString(arguments, "ifNameExists");
        if (!TryParseMode(mode, out var conflictMode))
        {
            error = $"ifNameExists must be suffix, skip or fail (got '{mode}').";
            return null;
        }

        var request = new ViewCreateRequest
        {
            IfNameExists = conflictMode,
            Atomic = ReadBool(arguments, "atomic", false)
        };
        for (var i = 0; i < array.Count; i++)
            request.Views.Add(ParseSpec(array[i], i));
        return request;
    }

    public static bool TryParseMode(string? value, out NameConflictMode mode)
    {
        mode = NameConflictMode.Suffix;
        if (string.IsNullOrWhiteSpace(value)) return true;
        switch (value!.Trim().ToLowerInvariant())
        {
            case "suffix": mode = NameConflictMode.Suffix; return true;
            case "skip": mode = NameConflictMode.Skip; return true;
            case "fail": mode = NameConflictMode.Fail; return true;
            default: return false;
        }
    }

    public static ViewCreateSpec ParseSpec(JToken token, int index)
    {
        var spec = new ViewCreateSpec { Index = index };
        if (token is not JObject obj)
        {
            spec.Errors.Add("The spec is not an object.");
            return spec;
        }

        spec.RawKind = Str(obj, "kind") ?? string.Empty;
        spec.Kind = ViewKinds.Normalize(spec.RawKind);
        if (spec.Kind == null)
            spec.Errors.Add(spec.RawKind.Length == 0
                ? "'kind' is required (" + string.Join(", ", ViewKinds.All) + ")."
                : $"Unknown kind '{spec.RawKind}'. Use one of: {string.Join(", ", ViewKinds.All)}.");

        spec.Name = Str(obj, "name");
        spec.ViewFamilyType = Ref(obj, "viewFamilyType");
        spec.Template = Ref(obj, "template");
        spec.Scale = Int(obj, "scale", spec.Errors);
        if (spec.Scale is <= 0) spec.Errors.Add("'scale' must be a positive integer (100 for 1:100).");
        spec.DetailLevel = Str(obj, "detailLevel");
        spec.Discipline = Str(obj, "discipline");
        spec.Phase = Ref(obj, "phase");
        spec.ScopeBox = Ref(obj, "scopeBox");
        spec.PlaceOnSheet = ParseSheet(obj["placeOnSheet"], spec.Errors);

        spec.Level = Ref(obj, "level");
        var levels = obj["levels"];
        if (levels is JValue { Type: JTokenType.String } allValue)
        {
            var text = allValue.Value<string>()?.Trim() ?? string.Empty;
            if (IsAll(text)) spec.AllLevels = true;
            else if (text.Length > 0) spec.Levels.Add(text);
        }
        else if (levels is JArray levelArray)
        {
            foreach (var item in levelArray)
            {
                var text = item.Type == JTokenType.Null ? string.Empty : item.ToString().Trim();
                if (IsAll(text)) spec.AllLevels = true;
                else if (text.Length > 0) spec.Levels.Add(text);
            }
        }
        else if (levels != null && levels.Type != JTokenType.Null)
            spec.Errors.Add("'levels' must be an array of level names or ids, or \"all\".");

        spec.AreaScheme = Ref(obj, "areaScheme");
        spec.ViewRange = ParseViewRange(obj["viewRange"], spec.Errors);
        spec.CropTo = obj["cropTo"] is JObject crop ? ParseRegion(crop, spec.Errors, "cropTo", allowScopeBox: true) : null;

        spec.From = Point(obj, "from", spec.Errors);
        spec.To = Point(obj, "to", spec.Errors);
        var depth = Dbl(obj, "depthMm", spec.Errors);
        if (depth.HasValue) spec.DepthMm = depth.Value;
        spec.HeightMm = Dbl(obj, "heightMm", spec.Errors);
        spec.BottomMm = Dbl(obj, "bottomMm", spec.Errors);
        spec.Flip = Bool(obj, "flip", false);

        spec.ParentView = Ref(obj, "parentView");
        spec.ReferenceView = Ref(obj, "referenceView");
        spec.Region = ParseRegion(obj, spec.Errors, null, allowScopeBox: false);
        if (spec.Region.IsEmpty && spec.Region.PaddingMm == null) spec.Region = null;

        spec.Isometric = Bool(obj, "isometric", true);
        spec.Orientation = ParseOrientation(obj["orientation"], spec.Errors);

        spec.SourceLegend = Ref(obj, "sourceLegend");
        return spec;
    }

    /// <summary>
    /// A region: <c>box {min,max}</c>, <c>elements</c>/<c>aroundElements</c>, <c>room</c>/<c>aroundRoom</c> (an id, or
    /// <c>{id, linkInstanceId}</c>), optionally <c>scopeBox</c>, and <c>paddingMm</c>.
    /// </summary>
    public static RegionSpec ParseRegion(JObject obj, List<string> errors, string? label, bool allowScopeBox)
    {
        var prefix = label == null ? string.Empty : label + ".";
        var region = new RegionSpec();
        if (obj["box"] is { } boxToken && boxToken.Type != JTokenType.Null)
        {
            region.Box = ParseBox(boxToken, errors, prefix + "box");
        }

        foreach (var key in new[] { "aroundElements", "elements" })
        {
            if (obj[key] is not { } idsToken || idsToken.Type == JTokenType.Null) continue;
            if (idsToken is not JArray ids)
            {
                errors.Add($"'{prefix}{key}' must be an array of element ids.");
                continue;
            }
            foreach (var id in ids)
            {
                if (TryLong(id, out var value) && value > 0) region.ElementIds.Add(value);
                else errors.Add($"'{prefix}{key}' holds '{id}', which is not an element id.");
            }
        }
        region.ElementIds = region.ElementIds.Distinct().ToList();

        foreach (var key in new[] { "aroundRoom", "room" })
        {
            if (obj[key] is not { } roomToken || roomToken.Type == JTokenType.Null) continue;
            if (roomToken is JObject roomObj)
            {
                if (TryLong(roomObj["id"], out var roomId) && roomId > 0) region.RoomId = roomId;
                else errors.Add($"'{prefix}{key}.id' must be a room element id.");
                if (TryLong(roomObj["linkInstanceId"], out var linkId)) region.RoomLinkInstanceId = linkId;
            }
            else if (TryLong(roomToken, out var roomId) && roomId > 0) region.RoomId = roomId;
            else errors.Add($"'{prefix}{key}' must be a room element id, or {{id, linkInstanceId}} for a linked room.");
        }
        if (TryLong(obj["roomLinkInstanceId"], out var roomLink)) region.RoomLinkInstanceId = roomLink;
        if (TryLong(obj["linkInstanceId"], out var link) && region.RoomLinkInstanceId == 0) region.RoomLinkInstanceId = link;

        if (allowScopeBox) region.ScopeBox = Ref(obj, "scopeBox");

        region.PaddingMm = Dbl(obj, "paddingMm", errors);
        if (region.PaddingMm is < 0) errors.Add($"'{prefix}paddingMm' cannot be negative.");
        if (region.SourceCount > 1)
            errors.Add($"Give one extent source{(label == null ? string.Empty : " in " + label)}: box, elements, room" +
                       (allowScopeBox ? " or scopeBox" : string.Empty) + $" (got {region.SourceCount}).");
        return region;
    }

    public static BoxMm? ParseBox(JToken token, List<string> errors, string label)
    {
        if (token is not JObject box)
        {
            errors.Add($"'{label}' must be {{min: {{x,y,z}}, max: {{x,y,z}}}} in mm.");
            return null;
        }
        var min = Point(box, "min", errors, label + ".");
        var max = Point(box, "max", errors, label + ".");
        if (min == null || max == null)
        {
            errors.Add($"'{label}' needs min and max ({{x, y, z}} in mm).");
            return null;
        }
        // Accept the corners in any order: the box is what they span.
        var lo = new Vec3(Math.Min(min.Value.X, max.Value.X), Math.Min(min.Value.Y, max.Value.Y), Math.Min(min.Value.Z, max.Value.Z));
        var hi = new Vec3(Math.Max(min.Value.X, max.Value.X), Math.Max(min.Value.Y, max.Value.Y), Math.Max(min.Value.Z, max.Value.Z));
        return new BoxMm(lo, hi);
    }

    public static ViewRangeSpec? ParseViewRange(JToken? token, List<string> errors)
    {
        if (token == null || token.Type == JTokenType.Null) return null;
        if (token is not JObject obj)
        {
            errors.Add("'viewRange' must be {topOffsetMm, cutOffsetMm, bottomOffsetMm, depthOffsetMm}.");
            return null;
        }
        var range = new ViewRangeSpec
        {
            TopOffsetMm = Dbl(obj, "topOffsetMm", errors),
            CutOffsetMm = Dbl(obj, "cutOffsetMm", errors),
            BottomOffsetMm = Dbl(obj, "bottomOffsetMm", errors),
            DepthOffsetMm = Dbl(obj, "depthOffsetMm", errors)
        };
        if (range.IsEmpty) errors.Add("'viewRange' names no offset (topOffsetMm, cutOffsetMm, bottomOffsetMm, depthOffsetMm).");
        return range;
    }

    private static OrientationSpec? ParseOrientation(JToken? token, List<string> errors)
    {
        if (token == null || token.Type == JTokenType.Null) return null;
        if (token.Type == JTokenType.String)
        {
            var preset = token.Value<string>()?.Trim() ?? string.Empty;
            if (!ViewOrientationMath.IsPreset(preset))
            {
                errors.Add($"Unknown orientation '{preset}'. Presets: {string.Join(", ", ViewOrientationMath.Presets)}, or {{eye, forward, up}}.");
                return null;
            }
            return new OrientationSpec { Preset = preset.ToLowerInvariant() };
        }
        if (token is not JObject obj)
        {
            errors.Add("'orientation' must be a preset name or {eye, forward, up}.");
            return null;
        }
        var spec = new OrientationSpec
        {
            Preset = Str(obj, "preset")?.ToLowerInvariant(),
            EyeMm = Point(obj, "eye", errors),
            Forward = Point(obj, "forward", errors),
            Up = Point(obj, "up", errors)
        };
        if (spec.Preset != null && !ViewOrientationMath.IsPreset(spec.Preset))
            errors.Add($"Unknown orientation preset '{spec.Preset}'. Presets: {string.Join(", ", ViewOrientationMath.Presets)}.");
        if (spec.Preset == null && spec.Forward == null)
            errors.Add("'orientation' needs a preset or a forward direction.");
        return spec;
    }

    private static SheetPlacementSpec? ParseSheet(JToken? token, List<string> errors)
    {
        if (token == null || token.Type == JTokenType.Null) return null;
        if (token.Type is JTokenType.String or JTokenType.Integer)
            return new SheetPlacementSpec { Sheet = token.ToString().Trim() };
        if (token is not JObject obj)
        {
            errors.Add("'placeOnSheet' must be {sheet, xMm, yMm} (or {sheets: [...]}, one per level).");
            return null;
        }
        var placement = new SheetPlacementSpec
        {
            Sheet = Ref(obj, "sheet"),
            XMm = Dbl(obj, "xMm", errors),
            YMm = Dbl(obj, "yMm", errors)
        };
        if (obj["sheets"] is JArray sheets)
            placement.Sheets = sheets.Select(s => s.Type == JTokenType.Null ? string.Empty : s.ToString().Trim()).ToList();
        if (string.IsNullOrWhiteSpace(placement.Sheet) && placement.Sheets.Count == 0)
            errors.Add("'placeOnSheet' needs 'sheet' (number, name or id) or 'sheets'.");
        if (placement.XMm.HasValue != placement.YMm.HasValue)
            errors.Add("'placeOnSheet' needs both xMm and yMm, or neither (the sheet centre).");
        return placement;
    }

    // ── Token helpers ─────────────────────────────────────────────────────────

    public static JArray? ToArray(object? raw)
    {
        switch (raw)
        {
            case null: return null;
            case JArray ja: return ja;
            case string s:
                try { return JToken.Parse(s) as JArray; }
                catch { return null; }
            case JToken: return null;
            default:
                try { return JArray.FromObject(raw); }
                catch { return null; }
        }
    }

    /// <summary>A JSON object argument (box, viewRange) given as an object or as a JSON string.</summary>
    public static JObject? ToObject(object? raw)
    {
        switch (raw)
        {
            case null: return null;
            case JObject jo: return jo;
            case string s when s.Trim().Length > 0:
                try { return JToken.Parse(s) as JObject; }
                catch { return null; }
            case JToken: return null;
            case string: return null;
            default:
                try { return JObject.FromObject(raw); }
                catch { return null; }
        }
    }

    private static bool IsAll(string text) => text == "*" || string.Equals(text, "all", StringComparison.OrdinalIgnoreCase);

    private static string? Str(JObject obj, string key)
    {
        var token = obj[key];
        if (token == null || token.Type == JTokenType.Null) return null;
        var text = token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text!.Trim();
    }

    /// <summary>A reference: a name, or an element id given as a number. Kept as text; the resolver decides.</summary>
    private static string? Ref(JObject obj, string key) => Str(obj, key);

    private static bool Bool(JObject obj, string key, bool fallback)
    {
        var token = obj[key];
        if (token == null || token.Type == JTokenType.Null) return fallback;
        if (token.Type == JTokenType.Boolean) return token.Value<bool>();
        var text = token.ToString().Trim();
        return bool.TryParse(text, out var value) ? value : fallback;
    }

    private static int? Int(JObject obj, string key, List<string> errors)
    {
        var token = obj[key];
        if (token == null || token.Type == JTokenType.Null) return null;
        var text = token.ToString().Trim();
        if (text.StartsWith("1:", StringComparison.Ordinal)) text = text.Substring(2);
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return value;
        errors.Add($"'{key}' must be an integer (got '{token}').");
        return null;
    }

    private static double? Dbl(JObject obj, string key, List<string> errors)
    {
        var token = obj[key];
        if (token == null || token.Type == JTokenType.Null) return null;
        if (token.Type is JTokenType.Float or JTokenType.Integer) return token.Value<double>();
        if (double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return value;
        errors.Add($"'{key}' must be a number in mm (got '{token}').");
        return null;
    }

    private static Vec3? Point(JObject obj, string key, List<string> errors, string prefix = "")
    {
        var token = obj[key];
        if (token == null || token.Type == JTokenType.Null) return null;
        if (token is JArray arr && (arr.Count == 2 || arr.Count == 3) && arr.All(t => t.Type is JTokenType.Float or JTokenType.Integer))
            return new Vec3(arr[0].Value<double>(), arr[1].Value<double>(), arr.Count == 3 ? arr[2].Value<double>() : 0);
        if (token is not JObject p)
        {
            errors.Add($"'{prefix}{key}' must be {{x, y, z}} in mm.");
            return null;
        }
        var x = Dbl(p, "x", errors);
        var y = Dbl(p, "y", errors);
        var z = Dbl(p, "z", errors);
        if (x == null || y == null)
        {
            errors.Add($"'{prefix}{key}' needs x and y (z is optional, default 0).");
            return null;
        }
        return new Vec3(x.Value, y.Value, z ?? 0);
    }

    private static bool TryLong(JToken? token, out long value)
    {
        value = 0;
        if (token == null || token.Type == JTokenType.Null) return false;
        if (token.Type == JTokenType.Integer)
        {
            value = token.Value<long>();
            return true;
        }
        return long.TryParse(token.ToString().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static string ReadString(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value == null) return string.Empty;
        return value switch
        {
            string s => s,
            JValue jv => jv.Value?.ToString() ?? string.Empty,
            _ => value.ToString() ?? string.Empty
        };
    }

    private static bool ReadBool(Dictionary<string, object?> args, string key, bool fallback)
    {
        if (!args.TryGetValue(key, out var value) || value == null) return fallback;
        return value switch
        {
            bool b => b,
            JValue { Type: JTokenType.Boolean } jv => jv.Value<bool>(),
            JValue jv when bool.TryParse(jv.ToString(), out var parsed) => parsed,
            string s when bool.TryParse(s, out var parsed) => parsed,
            _ => fallback
        };
    }
}
