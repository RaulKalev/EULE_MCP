using System.Globalization;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Documentation.Placement;

namespace RevitMCP.Addin.Legends;

/// <summary>How a type gets into a legend view.</summary>
public enum LegendPlacementKind
{
    /// <summary>A model type: a copy of the seed legend component, switched to this type (LEGEND_COMPONENT).</summary>
    Component,
    /// <summary>A detail item or generic annotation family type: placed as an ordinary view-specific instance.</summary>
    DetailInstance,
    /// <summary>Neither: Revit offers no way to show it in a legend through the API.</summary>
    Unsupported
}

/// <summary>A type the legend could show, read from the model. Pure DTO.</summary>
public sealed class LegendTypeInfo
{
    public long Id { get; set; }
    public string Family { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public LegendPlacementKind Kind { get; set; }
    /// <summary>Why Kind is Unsupported.</summary>
    public string? UnsupportedReason { get; set; }
    /// <summary>The value of the label parameter, when the label is a parameter (null = the type has no such value).</summary>
    public string? LabelParameterValue { get; set; }

    public string Display => Family.Length > 0 ? $"{Family}: {Type}" : Type;
}

/// <summary>One entry of <c>types</c>: an id, or a family and/or type name.</summary>
public sealed class LegendTypeRef
{
    public string Source { get; set; } = string.Empty;
    public long? Id { get; set; }
    public string? Family { get; set; }
    public string? Type { get; set; }
}

public enum LegendLabelMode { TypeName, FamilyAndType, Parameter, None }

/// <summary>The parsed arguments of revit_populate_legend and its preview.</summary>
public sealed class LegendPopulateRequest
{
    public const int MaxTypes = 200;
    public const int MaxColumns = 50;
    public const double DefaultRowSpacingMm = 10;
    public const double DefaultColumnSpacingMm = 90;
    public const double DefaultTextOffsetMm = 12;

    /// <summary>The legend view: a name or an id.</summary>
    public string Legend { get; set; } = string.Empty;
    /// <summary>The legend component to copy; null = the first one of the legend.</summary>
    public long? SeedId { get; set; }
    public List<LegendTypeRef> Types { get; } = new();
    public string? Category { get; set; }
    public List<string> Families { get; } = new();
    public int Columns { get; set; } = 1;
    public double RowSpacingMm { get; set; } = DefaultRowSpacingMm;
    public double ColumnSpacingMm { get; set; } = DefaultColumnSpacingMm;
    /// <summary>Top-left cell centre in printed mm; null = below what the legend already shows.</summary>
    public double[]? Origin { get; set; }
    public LegendLabelMode Label { get; set; } = LegendLabelMode.TypeName;
    public string? LabelParameter { get; set; }
    /// <summary>A text note type name or id; null = the document default.</summary>
    public string? TextType { get; set; }
    public double TextOffsetMm { get; set; } = DefaultTextOffsetMm;
    public bool ClearExisting { get; set; }
    /// <summary>request | family | type | category | label.</summary>
    public string SortBy { get; set; } = "request";
    public bool DryRun { get; set; } = true;

    public bool UsesFilters => Category != null || Families.Count > 0;
}

/// <summary>A cell of the symbol grid, in printed mm (model mm divided by the legend's scale).</summary>
public sealed class LegendCell
{
    public int Index { get; set; }
    public int Row { get; set; }
    public int Column { get; set; }
    /// <summary>Where the symbol's centre goes.</summary>
    public double XMm { get; set; }
    public double YMm { get; set; }
    /// <summary>Where the label's left edge goes (vertically centred on the row).</summary>
    public double LabelXMm { get; set; }
    public double LabelYMm { get; set; }
}

/// <summary>An element already in the legend view, for clearExisting and the default origin.</summary>
public sealed class LegendExistingItem
{
    public long Id { get; set; }
    /// <summary>component | textNote | detailItem.</summary>
    public string Kind { get; set; } = string.Empty;
    /// <summary>Bounding box in printed mm; null when Revit gave none.</summary>
    public double? MinXMm { get; set; }
    public double? MinYMm { get; set; }
}

/// <summary>
/// Pure planning for revit_populate_legend: argument parsing, type matching, sort order, grid cells, labels, what
/// clearExisting deletes and the approval text. No Revit types; unit tested.
/// </summary>
public static class LegendPlanning
{
    public static readonly string[] SortKeys = { "request", "family", "type", "category", "label" };

    // ── Arguments ────────────────────────────────────────────────────────────

    public static LegendPopulateRequest Parse(IReadOnlyDictionary<string, object?> args, List<string> errors)
    {
        var request = new LegendPopulateRequest();
        var legend = Token(args, "legend");
        request.Legend = legend == null ? string.Empty : Text(legend);
        if (request.Legend.Length == 0) errors.Add("Provide 'legend': the name or id of a legend view.");

        var seed = Token(args, "seedId");
        if (seed != null)
        {
            var value = Num(seed);
            if (value is > 0 && Math.Abs(value.Value - Math.Round(value.Value)) < 1e-9) request.SeedId = (long)Math.Round(value.Value);
            else errors.Add("seedId must be the element id of a legend component of the legend.");
        }

        var types = Token(args, "types");
        if (types != null)
        {
            if (types is not JArray array) errors.Add("types must be an array of type ids or {family, type} objects.");
            else
            {
                var index = 0;
                foreach (var item in array)
                {
                    var source = $"types[{index++}]";
                    var parsed = ParseTypeRef(item, source, errors);
                    if (parsed != null) request.Types.Add(parsed);
                }
            }
        }

        var category = Token(args, "category");
        if (category != null && Text(category).Length > 0) request.Category = Text(category);

        var families = Token(args, "families");
        if (families != null)
        {
            if (families is JArray list)
                request.Families.AddRange(list.Select(Text).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
            else if (Text(families).Length > 0) request.Families.Add(Text(families));
        }

        if (request.Types.Count > 0 && request.UsesFilters)
            errors.Add("Give either types or category/families, not both.");
        else if (request.Types.Count == 0 && !request.UsesFilters && !errors.Any(e => e.StartsWith("types", StringComparison.Ordinal)))
            errors.Add("Provide types (type ids or {family, type}) or a category and/or families filter.");
        if (request.Types.Count > LegendPopulateRequest.MaxTypes)
            errors.Add($"{request.Types.Count} types were given; at most {LegendPopulateRequest.MaxTypes} per call.");

        request.Columns = (int)Number(args, "columns", 1, 1, LegendPopulateRequest.MaxColumns, wholeNumber: true, errors);
        request.RowSpacingMm = Number(args, "rowSpacingMm", LegendPopulateRequest.DefaultRowSpacingMm, 0.5, 1000, false, errors);
        request.ColumnSpacingMm = Number(args, "columnSpacingMm", LegendPopulateRequest.DefaultColumnSpacingMm, 0.5, 2000, false, errors);
        request.TextOffsetMm = Number(args, "textOffsetMm", LegendPopulateRequest.DefaultTextOffsetMm, -1000, 1000, false, errors);

        var origin = Token(args, "origin");
        if (origin != null) request.Origin = ParsePoint(origin, "origin", errors);

        var label = Token(args, "label");
        if (label != null)
        {
            var text = Text(label);
            switch (text.Replace(" ", string.Empty).ToLowerInvariant())
            {
                case "":
                case "typename":
                case "type":
                    request.Label = LegendLabelMode.TypeName;
                    break;
                case "familyandtype":
                case "familytype":
                case "family:type":
                    request.Label = LegendLabelMode.FamilyAndType;
                    break;
                case "none":
                    request.Label = LegendLabelMode.None;
                    break;
                default:
                    request.Label = LegendLabelMode.Parameter;
                    request.LabelParameter = text;
                    break;
            }
        }

        var textType = Token(args, "textType");
        if (textType != null && Text(textType).Length > 0) request.TextType = Text(textType);

        request.ClearExisting = Bool(args, "clearExisting", false);
        request.DryRun = Bool(args, "dryRun", true);

        var sort = Token(args, "sortBy");
        request.SortBy = request.Types.Count > 0 ? "request" : "family";
        if (sort != null && Text(sort).Length > 0)
        {
            var key = SortKeys.FirstOrDefault(k => k.Equals(Text(sort), StringComparison.OrdinalIgnoreCase));
            if (key == null) errors.Add($"sortBy '{Text(sort)}' is not one of {string.Join(", ", SortKeys)}.");
            else request.SortBy = key;
        }
        return request;
    }

    private static LegendTypeRef? ParseTypeRef(JToken item, string source, List<string> errors)
    {
        if (item.Type is JTokenType.Integer or JTokenType.Float || item.Type == JTokenType.String && Num(item) != null)
        {
            var id = Num(item);
            if (id is > 0) return new LegendTypeRef { Source = source, Id = (long)Math.Round(id.Value) };
            errors.Add($"{source} is not a valid type id.");
            return null;
        }
        if (item is JObject obj)
        {
            var idToken = obj["id"] ?? obj["typeId"];
            if (idToken != null && idToken.Type != JTokenType.Null)
            {
                var id = Num(idToken);
                if (id is > 0) return new LegendTypeRef { Source = source, Id = (long)Math.Round(id.Value) };
                errors.Add($"{source}.id is not a valid type id.");
                return null;
            }
            var family = Text(obj["family"] ?? obj["familyName"]);
            var type = Text(obj["type"] ?? obj["typeName"]);
            if (family.Length == 0 && type.Length == 0)
            {
                errors.Add($"{source} needs an id, or a family and/or a type name.");
                return null;
            }
            return new LegendTypeRef
            {
                Source = source,
                Family = family.Length > 0 ? family : null,
                Type = type.Length > 0 ? type : null
            };
        }
        if (item.Type == JTokenType.String)
        {
            // "Family: Type" as one string.
            var text = Text(item);
            var colon = text.IndexOf(':');
            if (colon > 0)
                return new LegendTypeRef { Source = source, Family = text.Substring(0, colon).Trim(), Type = text.Substring(colon + 1).Trim() };
            return new LegendTypeRef { Source = source, Type = text };
        }
        errors.Add($"{source} must be a type id or {{family, type}}.");
        return null;
    }

    // ── Matching ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The types to place, in placement order, from what the model offers. <paramref name="problems"/> gets every
    /// reference that matches nothing (with close names) or several types; such a request must not run.
    /// <paramref name="categoryName"/> is the category's real name when the request filters by category.
    /// </summary>
    public static List<LegendTypeInfo> Select(LegendPopulateRequest request, IReadOnlyList<LegendTypeInfo> available,
        string? categoryName, List<string> problems)
    {
        var picked = new List<LegendTypeInfo>();
        var seen = new HashSet<long>();
        void Add(LegendTypeInfo t)
        {
            if (seen.Add(t.Id)) picked.Add(t);
        }

        if (request.Types.Count > 0)
        {
            foreach (var r in request.Types)
            {
                if (r.Id != null)
                {
                    var byId = available.FirstOrDefault(t => t.Id == r.Id);
                    if (byId != null) Add(byId);
                    else problems.Add($"{r.Source}: {r.Id} is not the id of a type in this document.");
                    continue;
                }

                var matches = available.Where(t =>
                        (r.Family == null || t.Family.Equals(r.Family, StringComparison.OrdinalIgnoreCase)) &&
                        (r.Type == null || t.Type.Equals(r.Type, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (matches.Count == 0)
                {
                    problems.Add($"{r.Source}: no type {Describe(r)}{Suggest(r, available)}");
                    continue;
                }
                if (r.Family == null && matches.Select(m => m.Family).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                {
                    problems.Add($"{r.Source}: the type name '{r.Type}' is in {matches.Count} families " +
                                 $"({string.Join(", ", matches.Select(m => $"'{m.Family}'").Distinct().Take(6))}); give the family too.");
                    continue;
                }
                // {family} alone: every type of the family.
                foreach (var m in matches.OrderBy(m => m.Type, StringComparer.OrdinalIgnoreCase)) Add(m);
            }
            return Sort(picked, request);
        }

        IEnumerable<LegendTypeInfo> query = available;
        if (categoryName != null)
            query = query.Where(t => t.Category.Equals(categoryName, StringComparison.OrdinalIgnoreCase));
        if (request.Families.Count > 0)
        {
            var familyNames = new HashSet<string>(available.Select(t => t.Family), StringComparer.OrdinalIgnoreCase);
            foreach (var family in request.Families.Where(f => !familyNames.Contains(f)))
                problems.Add($"families: no family '{family}'{SuggestFrom(family, familyNames)}");
            var wanted = new HashSet<string>(request.Families, StringComparer.OrdinalIgnoreCase);
            query = query.Where(t => wanted.Contains(t.Family));
        }
        foreach (var t in query) Add(t);
        if (picked.Count == 0 && problems.Count == 0)
            problems.Add("No type matches " + (categoryName != null ? $"category '{categoryName}'" : string.Empty) +
                         (categoryName != null && request.Families.Count > 0 ? " and " : string.Empty) +
                         (request.Families.Count > 0 ? $"families {string.Join(", ", request.Families.Select(f => $"'{f}'"))}" : string.Empty) + ".");
        if (picked.Count > LegendPopulateRequest.MaxTypes)
            problems.Add($"{picked.Count} types match; at most {LegendPopulateRequest.MaxTypes} per call. Narrow the filter.");
        return Sort(picked, request);
    }

    public static List<LegendTypeInfo> Sort(List<LegendTypeInfo> types, LegendPopulateRequest request)
    {
        var cmp = StringComparer.OrdinalIgnoreCase;
        return request.SortBy switch
        {
            "family" => types.OrderBy(t => t.Family, cmp).ThenBy(t => t.Type, cmp).ToList(),
            "type" => types.OrderBy(t => t.Type, cmp).ThenBy(t => t.Family, cmp).ToList(),
            "category" => types.OrderBy(t => t.Category, cmp).ThenBy(t => t.Family, cmp).ThenBy(t => t.Type, cmp).ToList(),
            "label" => types.OrderBy(t => LabelText(request, t) ?? string.Empty, cmp).ThenBy(t => t.Family, cmp).ToList(),
            _ => types
        };
    }

    private static string Describe(LegendTypeRef r) =>
        r.Family != null && r.Type != null ? $"'{r.Family}: {r.Type}'"
        : r.Family != null ? $"in a family '{r.Family}'"
        : $"named '{r.Type}'";

    private static string Suggest(LegendTypeRef r, IReadOnlyList<LegendTypeInfo> available)
    {
        if (r.Family != null && !available.Any(t => t.Family.Equals(r.Family, StringComparison.OrdinalIgnoreCase)))
            return SuggestFrom(r.Family, available.Select(t => t.Family));
        var pool = r.Family != null
            ? available.Where(t => t.Family.Equals(r.Family, StringComparison.OrdinalIgnoreCase)).Select(t => t.Type)
            : available.Select(t => t.Type);
        return SuggestFrom(r.Type ?? string.Empty, pool);
    }

    /// <summary>"; did you mean 'a', 'b'?" or ".".</summary>
    public static string SuggestFrom(string name, IEnumerable<string> pool)
    {
        var close = Close(name, pool);
        return close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(c => $"'{c}'"))}?" : ".";
    }

    public static List<string> Close(string name, IEnumerable<string> pool, int max = 5) =>
        name.Length == 0
            ? new List<string>()
            : pool.Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(p => (Name: p, Score: p.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0
                    ? 1.0
                    : FuzzyNameMatcher.Similarity(p, name)))
                .Where(p => p.Score >= 0.6)
                .OrderByDescending(p => p.Score).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Take(max).Select(p => p.Name).ToList();

    // ── Labels ───────────────────────────────────────────────────────────────

    /// <summary>The label text of a type; null = no label (label none, or the parameter has no value).</summary>
    public static string? LabelText(LegendPopulateRequest request, LegendTypeInfo type) => request.Label switch
    {
        LegendLabelMode.None => null,
        LegendLabelMode.FamilyAndType => type.Display,
        LegendLabelMode.Parameter => string.IsNullOrWhiteSpace(type.LabelParameterValue) ? null : type.LabelParameterValue!.Trim(),
        _ => type.Type
    };

    // ── Grid ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Where the grid starts when no origin is given (printed mm): under the lowest element that stays in the legend,
    /// one row down, aligned with the seed (else with the leftmost element, else 0). With clearExisting only the seed
    /// stays, so the grid starts one row under it.
    /// </summary>
    public static double[] DefaultOrigin(double[]? seedCentre, IEnumerable<LegendExistingItem> kept, double rowSpacingMm)
    {
        var keptList = kept.Where(k => k.MinYMm != null).ToList();
        var x = seedCentre?[0] ?? (keptList.Count > 0 ? keptList.Where(k => k.MinXMm != null).Select(k => k.MinXMm!.Value).DefaultIfEmpty(0).Min() : 0);
        double? bottom = keptList.Count > 0 ? keptList.Min(k => k.MinYMm!.Value) : null;
        if (seedCentre != null) bottom = bottom == null ? seedCentre[1] : Math.Min(bottom.Value, seedCentre[1]);
        var y = bottom == null ? 0 : bottom.Value - rowSpacingMm;
        return new[] { Round(x), Round(y) };
    }

    /// <summary>Row-major cells: left to right, then down. Y grows upward, as in Revit, so rows go to smaller Y.</summary>
    public static List<LegendCell> Cells(int count, int columns, double rowSpacingMm, double columnSpacingMm, double[] origin, double textOffsetMm)
    {
        columns = Math.Max(1, columns);
        var cells = new List<LegendCell>(count);
        for (var i = 0; i < count; i++)
        {
            var row = i / columns;
            var column = i % columns;
            var x = origin[0] + column * columnSpacingMm;
            var y = origin[1] - row * rowSpacingMm;
            cells.Add(new LegendCell
            {
                Index = i,
                Row = row,
                Column = column,
                XMm = Round(x),
                YMm = Round(y),
                LabelXMm = Round(x + textOffsetMm),
                LabelYMm = Round(y)
            });
        }
        return cells;
    }

    /// <summary>Printed mm to model feet on a view of this scale (1:scale).</summary>
    public static double PaperMmToModelFeet(double mm, int scale) => mm * Math.Max(1, scale) / 304.8;

    public static double ModelFeetToPaperMm(double feet, int scale) => Round(feet * 304.8 / Math.Max(1, scale));

    // ── clearExisting ────────────────────────────────────────────────────────

    /// <summary>What clearExisting deletes: every legend component, text note and detail item of the legend except the seed.</summary>
    public static List<LegendExistingItem> ToClear(IEnumerable<LegendExistingItem> existing, long? seedId) =>
        existing.Where(e => e.Id != seedId && (e.Kind == "component" || e.Kind == "textNote" || e.Kind == "detailItem")).ToList();

    // ── Approval ─────────────────────────────────────────────────────────────

    public static string ApprovalSummary(string document, string legend, int toPlace, int labels, IReadOnlyCollection<LegendExistingItem>? toDelete,
        bool clearExisting, int problems)
    {
        var text = $"Place {toPlace} symbol(s){(labels > 0 ? $" with {labels} text label(s)" : string.Empty)} in legend '{legend}' of " +
                   $"{(document.Length > 0 ? $"'{document}'" : "the active document")}.";
        if (clearExisting)
        {
            if (toDelete == null)
                text += " clearExisting=true: DELETES the legend components, text notes and detail items already in the legend (the seed stays).";
            else if (toDelete.Count > 0)
            {
                var byKind = toDelete.GroupBy(d => d.Kind).Select(g => $"{g.Count()} {KindLabel(g.Key)}");
                text += $" clearExisting=true: DELETES {toDelete.Count} element(s) already in the legend ({string.Join(", ", byKind)}); the seed stays.";
            }
            else text += " clearExisting=true: nothing to delete besides the seed, which stays.";
        }
        text += " One transaction, one undo.";
        if (problems > 0) text += $" The request has {problems} problem(s) and will be refused.";
        return text;
    }

    public static string KindLabel(string kind) => kind switch
    {
        "component" => "legend component(s)",
        "textNote" => "text note(s)",
        "detailItem" => "detail item(s)",
        _ => kind
    };

    // ── JSON helpers ─────────────────────────────────────────────────────────

    private static double Number(IReadOnlyDictionary<string, object?> args, string key, double fallback, double min, double max, bool wholeNumber, List<string> errors)
    {
        var token = Token(args, key);
        if (token == null) return fallback;
        var value = Num(token);
        if (value == null || value < min || value > max || wholeNumber && Math.Abs(value.Value - Math.Round(value.Value)) > 1e-9)
        {
            errors.Add($"{key} must be {(wholeNumber ? "a whole number" : "a number")} from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}.");
            return fallback;
        }
        return value.Value;
    }

    private static double[]? ParsePoint(JToken token, string key, List<string> errors)
    {
        double? x = null, y = null;
        if (token is JArray a && a.Count == 2) { x = Num(a[0]); y = Num(a[1]); }
        else if (token is JObject o) { x = Num(o["xMm"] ?? o["x"]); y = Num(o["yMm"] ?? o["y"]); }
        else if (token.Type == JTokenType.String)
        {
            var parts = Text(token).Split(',');
            if (parts.Length == 2) { x = Num(new JValue(parts[0].Trim())); y = Num(new JValue(parts[1].Trim())); }
        }
        if (x == null || y == null)
        {
            errors.Add($"{key} must be [xMm, yMm] or {{xMm, yMm}} in printed mm.");
            return null;
        }
        return new[] { x.Value, y.Value };
    }

    private static bool Bool(IReadOnlyDictionary<string, object?> args, string key, bool fallback)
    {
        var token = Token(args, key);
        if (token == null) return fallback;
        if (token.Type == JTokenType.Boolean) return (bool)token;
        var text = Text(token).ToLowerInvariant();
        return text is "true" or "1" or "yes" || (text is not ("false" or "0" or "no") && fallback);
    }

    internal static JToken? Token(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var raw) || raw == null) return null;
        JToken? token = raw switch
        {
            JToken t => t,
            string s => TryParse(s),
            _ => JToken.FromObject(raw)
        };
        return token == null || token.Type == JTokenType.Null ? null : token;
    }

    private static JToken? TryParse(string s)
    {
        var trimmed = s.Trim();
        if (trimmed.Length == 0) return null;
        if (trimmed.StartsWith("[", StringComparison.Ordinal) || trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            try { return JToken.Parse(trimmed); }
            catch { /* plain text */ }
        }
        return new JValue(s);
    }

    internal static string Text(JToken? token) =>
        token == null || token.Type == JTokenType.Null ? string.Empty
        : token.Type == JTokenType.String ? ((string?)token ?? string.Empty).Trim()
        : token.Type is JTokenType.Integer or JTokenType.Float ? Convert.ToString(((JValue)token).Value, CultureInfo.InvariantCulture) ?? string.Empty
        : token.ToString().Trim();

    internal static double? Num(JToken? token)
    {
        if (token == null) return null;
        if (token.Type is JTokenType.Integer or JTokenType.Float) return (double)token;
        if (token.Type == JTokenType.String
            && double.TryParse((string?)token, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return v;
        return null;
    }

    private static double Round(double v) => Math.Round(v, 2);
}
