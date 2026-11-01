using System.Globalization;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.ViewFilters;

public static class ViewFilterKinds
{
    public const string Rule = "rule";
    public const string Selection = "selection";
}

/// <summary>One filter of revit_create_view_filters.</summary>
public sealed class ViewFilterCreateSpec
{
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = ViewFilterKinds.Rule;
    public List<string> Categories { get; } = new();
    public ViewFilterNode? Rules { get; set; }
    public List<long> ElementIds { get; } = new();
    public List<ViewFilterIssue> Issues { get; } = new();
}

public sealed class ViewFilterCreateRequest
{
    public List<ViewFilterCreateSpec> Filters { get; } = new();

    /// <summary>false (default): a taken name makes the filter invalid; true: the filter is left alone.</summary>
    public bool SkipExisting { get; set; }
    public bool Atomic { get; set; }
}

/// <summary>One filter of revit_edit_view_filters. A null list means "leave as it is".</summary>
public sealed class ViewFilterEditSpec
{
    public int Index { get; set; }
    public string Filter { get; set; } = string.Empty;
    public string? NewName { get; set; }
    public List<string>? Categories { get; set; }
    public bool HasRules { get; set; }
    public ViewFilterNode? Rules { get; set; }
    public bool ClearRules { get; set; }
    public List<long>? ElementIds { get; set; }
    public List<long>? AddElementIds { get; set; }
    public List<long>? RemoveElementIds { get; set; }
    public List<ViewFilterIssue> Issues { get; } = new();

    public bool TouchesRuleFields => Categories != null || HasRules || ClearRules;
    public bool TouchesMembers => ElementIds != null || AddElementIds != null || RemoveElementIds != null;
}

public sealed class ViewFilterEditRequest
{
    public List<ViewFilterEditSpec> Edits { get; } = new();
    public bool Atomic { get; set; }
}

public sealed class ViewFilterColor
{
    public ViewFilterColor(byte r, byte g, byte b)
    {
        R = r;
        G = g;
        B = b;
    }

    public byte R { get; }
    public byte G { get; }
    public byte B { get; }

    public string ToHex() => string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", R, G, B);
}

public sealed class ViewFilterLineSpec
{
    public ViewFilterColor? Color { get; set; }
    public int? Weight { get; set; }
    public string? Pattern { get; set; }
}

public sealed class ViewFilterFillSpec
{
    public ViewFilterColor? Color { get; set; }
    public string? Pattern { get; set; }
    public bool? Visible { get; set; }
}

/// <summary>Optional graphics of a filter in a view. A null field keeps what the view has.</summary>
public sealed class ViewFilterGraphicsSpec
{
    public bool? Halftone { get; set; }
    public int? Transparency { get; set; }
    public ViewFilterLineSpec? ProjectionLines { get; set; }
    public ViewFilterLineSpec? CutLines { get; set; }
    public ViewFilterFillSpec? SurfaceForeground { get; set; }
    public ViewFilterFillSpec? SurfaceBackground { get; set; }
    public ViewFilterFillSpec? CutForeground { get; set; }
    public ViewFilterFillSpec? CutBackground { get; set; }
}

/// <summary>One entry of 'add' in revit_apply_view_filters.</summary>
public sealed class ViewFilterApplyItem
{
    public int Index { get; set; }
    public string Filter { get; set; } = string.Empty;
    public bool? Enabled { get; set; }
    public bool? Visible { get; set; }
    public bool ClearGraphics { get; set; }
    public ViewFilterGraphicsSpec? Graphics { get; set; }
    public List<ViewFilterIssue> Issues { get; } = new();

    public bool HasSettings => Enabled != null || Visible != null || ClearGraphics || Graphics != null;
}

public sealed class ViewFilterApplyRequest
{
    public List<string> Views { get; } = new();
    public List<ViewFilterApplyItem> Add { get; } = new();
    public List<string> Remove { get; } = new();
    public List<string> Order { get; } = new();
    public bool Atomic { get; set; }
}

public static class ViewFilterIfExists
{
    public const string Reuse = "reuse";
    public const string Overwrite = "overwrite";
    public const string Skip = "skip";
    public const string Fail = "fail";
}

public sealed class ViewFilterCopyRequest
{
    public string SourceDocument { get; set; } = string.Empty;
    public string SourceView { get; set; } = string.Empty;
    public List<string> Filters { get; } = new();
    public List<string> TargetViews { get; } = new();
    public string IfExists { get; set; } = ViewFilterIfExists.Reuse;
    public bool CopySettings { get; set; } = true;
    public bool Atomic { get; set; }
}

/// <summary>Reads the arguments of the view filter write tools (#134). Pure: JSON in, specs out, so it is unit tested without Revit.</summary>
public static class ViewFilterRequestParser
{
    public const int MaxFilters = 200;
    public const int MaxViews = 500;

    /// <summary>The characters Revit refuses in a filter name (the same list as other element names).</summary>
    public const string ForbiddenNameChars = "\\:{}[]|;<>?`~";

    public static ViewFilterCreateRequest? ParseCreate(Dictionary<string, object?> arguments, out string? error)
    {
        var array = RequireArray(arguments, "filters", "an array of {name, categories, rules} or {name, kind: selection, elementIds}", MaxFilters, out error);
        if (array == null) return null;

        var mode = (ReadString(arguments, "ifNameExists") ?? "fail").Trim().ToLowerInvariant();
        if (mode.Length == 0) mode = "fail";
        if (mode != "fail" && mode != "skip")
        {
            error = $"ifNameExists must be fail or skip (got '{mode}').";
            return null;
        }

        var request = new ViewFilterCreateRequest { SkipExisting = mode == "skip", Atomic = ReadBool(arguments, "atomic", false) };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < array.Count; i++)
        {
            var spec = ParseCreateSpec(array[i], i);
            if (spec.Name.Length > 0 && !seen.Add(spec.Name))
                spec.Issues.Add(new ViewFilterIssue(ViewFilterCodes.DuplicateNameInRequest, $"Filter name '{spec.Name}' appears more than once in this request.", $"filters[{i}].name"));
            request.Filters.Add(spec);
        }
        return request;
    }

    private static ViewFilterCreateSpec ParseCreateSpec(JToken raw, int index)
    {
        var spec = new ViewFilterCreateSpec { Index = index };
        var path = $"filters[{index}]";
        if (raw is not JObject obj)
        {
            spec.Issues.Add(new ViewFilterIssue(ViewFilterCodes.InvalidSpec, "A filter spec must be an object.", path));
            return spec;
        }

        spec.Name = (ViewFilterRuleParser.Prop(obj, "name")?.ToString() ?? string.Empty).Trim();
        var nameIssue = CheckName(spec.Name, path + ".name");
        if (nameIssue != null) spec.Issues.Add(nameIssue);

        var rawCategories = ViewFilterRuleParser.Prop(obj, "categories");
        var rawRules = ViewFilterRuleParser.Prop(obj, "rules");
        var rawElements = ViewFilterRuleParser.Prop(obj, "elementIds");

        var kind = ViewFilterRuleParser.Prop(obj, "kind")?.ToString().Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(kind)) kind = rawElements != null ? ViewFilterKinds.Selection : ViewFilterKinds.Rule;
        if (kind != ViewFilterKinds.Rule && kind != ViewFilterKinds.Selection)
        {
            spec.Issues.Add(new ViewFilterIssue(ViewFilterCodes.InvalidSpec, $"kind must be rule or selection (got '{kind}').", path + ".kind"));
            return spec;
        }
        spec.Kind = kind!;

        if (spec.Kind == ViewFilterKinds.Selection)
        {
            if (rawCategories != null || rawRules != null)
                spec.Issues.Add(new ViewFilterIssue(ViewFilterCodes.InvalidSpec, "A selection filter is a fixed set of elements: it takes elementIds, not categories or rules.", path));
            if (rawElements != null) spec.ElementIds.AddRange(ReadIds(rawElements, spec.Issues, path + ".elementIds"));
            return spec;
        }

        if (rawElements != null)
            spec.Issues.Add(new ViewFilterIssue(ViewFilterCodes.InvalidSpec, "A rule filter takes categories and rules, not elementIds.", path));
        spec.Categories.AddRange(ReadRefs(rawCategories));
        if (spec.Categories.Count == 0)
            spec.Issues.Add(new ViewFilterIssue(ViewFilterCodes.NoCategories, "A rule filter needs at least one category.", path + ".categories"));
        spec.Rules = ViewFilterRuleParser.Parse(rawRules, spec.Issues, path + ".rules");
        return spec;
    }

    public static ViewFilterEditRequest? ParseEdit(Dictionary<string, object?> arguments, out string? error)
    {
        var array = RequireArray(arguments, "edits", "an array of {filter, name?, categories?, rules?, clearRules?, elementIds?, addElementIds?, removeElementIds?}", MaxFilters, out error);
        if (array == null) return null;

        var request = new ViewFilterEditRequest { Atomic = ReadBool(arguments, "atomic", false) };
        for (var i = 0; i < array.Count; i++)
            request.Edits.Add(ParseEditSpec(array[i], i));
        return request;
    }

    private static ViewFilterEditSpec ParseEditSpec(JToken raw, int index)
    {
        var spec = new ViewFilterEditSpec { Index = index };
        var path = $"edits[{index}]";
        if (raw is not JObject obj)
        {
            spec.Issues.Add(new ViewFilterIssue(ViewFilterCodes.InvalidSpec, "An edit must be an object.", path));
            return spec;
        }

        spec.Filter = (ViewFilterRuleParser.Prop(obj, "filter")?.ToString() ?? string.Empty).Trim();
        if (spec.Filter.Length == 0)
            spec.Issues.Add(new ViewFilterIssue(ViewFilterCodes.InvalidSpec, "'filter' is required: the filter's name or id.", path + ".filter"));

        var rawName = ViewFilterRuleParser.Prop(obj, "name");
        if (rawName != null)
        {
            spec.NewName = rawName.ToString().Trim();
            var nameIssue = CheckName(spec.NewName, path + ".name");
            if (nameIssue != null) spec.Issues.Add(nameIssue);
        }

        var rawCategories = ViewFilterRuleParser.Prop(obj, "categories");
        if (rawCategories != null)
        {
            spec.Categories = ReadRefs(rawCategories);
            if (spec.Categories.Count == 0)
                spec.Issues.Add(new ViewFilterIssue(ViewFilterCodes.NoCategories, "'categories' is empty: a rule filter needs at least one category.", path + ".categories"));
        }

        var rawRules = ViewFilterRuleParser.Prop(obj, "rules");
        if (rawRules != null)
        {
            spec.HasRules = true;
            var before = spec.Issues.Count;
            spec.Rules = ViewFilterRuleParser.Parse(rawRules, spec.Issues, path + ".rules");
            if (spec.Rules == null && spec.Issues.Count == before)
                spec.Issues.Add(new ViewFilterIssue(ViewFilterCodes.InvalidSpec, "'rules' holds no rule. To remove the rules pass clearRules=true.", path + ".rules"));
        }

        spec.ClearRules = ReadOptionalBool(obj, "clearRules", spec.Issues, path) == true;
        if (spec.ClearRules && spec.HasRules)
            spec.Issues.Add(new ViewFilterIssue(ViewFilterCodes.InvalidSpec, "Give 'rules' or clearRules=true, not both.", path));

        spec.ElementIds = ReadOptionalIds(obj, "elementIds", spec.Issues, path);
        spec.AddElementIds = ReadOptionalIds(obj, "addElementIds", spec.Issues, path);
        spec.RemoveElementIds = ReadOptionalIds(obj, "removeElementIds", spec.Issues, path);
        if (spec.ElementIds != null && (spec.AddElementIds != null || spec.RemoveElementIds != null))
            spec.Issues.Add(new ViewFilterIssue(ViewFilterCodes.InvalidSpec, "Give elementIds (replace) or addElementIds/removeElementIds, not both.", path));

        if (spec.TouchesRuleFields && spec.TouchesMembers)
            spec.Issues.Add(new ViewFilterIssue(ViewFilterCodes.InvalidSpec, "One filter cannot take both categories/rules and element ids: it is either a rule filter or a selection filter.", path));
        if (spec.NewName == null && !spec.TouchesRuleFields && !spec.TouchesMembers)
            spec.Issues.Add(new ViewFilterIssue(ViewFilterCodes.NothingToDo, "This edit changes nothing: give name, categories, rules, clearRules or element ids.", path));
        return spec;
    }

    public static List<string>? ParseDelete(Dictionary<string, object?> arguments, out string? error)
    {
        var array = RequireArray(arguments, "filters", "an array of filter names or ids", MaxFilters, out error);
        if (array == null) return null;
        var refs = ReadRefs(array).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (refs.Count == 0)
        {
            error = "'filters' holds no filter name or id.";
            return null;
        }
        return refs;
    }

    public static ViewFilterApplyRequest? ParseApply(Dictionary<string, object?> arguments, out string? error)
    {
        var views = RequireArray(arguments, "views", "an array of view or view template names or ids", MaxViews, out error);
        if (views == null) return null;

        var request = new ViewFilterApplyRequest { Atomic = ReadBool(arguments, "atomic", false) };
        request.Views.AddRange(ReadRefs(views).Distinct(StringComparer.OrdinalIgnoreCase));

        var addToken = ToToken(arguments, "add");
        if (addToken is JArray add)
        {
            for (var i = 0; i < add.Count; i++)
                request.Add.Add(ParseApplyItem(add[i], i));
        }
        else if (addToken != null && addToken.Type != JTokenType.Null)
        {
            error = "'add' must be an array of filter names or ids, or objects {filter, enabled, visible, halftone, transparency, projectionLines, cutLines, surfaceForeground, surfaceBackground, cutForeground, cutBackground}.";
            return null;
        }
        request.Remove.AddRange(ReadRefs(ToToken(arguments, "remove")));
        request.Order.AddRange(ReadRefs(ToToken(arguments, "order")));

        if (request.Add.Count == 0 && request.Remove.Count == 0 && request.Order.Count == 0)
        {
            error = "Nothing to do: give 'add', 'remove' or 'order'.";
            return null;
        }
        return request;
    }

    private static ViewFilterApplyItem ParseApplyItem(JToken raw, int index)
    {
        var item = new ViewFilterApplyItem { Index = index };
        var path = $"add[{index}]";
        if (raw.Type is JTokenType.String or JTokenType.Integer)
        {
            item.Filter = raw.ToString().Trim();
            return item;
        }
        if (raw is not JObject obj)
        {
            item.Issues.Add(new ViewFilterIssue(ViewFilterCodes.InvalidSpec, "An 'add' entry is a filter name or id, or an object with 'filter'.", path));
            return item;
        }

        item.Filter = (ViewFilterRuleParser.Prop(obj, "filter")?.ToString() ?? string.Empty).Trim();
        if (item.Filter.Length == 0)
            item.Issues.Add(new ViewFilterIssue(ViewFilterCodes.InvalidSpec, "'filter' is required: the filter's name or id.", path + ".filter"));

        item.Enabled = ReadOptionalBool(obj, "enabled", item.Issues, path);
        item.Visible = ReadOptionalBool(obj, "visible", item.Issues, path);
        item.ClearGraphics = ReadOptionalBool(obj, "clearGraphics", item.Issues, path) == true;
        item.Graphics = ParseGraphics(obj, item.Issues, path);
        return item;
    }

    /// <summary>The graphics fields of an object; null when it has none. Every bad value is an issue with code graphics_invalid.</summary>
    public static ViewFilterGraphicsSpec? ParseGraphics(JObject obj, List<ViewFilterIssue> issues, string path)
    {
        var spec = new ViewFilterGraphicsSpec();
        var any = false;

        var halftone = ReadOptionalBool(obj, "halftone", issues, path, ViewFilterCodes.GraphicsInvalid);
        if (halftone != null)
        {
            spec.Halftone = halftone;
            any = true;
        }

        var transparency = ViewFilterRuleParser.Prop(obj, "transparency");
        if (transparency != null)
        {
            any = true;
            if (ViewFilterRuleParser.TryNumber(transparency, out var t) && t == Math.Floor(t) && t is >= 0 and <= 100) spec.Transparency = (int)t;
            else issues.Add(new ViewFilterIssue(ViewFilterCodes.GraphicsInvalid, "transparency must be a whole number from 0 to 100.", path + ".transparency"));
        }

        spec.ProjectionLines = ParseLine(obj, "projectionLines", issues, path, ref any);
        spec.CutLines = ParseLine(obj, "cutLines", issues, path, ref any);
        spec.SurfaceForeground = ParseFill(obj, "surfaceForeground", issues, path, ref any);
        spec.SurfaceBackground = ParseFill(obj, "surfaceBackground", issues, path, ref any);
        spec.CutForeground = ParseFill(obj, "cutForeground", issues, path, ref any);
        spec.CutBackground = ParseFill(obj, "cutBackground", issues, path, ref any);
        return any ? spec : null;
    }

    private static ViewFilterLineSpec? ParseLine(JObject obj, string key, List<ViewFilterIssue> issues, string path, ref bool any)
    {
        var raw = ViewFilterRuleParser.Prop(obj, key);
        if (raw == null) return null;
        any = true;
        var at = $"{path}.{key}";
        if (raw is not JObject line)
        {
            issues.Add(new ViewFilterIssue(ViewFilterCodes.GraphicsInvalid, $"{key} must be an object {{color, weight, pattern}}.", at));
            return null;
        }

        var spec = new ViewFilterLineSpec { Color = ReadColor(line, issues, at), Pattern = ViewFilterRuleParser.Prop(line, "pattern")?.ToString() };
        var weight = ViewFilterRuleParser.Prop(line, "weight");
        if (weight != null)
        {
            if (ViewFilterRuleParser.TryNumber(weight, out var w) && w == Math.Floor(w) && w is >= 1 and <= 16) spec.Weight = (int)w;
            else issues.Add(new ViewFilterIssue(ViewFilterCodes.GraphicsInvalid, "A line weight must be a whole number from 1 to 16.", at + ".weight"));
        }
        return spec;
    }

    private static ViewFilterFillSpec? ParseFill(JObject obj, string key, List<ViewFilterIssue> issues, string path, ref bool any)
    {
        var raw = ViewFilterRuleParser.Prop(obj, key);
        if (raw == null) return null;
        any = true;
        var at = $"{path}.{key}";
        if (raw is not JObject fill)
        {
            issues.Add(new ViewFilterIssue(ViewFilterCodes.GraphicsInvalid, $"{key} must be an object {{color, pattern, visible}}.", at));
            return null;
        }
        return new ViewFilterFillSpec
        {
            Color = ReadColor(fill, issues, at),
            Pattern = ViewFilterRuleParser.Prop(fill, "pattern")?.ToString(),
            Visible = ReadOptionalBool(fill, "visible", issues, at, ViewFilterCodes.GraphicsInvalid)
        };
    }

    private static ViewFilterColor? ReadColor(JObject obj, List<ViewFilterIssue> issues, string path)
    {
        var raw = ViewFilterRuleParser.Prop(obj, "color");
        if (raw == null) return null;
        if (TryParseColor(raw, out var color)) return color;
        issues.Add(new ViewFilterIssue(ViewFilterCodes.GraphicsInvalid, $"A color is \"#RRGGBB\", [r, g, b] or {{r, g, b}} with values 0-255; got {raw.ToString(Newtonsoft.Json.Formatting.None)}.", path + ".color"));
        return null;
    }

    public static bool TryParseColor(JToken raw, out ViewFilterColor? color)
    {
        color = null;
        switch (raw)
        {
            case JValue when raw.Type == JTokenType.String:
                var hex = raw.ToString().Trim().TrimStart('#');
                if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return false;
                color = new ViewFilterColor((byte)((rgb >> 16) & 255), (byte)((rgb >> 8) & 255), (byte)(rgb & 255));
                return true;
            case JArray array when array.Count == 3:
                return TryRgb(array[0], array[1], array[2], out color);
            case JObject obj:
                var r = ViewFilterRuleParser.Prop(obj, "r");
                var g = ViewFilterRuleParser.Prop(obj, "g");
                var b = ViewFilterRuleParser.Prop(obj, "b");
                return r != null && g != null && b != null && TryRgb(r, g, b, out color);
            default:
                return false;
        }
    }

    private static bool TryRgb(JToken r, JToken g, JToken b, out ViewFilterColor? color)
    {
        color = null;
        if (!Channel(r, out var red) || !Channel(g, out var green) || !Channel(b, out var blue)) return false;
        color = new ViewFilterColor(red, green, blue);
        return true;
    }

    private static bool Channel(JToken token, out byte value)
    {
        value = 0;
        if (!ViewFilterRuleParser.TryNumber(token, out var n) || n != Math.Floor(n) || n < 0 || n > 255) return false;
        value = (byte)n;
        return true;
    }

    public static ViewFilterCopyRequest? ParseCopy(Dictionary<string, object?> arguments, out string? error)
    {
        error = null;
        var request = new ViewFilterCopyRequest
        {
            SourceDocument = (ReadString(arguments, "sourceDocument") ?? string.Empty).Trim(),
            SourceView = (ReadString(arguments, "sourceView") ?? string.Empty).Trim(),
            CopySettings = ReadBool(arguments, "copySettings", true),
            Atomic = ReadBool(arguments, "atomic", false)
        };
        request.Filters.AddRange(ReadRefs(ToToken(arguments, "filters")));
        request.TargetViews.AddRange(ReadRefs(ToToken(arguments, "targetViews")));

        var mode = (ReadString(arguments, "ifExists") ?? ViewFilterIfExists.Reuse).Trim().ToLowerInvariant();
        if (mode.Length == 0) mode = ViewFilterIfExists.Reuse;
        if (mode is not (ViewFilterIfExists.Reuse or ViewFilterIfExists.Overwrite or ViewFilterIfExists.Skip or ViewFilterIfExists.Fail))
        {
            error = $"ifExists must be reuse, overwrite, skip or fail (got '{mode}').";
            return null;
        }
        request.IfExists = mode;

        if (request.SourceView.Length == 0 && request.Filters.Count == 0)
        {
            error = "Give sourceView (its filters and their settings) or filters (names or ids in the source document), or both.";
            return null;
        }
        return request;
    }

    /// <summary>A filter name's problem, or null. Revit's own check runs again in the add-in.</summary>
    public static ViewFilterIssue? CheckName(string name, string path)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new ViewFilterIssue(ViewFilterCodes.InvalidFilterName, "A filter needs a name.", path);
        var bad = name.Where(c => ForbiddenNameChars.IndexOf(c) >= 0).Distinct().ToArray();
        return bad.Length == 0
            ? null
            : new ViewFilterIssue(ViewFilterCodes.InvalidFilterName, $"Revit refuses these characters in a name: {string.Join(" ", bad)} ('{name}').", path);
    }

    public static List<string> ReadRefs(JToken? raw)
    {
        var result = new List<string>();
        switch (raw)
        {
            case null:
                break;
            case JArray array:
                foreach (var item in array)
                    if (item.Type is JTokenType.String or JTokenType.Integer && !string.IsNullOrWhiteSpace(item.ToString()))
                        result.Add(item.ToString().Trim());
                break;
            default:
                if (raw.Type is JTokenType.String or JTokenType.Integer && !string.IsNullOrWhiteSpace(raw.ToString()))
                    result.Add(raw.ToString().Trim());
                break;
        }
        return result;
    }

    public static List<string> ReadRefs(Dictionary<string, object?> arguments, string key) => ReadRefs(ToToken(arguments, key));

    private static List<long> ReadIds(JToken raw, List<ViewFilterIssue> issues, string path)
    {
        var ids = new List<long>();
        if (raw is not JArray array)
        {
            issues.Add(new ViewFilterIssue(ViewFilterCodes.InvalidSpec, "Must be an array of element ids.", path));
            return ids;
        }
        foreach (var item in array)
        {
            if (item.Type == JTokenType.Integer) ids.Add(item.Value<long>());
            else if (long.TryParse(item.ToString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id)) ids.Add(id);
            else issues.Add(new ViewFilterIssue(ViewFilterCodes.InvalidSpec, $"'{item}' is not an element id.", path));
        }
        return ids.Distinct().ToList();
    }

    private static List<long>? ReadOptionalIds(JObject obj, string key, List<ViewFilterIssue> issues, string path)
    {
        var raw = ViewFilterRuleParser.Prop(obj, key);
        return raw == null ? null : ReadIds(raw, issues, $"{path}.{key}");
    }

    private static bool? ReadOptionalBool(JObject obj, string key, List<ViewFilterIssue> issues, string path, string code = ViewFilterCodes.InvalidSpec)
    {
        var raw = ViewFilterRuleParser.Prop(obj, key);
        if (raw == null) return null;
        if (raw.Type == JTokenType.Boolean) return raw.Value<bool>();
        issues.Add(new ViewFilterIssue(code, $"{key} must be true or false.", $"{path}.{key}"));
        return null;
    }

    private static JArray? RequireArray(Dictionary<string, object?> arguments, string key, string wanted, int limit, out string? error)
    {
        error = null;
        if (ToToken(arguments, key) is not JArray array)
        {
            error = $"'{key}' is required: {wanted}.";
            return null;
        }
        if (array.Count == 0)
        {
            error = $"'{key}' is empty.";
            return null;
        }
        if (array.Count > limit)
        {
            error = $"'{key}' holds {array.Count} entries; the limit is {limit} per call. Split the request.";
            return null;
        }
        return array;
    }

    internal static JToken? ToToken(Dictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var raw) || raw == null) return null;
        return raw as JToken ?? JToken.FromObject(raw);
    }

    private static string? ReadString(Dictionary<string, object?> arguments, string key)
    {
        var token = ToToken(arguments, key);
        return token == null || token.Type == JTokenType.Null ? null : token.ToString();
    }

    private static bool ReadBool(Dictionary<string, object?> arguments, string key, bool fallback)
    {
        var token = ToToken(arguments, key);
        return token != null && token.Type == JTokenType.Boolean ? token.Value<bool>() : fallback;
    }
}
