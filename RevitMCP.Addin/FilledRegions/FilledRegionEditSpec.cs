using System.Globalization;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.FilledRegions;

/// <summary>One edit of an existing region. Type, line style and parameters keep the id; a boundary replaces the element.</summary>
public sealed class RegionEditSpec
{
    public int Index { get; set; }
    public long Id { get; set; }
    public string? Type { get; set; }
    public string? LineStyle { get; set; }
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public RegionShapeSpec? Boundary { get; set; }
    public List<string> Errors { get; } = new();
    public bool Reshapes => Boundary != null;
}

public sealed class RegionEditRequest
{
    public List<RegionEditSpec> Edits { get; } = new();
    public bool Atomic { get; set; }
}

/// <summary>An RGB colour of a type, 0-255 per channel.</summary>
public readonly struct RegionColor
{
    public RegionColor(byte r, byte g, byte b)
    {
        R = r;
        G = g;
        B = b;
    }

    public byte R { get; }
    public byte G { get; }
    public byte B { get; }

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>"#RRGGBB", "RRGGBB", "r,g,b" or {r, g, b}. Null with an error when it is something else.</summary>
    public static RegionColor? Parse(JToken? token, string field, List<string> errors)
    {
        if (token == null || token.Type == JTokenType.Null) return null;
        if (token is JObject o)
        {
            var r = FilledRegionSpecParser.Number(o["r"]);
            var g = FilledRegionSpecParser.Number(o["g"]);
            var b = FilledRegionSpecParser.Number(o["b"]);
            if (IsChannel(r) && IsChannel(g) && IsChannel(b)) return new RegionColor((byte)r!.Value, (byte)g!.Value, (byte)b!.Value);
            errors.Add($"{field}: r, g and b must be 0-255.");
            return null;
        }

        var text = (FilledRegionSpecParser.Text(token) ?? string.Empty).Trim();
        var hex = text.StartsWith("#", StringComparison.Ordinal) ? text.Substring(1) : text;
        if (hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            return new RegionColor((byte)(value >> 16), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF));

        var parts = text.Split(',');
        if (parts.Length == 3 &&
            byte.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var red) &&
            byte.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var green) &&
            byte.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var blue))
            return new RegionColor(red, green, blue);

        errors.Add($"{field}: '{text}' is not a colour. Use \"#RRGGBB\", \"r,g,b\" or {{r, g, b}}.");
        return null;
    }

    private static bool IsChannel(double? value) => value is >= 0 and <= 255 && Math.Abs(value.Value - Math.Round(value.Value)) < 1e-9;
}

/// <summary>A type to create by duplicating, or to edit. A null field stays as it is.</summary>
public sealed class RegionTypeSpec
{
    public int Index { get; set; }

    /// <summary>Edit this type (name or id).</summary>
    public string? Type { get; set; }

    /// <summary>Create a new type by duplicating this one (name or id); Name is the new type's name.</summary>
    public string? DuplicateFrom { get; set; }

    /// <summary>The new type's name, or the new name of an edited type.</summary>
    public string? Name { get; set; }

    /// <summary>Pattern name or id, "solid", or "none". Null = unchanged.</summary>
    public string? ForegroundPattern { get; set; }
    public string? BackgroundPattern { get; set; }
    public RegionColor? ForegroundColor { get; set; }
    public RegionColor? BackgroundColor { get; set; }
    public int? LineWeight { get; set; }
    public bool? IsMasking { get; set; }

    public List<string> Errors { get; } = new();
    public bool IsCreate => !string.IsNullOrWhiteSpace(DuplicateFrom);

    public bool ChangesAppearance =>
        ForegroundPattern != null || BackgroundPattern != null || ForegroundColor != null || BackgroundColor != null ||
        LineWeight != null || IsMasking != null;
}

public sealed class RegionTypeRequest
{
    public List<RegionTypeSpec> Types { get; } = new();
    public bool Atomic { get; set; }
}

/// <summary>Reads the edit and type tools' arguments. Pure.</summary>
public static class FilledRegionEditParser
{
    public const int MaxEdits = 500;
    public const int MaxTypes = 100;

    public static RegionEditRequest? ParseEdits(IDictionary<string, object?> args, out string? error)
    {
        var edits = FilledRegionSpecParser.ReadArray(args, "edits", out error);
        if (error != null) return null;
        if (edits == null || edits.Count == 0)
        {
            error = "Provide 'edits': an array of {id, type?, lineStyle?, parameters?, boundary?}.";
            return null;
        }
        if (edits.Count > MaxEdits)
        {
            error = $"{edits.Count} edits were given; the limit is {MaxEdits} per call.";
            return null;
        }

        var request = new RegionEditRequest { Atomic = FilledRegionSpecParser.ReadBool(args, "atomic") };
        var index = 0;
        foreach (var token in edits) request.Edits.Add(ParseEdit(token, index++));
        return request;
    }

    private static RegionEditSpec ParseEdit(JToken token, int index)
    {
        var spec = new RegionEditSpec { Index = index };
        if (token is not JObject o)
        {
            spec.Errors.Add("An edit must be an object.");
            return spec;
        }

        spec.Id = FilledRegionSpecParser.Long(o["id"]);
        if (spec.Id == 0) spec.Errors.Add("'id' (the filled region's element id) is required.");
        spec.Type = FilledRegionSpecParser.Text(o["type"]);
        spec.LineStyle = FilledRegionSpecParser.Text(o["lineStyle"]);
        spec.Parameters = FilledRegionSpecParser.ParseParameters(o["parameters"], spec.Errors);

        if (o["boundary"] is JObject boundary)
        {
            spec.Boundary = FilledRegionSpecParser.ParseShape(boundary, spec.Errors);
            if (spec.Boundary.Kind == RegionShapeKind.None && !spec.Errors.Any(e => e.Contains("shape")))
                spec.Errors.Add("'boundary' must hold one shape: loops, rectangle, room, detailLines or element.");
        }
        else if (o["boundary"] != null && o["boundary"]!.Type != JTokenType.Null)
        {
            spec.Errors.Add("'boundary' must be an object holding one shape.");
        }

        if (string.IsNullOrWhiteSpace(spec.Type) && string.IsNullOrWhiteSpace(spec.LineStyle) && spec.Parameters.Count == 0 && spec.Boundary == null)
            spec.Errors.Add("Nothing to change: give type, lineStyle, parameters or boundary.");
        return spec;
    }

    public static RegionTypeRequest? ParseTypes(IDictionary<string, object?> args, out string? error)
    {
        var types = FilledRegionSpecParser.ReadArray(args, "types", out error);
        if (error != null) return null;
        if (types == null || types.Count == 0)
        {
            error = "Provide 'types': an array of {type | duplicateFrom, name, foregroundPattern, foregroundColor, backgroundPattern, backgroundColor, lineWeight, isMasking}.";
            return null;
        }
        if (types.Count > MaxTypes)
        {
            error = $"{types.Count} types were given; the limit is {MaxTypes} per call.";
            return null;
        }

        var request = new RegionTypeRequest { Atomic = FilledRegionSpecParser.ReadBool(args, "atomic") };
        var index = 0;
        foreach (var token in types) request.Types.Add(ParseType(token, index++));
        return request;
    }

    private static RegionTypeSpec ParseType(JToken token, int index)
    {
        var spec = new RegionTypeSpec { Index = index };
        if (token is not JObject o)
        {
            spec.Errors.Add("A type spec must be an object.");
            return spec;
        }

        spec.Type = FilledRegionSpecParser.Text(o["type"]);
        spec.DuplicateFrom = FilledRegionSpecParser.Text(o["duplicateFrom"]);
        spec.Name = FilledRegionSpecParser.Text(o["name"])?.Trim();
        spec.ForegroundPattern = PatternText(o["foregroundPattern"]);
        spec.BackgroundPattern = PatternText(o["backgroundPattern"]);
        spec.ForegroundColor = RegionColor.Parse(o["foregroundColor"], "foregroundColor", spec.Errors);
        spec.BackgroundColor = RegionColor.Parse(o["backgroundColor"], "backgroundColor", spec.Errors);

        var weight = FilledRegionSpecParser.Number(o["lineWeight"]);
        if (weight != null)
        {
            if (weight is >= 1 and <= 16 && Math.Abs(weight.Value - Math.Round(weight.Value)) < 1e-9) spec.LineWeight = (int)weight.Value;
            else spec.Errors.Add($"lineWeight must be a whole number from 1 to 16 (got {weight.Value.ToString(CultureInfo.InvariantCulture)}).");
        }
        if (o["isMasking"] is { Type: JTokenType.Boolean } masking) spec.IsMasking = masking.Value<bool>();
        else if (o["isMasking"] != null && o["isMasking"]!.Type != JTokenType.Null) spec.Errors.Add("isMasking must be true or false.");

        var hasType = !string.IsNullOrWhiteSpace(spec.Type);
        if (hasType == spec.IsCreate)
            spec.Errors.Add("Give 'type' to edit an existing type, or 'duplicateFrom' with 'name' to create one.");
        if (spec.IsCreate && string.IsNullOrWhiteSpace(spec.Name))
            spec.Errors.Add("'name' is required with duplicateFrom: the new type's name.");
        if (hasType && !spec.ChangesAppearance && string.IsNullOrWhiteSpace(spec.Name))
            spec.Errors.Add("Nothing to change: give a pattern, colour, lineWeight, isMasking or a new name.");
        if (!string.IsNullOrEmpty(spec.Name) && spec.Name!.IndexOfAny(ForbiddenNameChars) >= 0)
            spec.Errors.Add($"The name '{spec.Name}' has a character Revit refuses in names.");
        return spec;
    }

    /// <summary>Characters Revit refuses in element type names.</summary>
    private static readonly char[] ForbiddenNameChars = { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' };

    private static string? PatternText(JToken? token) =>
        token == null || token.Type == JTokenType.Null ? null : FilledRegionSpecParser.Text(token)?.Trim() ?? string.Empty;
}

/// <summary>Plain-language summaries of the write tools' requests, for the approval prompt. Pure.</summary>
public static class FilledRegionSummaries
{
    public static string Create(RegionCreateRequest request)
    {
        var parts = new List<string>();
        if (request.Regions.Count > 0)
        {
            var kinds = request.Regions.GroupBy(r => KindLabel(r.Shape.Kind)).Select(g => $"{g.Count()} from {g.Key}");
            var views = request.Regions.Select(r => r.View).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            parts.Add($"{request.Regions.Count} filled region(s) ({string.Join(", ", kinds)}) in {views} view(s)");
        }
        foreach (var rule in request.Rules)
        {
            var source = rule.SourceKind == RuleSourceKind.Rooms
                ? rule.LinkInstanceId != 0 ? $"linked rooms of link {rule.LinkInstanceId}" : "rooms"
                : "elements" + (rule.Categories.Count > 0 ? $" of {string.Join(", ", rule.Categories)}" : string.Empty);
            var type = rule.TypeMap != null ? $"the type mapped from '{rule.TypeMap.Parameter}'" : $"type '{rule.Type}'";
            parts.Add($"a region per matching {source} in view '{rule.View}' with {type}");
        }
        return "Create " + string.Join(" and ", parts) + (request.Atomic ? " (atomic)." : ".");
    }

    public static string CreateCounted(int regions, int views, int skipped) =>
        $"Create {regions} filled region(s) in {views} view(s)" + (skipped > 0 ? $"; {skipped} source(s) skipped." : ".");

    public static string Edit(RegionEditRequest request)
    {
        var reshape = request.Edits.Count(e => e.Reshapes);
        var other = request.Edits.Count - reshape;
        var parts = new List<string>();
        if (other > 0) parts.Add($"change type, line style or parameters of {other} filled region(s), ids kept");
        if (reshape > 0) parts.Add($"replace {reshape} filled region(s) to reshape them: new ids, and tags and dimensions on the old regions may be lost");
        return "Edit: " + string.Join("; ", parts) + ".";
    }

    public static string Types(RegionTypeRequest request)
    {
        var created = request.Types.Where(t => t.IsCreate).Select(t => $"'{t.Name}'").ToList();
        var edited = request.Types.Where(t => !t.IsCreate).Select(t => $"'{t.Type}'").ToList();
        var parts = new List<string>();
        if (created.Count > 0) parts.Add($"create filled region type(s) {string.Join(", ", created)}");
        if (edited.Count > 0) parts.Add($"edit filled region type(s) {string.Join(", ", edited)}; every region of those types changes");
        return char.ToUpperInvariant(parts[0][0]) + string.Join("; ", parts).Substring(1) + ".";
    }

    private static string KindLabel(RegionShapeKind kind) => kind switch
    {
        RegionShapeKind.Loops => "coordinates",
        RegionShapeKind.Rectangle => "a rectangle",
        RegionShapeKind.Room => "a room boundary",
        RegionShapeKind.DetailLines => "detail lines",
        RegionShapeKind.ElementBox => "an element box",
        _ => "an invalid shape"
    };
}
