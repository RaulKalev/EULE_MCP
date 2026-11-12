using System.Globalization;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Styles;

/// <summary>The two kinds of style revit_get_style_usage and revit_delete_styles handle.</summary>
public static class StyleKinds
{
    public const string LineStyle = "lineStyle";
    public const string FilledRegionType = "filledRegionType";
}

/// <summary>How an element uses a style.</summary>
public static class StyleUseKinds
{
    /// <summary>A boundary edge of a filled or masking region (its sketch line).</summary>
    public const string RegionBoundary = "filledRegionBoundary";
    public const string FilledRegion = "filledRegion";
    public const string MaskingRegion = "maskingRegion";
    // Curves use their CurveElementType: DetailCurve, ModelCurve, SymbolicCurve, …
}

/// <summary>A line style (a subcategory of Lines, by its category id) or a filled region type, as plain data.</summary>
public sealed class StyleInfo
{
    public string Kind { get; set; } = StyleKinds.LineStyle;
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>One of Revit's own line styles (negative id): never renamed or deleted.</summary>
    public bool BuiltIn { get; set; }
}

/// <summary>One element that uses a style.</summary>
public sealed class StyleUse
{
    public long ElementId { get; set; }
    /// <summary>The style used: a line style's category id, or a filled region type id.</summary>
    public long StyleId { get; set; }
    public string StyleKind { get; set; } = StyleKinds.LineStyle;
    /// <summary>DetailCurve, ModelCurve, …, filledRegionBoundary, filledRegion, maskingRegion.</summary>
    public string Use { get; set; } = string.Empty;
    /// <summary>The owner view; null for model elements.</summary>
    public long? ViewId { get; set; }
    public string? ViewName { get; set; }
    /// <summary>For a region boundary edge: the region it bounds.</summary>
    public long? RegionId { get; set; }
}

public sealed class StyleViewUsage
{
    public long? ViewId { get; set; }
    public string? ViewName { get; set; }
    public int Count { get; set; }
    public List<long> ElementIds { get; set; } = new();
}

public sealed class StyleUsage
{
    public StyleInfo Style { get; set; } = new();
    public int Count { get; set; }
    /// <summary>Count per use (DetailCurve, filledRegionBoundary, …).</summary>
    public SortedDictionary<string, int> ByUse { get; set; } = new(StringComparer.Ordinal);
    public List<StyleViewUsage> ByView { get; set; } = new();
    /// <summary>True when some element ids were left out of ByView by the id cap.</summary>
    public bool IdsTruncated { get; set; }
}

/// <summary>The request of revit_preview_delete_styles and revit_delete_styles.</summary>
public sealed class StyleDeleteRequest
{
    public List<string> LineStyles { get; set; } = new();
    public List<string> FilledRegionTypes { get; set; } = new();
    public string? ReplaceLineStyle { get; set; }
    public string? ReplaceFilledRegionType { get; set; }
}

public sealed class StyleDeleteItem
{
    public string Kind { get; set; } = StyleKinds.LineStyle;
    /// <summary>The name or id as given.</summary>
    public string Reference { get; set; } = string.Empty;
    public StyleInfo? Style { get; set; }
    /// <summary>delete | refused | not_found.</summary>
    public string Status { get; set; } = StyleDeleteStatus.Delete;
    public string? Code { get; set; }
    public string? Reason { get; set; }
    /// <summary>Elements that use it (reassigned first when there is a replacement).</summary>
    public int Uses { get; set; }
    /// <summary>The style its users move to; null when it is unused or refused.</summary>
    public StyleInfo? ReassignTo { get; set; }
}

public static class StyleDeleteStatus
{
    public const string Delete = "delete";
    public const string Refused = "refused";
    public const string NotFound = "not_found";
}

public static class StyleDeleteCodes
{
    public const string NotFound = "style_not_found";
    public const string BuiltIn = "builtin_style";
    public const string InUse = "style_in_use";
    public const string LastFilledRegionType = "last_filled_region_type";
    public const string ListedTwice = "listed_twice";
    public const string ReplacementNotFound = "replacement_not_found";
    public const string ReplacementDeleted = "replacement_deleted";
}

public sealed class StyleDeletePlan
{
    public List<StyleDeleteItem> Items { get; } = new();
    public StyleInfo? LineReplacement { get; set; }
    public StyleInfo? RegionReplacement { get; set; }
    /// <summary>Why nothing may be deleted: any problem refuses the whole request.</summary>
    public List<string> Problems { get; } = new();
    public List<string> Warnings { get; } = new();
    /// <summary>The uses to move to a replacement before the styles are deleted.</summary>
    public List<StyleUse> Reassign { get; } = new();

    public bool Ready => Problems.Count == 0 && Items.Count > 0 && Items.All(i => i.Status == StyleDeleteStatus.Delete);
}

/// <summary>
/// Style usage and delete planning for revit_get_style_usage / revit_delete_styles, on plain data the Revit side
/// collects. Pure: no Revit types, unit tested.
/// </summary>
public static class StyleUsagePlanner
{
    public const int DefaultIdCap = 50;
    public const int MaxIdCap = 1000;
    public const int MaxStylesPerRequest = 200;

    /// <summary>What the usage count does not see: said in every usage and delete result.</summary>
    public static readonly IReadOnlyList<string> NotCounted = new[]
    {
        "lines and filled regions inside loaded families (a family's line styles with the same name are listed in the project's Lines)",
        "view templates, view filters and graphic overrides, including edges changed with the Linework tool",
        "line styles or filled region types named in parameters, schedules or other add-ins' settings",
        "linked models"
    };

    /// <summary>A style by id (a whole number) or by name, ignoring case; null when there is none.</summary>
    public static StyleInfo? Find(string reference, IEnumerable<StyleInfo> styles)
    {
        var text = reference.Trim();
        var list = styles as IReadOnlyCollection<StyleInfo> ?? styles.ToList();
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            var byId = list.FirstOrDefault(s => s.Id == id);
            if (byId != null) return byId;
        }
        return list.FirstOrDefault(s => string.Equals(s.Name, text, StringComparison.Ordinal))
               ?? list.FirstOrDefault(s => string.Equals(s.Name, text, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Up to <paramref name="max"/> names close to <paramref name="reference"/>: containing it, contained in it, or a few edits away.</summary>
    public static List<string> CloseNames(string reference, IEnumerable<string> names, int max = 5)
    {
        var text = reference.Trim();
        if (text.Length == 0) return new List<string>();
        return names.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => (Name: n, Score: Closeness(text, n)))
            .Where(x => x.Score < int.MaxValue)
            .OrderBy(x => x.Score).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Take(max).Select(x => x.Name).ToList();
    }

    /// <summary>" Did you mean 'A', 'B'?" or "", for a not-found message.</summary>
    public static string DidYouMean(string reference, IEnumerable<string> names)
    {
        var close = CloseNames(reference, names);
        return close.Count > 0 ? $" Did you mean {string.Join(", ", close.Select(n => $"'{n}'"))}?" : string.Empty;
    }

    private static int Closeness(string reference, string name)
    {
        if (name.IndexOf(reference, StringComparison.OrdinalIgnoreCase) >= 0 || reference.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
            return Math.Abs(name.Length - reference.Length);
        var limit = Math.Max(1, Math.Min(3, reference.Length / 3));
        var distance = EditDistance(reference.ToLowerInvariant(), name.ToLowerInvariant(), limit);
        return distance <= limit ? 100 + distance : int.MaxValue;
    }

    private static int EditDistance(string a, string b, int limit)
    {
        if (Math.Abs(a.Length - b.Length) > limit) return int.MaxValue;
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    /// <summary>One style's uses: counts per use, and per view (model elements under a null view) with up to <paramref name="idCap"/> ids in all.</summary>
    public static StyleUsage Summarise(StyleInfo style, IEnumerable<StyleUse> uses, int idCap)
    {
        var mine = uses.Where(u => u.StyleId == style.Id && u.StyleKind == style.Kind).ToList();
        var usage = new StyleUsage { Style = style, Count = mine.Count };
        foreach (var group in mine.GroupBy(u => u.Use, StringComparer.Ordinal))
            usage.ByUse[group.Key] = group.Count();

        var remaining = Math.Max(0, idCap);
        foreach (var group in mine.GroupBy(u => u.ViewId).OrderByDescending(g => g.Count()).ThenBy(g => g.Key ?? long.MinValue))
        {
            var ids = group.Select(u => u.ElementId).Distinct().OrderBy(i => i).ToList();
            var shown = ids.Take(remaining).ToList();
            remaining -= shown.Count;
            if (shown.Count < ids.Count) usage.IdsTruncated = true;
            usage.ByView.Add(new StyleViewUsage
            {
                ViewId = group.Key,
                ViewName = group.Key == null ? "(model)" : group.Select(u => u.ViewName).FirstOrDefault(n => !string.IsNullOrEmpty(n)),
                Count = group.Count(),
                ElementIds = shown
            });
        }
        return usage;
    }

    /// <summary>
    /// What revit_delete_styles would do. Refused, and the whole request with it: a name that is not found, one of
    /// Revit's own line styles, a style still in use without a replacement of its kind, deleting every filled region
    /// type, and a replacement that is missing or deleted itself.
    /// </summary>
    public static StyleDeletePlan PlanDelete(
        StyleDeleteRequest request, IReadOnlyList<StyleInfo> lineStyles, IReadOnlyList<StyleInfo> regionTypes, IReadOnlyList<StyleUse> uses)
    {
        var plan = new StyleDeletePlan();

        StyleInfo? Replacement(string? reference, IReadOnlyList<StyleInfo> available, string what)
        {
            if (string.IsNullOrWhiteSpace(reference)) return null;
            var found = Find(reference!, available);
            if (found == null)
                plan.Problems.Add($"{StyleDeleteCodes.ReplacementNotFound}: replaceWith.{what} '{reference}' is not in the document." +
                                  DidYouMean(reference!, available.Select(s => s.Name)));
            return found;
        }

        plan.LineReplacement = Replacement(request.ReplaceLineStyle, lineStyles, "lineStyle");
        plan.RegionReplacement = Replacement(request.ReplaceFilledRegionType, regionTypes, "filledRegionType");

        AddItems(plan, request.LineStyles, StyleKinds.LineStyle, lineStyles, plan.LineReplacement, uses);
        AddItems(plan, request.FilledRegionTypes, StyleKinds.FilledRegionType, regionTypes, plan.RegionReplacement, uses);

        var deletedRegionTypes = plan.Items.Where(i => i.Kind == StyleKinds.FilledRegionType && i.Style != null).Select(i => i.Style!.Id).Distinct().Count();
        if (regionTypes.Count > 0 && deletedRegionTypes >= regionTypes.Count)
        {
            foreach (var item in plan.Items.Where(i => i.Kind == StyleKinds.FilledRegionType && i.Status == StyleDeleteStatus.Delete))
                Refuse(item, StyleDeleteCodes.LastFilledRegionType, "A document keeps at least one filled region type; this request deletes every one.");
        }

        if (plan.Items.Count == 0 && plan.Problems.Count == 0)
            plan.Problems.Add("Give lineStyles and/or filledRegionTypes: the names (or ids) of the styles to delete.");
        if (plan.LineReplacement != null && request.LineStyles.Count == 0)
            plan.Warnings.Add("replaceWith.lineStyle is given but no line style is deleted.");
        if (plan.RegionReplacement != null && request.FilledRegionTypes.Count == 0)
            plan.Warnings.Add("replaceWith.filledRegionType is given but no filled region type is deleted.");

        foreach (var item in plan.Items.Where(i => i.Status != StyleDeleteStatus.Delete))
            plan.Problems.Add($"{item.Code}: {item.Reason}");

        if (plan.Problems.Count == 0)
        {
            var moving = new HashSet<(string, long)>(plan.Items.Where(i => i.ReassignTo != null).Select(i => (i.Kind, i.Style!.Id)));
            plan.Reassign.AddRange(uses.Where(u => moving.Contains((u.StyleKind, u.StyleId))));
        }
        return plan;
    }

    private static void AddItems(
        StyleDeletePlan plan, List<string> references, string kind, IReadOnlyList<StyleInfo> available, StyleInfo? replacement, IReadOnlyList<StyleUse> uses)
    {
        var what = kind == StyleKinds.LineStyle ? "line style" : "filled region type";
        var seen = new HashSet<long>();
        foreach (var reference in references)
        {
            var item = new StyleDeleteItem { Kind = kind, Reference = reference };
            plan.Items.Add(item);
            var style = Find(reference, available);
            if (style == null)
            {
                item.Status = StyleDeleteStatus.NotFound;
                item.Code = StyleDeleteCodes.NotFound;
                item.Reason = $"No {what} '{reference}'." + DidYouMean(reference, available.Select(s => s.Name));
                continue;
            }
            item.Style = style;
            item.Uses = uses.Count(u => u.StyleKind == kind && u.StyleId == style.Id);

            if (!seen.Add(style.Id))
                Refuse(item, StyleDeleteCodes.ListedTwice, $"The {what} '{style.Name}' is listed twice.");
            else if (style.BuiltIn)
                Refuse(item, StyleDeleteCodes.BuiltIn, $"'{style.Name}' is one of Revit's own line styles and cannot be deleted.");
            else if (replacement != null && replacement.Id == style.Id)
                Refuse(item, StyleDeleteCodes.ReplacementDeleted, $"'{style.Name}' is the replacement (replaceWith) and is deleted too.");
            else if (item.Uses > 0 && replacement == null)
                Refuse(item, StyleDeleteCodes.InUse,
                    $"'{style.Name}' is used by {item.Uses} element(s); give replaceWith.{(kind == StyleKinds.LineStyle ? "lineStyle" : "filledRegionType")} to move them first (revit_get_style_usage lists them).");
            else if (item.Uses > 0)
                item.ReassignTo = replacement;
        }
    }

    private static void Refuse(StyleDeleteItem item, string code, string reason)
    {
        item.Status = StyleDeleteStatus.Refused;
        item.Code = code;
        item.Reason = reason;
        item.ReassignTo = null;
    }

    /// <summary>"Delete line styles 'A' (12 uses → 'B'), 'C' and filled region types …", for approval summaries.</summary>
    public static string Describe(StyleDeletePlan plan, int max = 8)
    {
        string Part(string kind, string label)
        {
            var items = plan.Items.Where(i => i.Kind == kind).ToList();
            if (items.Count == 0) return string.Empty;
            var text = string.Join(", ", items.Take(max).Select(i =>
                $"'{i.Style?.Name ?? i.Reference}'" +
                (i.Status != StyleDeleteStatus.Delete ? $" ({i.Code})"
                    : i.ReassignTo != null ? $" ({i.Uses} use(s) moved to '{i.ReassignTo.Name}')"
                    : " (unused)")));
            return $"{items.Count} {label}: {text}{(items.Count > max ? ", …" : string.Empty)}";
        }
        return string.Join("; ", new[] { Part(StyleKinds.LineStyle, "line style(s)"), Part(StyleKinds.FilledRegionType, "filled region type(s)") }
            .Where(p => p.Length > 0));
    }
}

/// <summary>Arguments of the usage and delete tools. Pure.</summary>
public static class StyleRequestParser
{
    /// <summary>lineStyles, filledRegionTypes: arrays of names or ids (a single string is one name).</summary>
    public static List<string> References(IReadOnlyDictionary<string, object?> args, string key, List<string> errors)
    {
        var result = new List<string>();
        if (!args.TryGetValue(key, out var raw) || raw == null) return result;
        JToken? token = raw switch
        {
            JToken t => t,
            string s when s.Trim().StartsWith("[", StringComparison.Ordinal) => TryParse(s),
            string s => new JValue(s),
            _ => TryFrom(raw)
        };
        if (token == null || token.Type == JTokenType.Null) return result;
        var items = token is JArray array ? array.ToList() : new List<JToken> { token };
        var index = 0;
        foreach (var item in items)
        {
            var text = item.Type is JTokenType.String or JTokenType.Integer ? item.ToString().Trim() : string.Empty;
            if (item.Type == JTokenType.Null || (item.Type == JTokenType.String && text.Length == 0)) { index++; continue; }
            if (text.Length == 0) errors.Add($"{key}[{index}] must be a name or an id.");
            else result.Add(text);
            index++;
        }
        if (result.Count > StyleUsagePlanner.MaxStylesPerRequest)
            errors.Add($"{result.Count} {key} were given; at most {StyleUsagePlanner.MaxStylesPerRequest} per call.");
        return result;
    }

    public static StyleDeleteRequest ParseDelete(IReadOnlyDictionary<string, object?> args, List<string> errors)
    {
        var request = new StyleDeleteRequest
        {
            LineStyles = References(args, "lineStyles", errors),
            FilledRegionTypes = References(args, "filledRegionTypes", errors)
        };
        if (args.TryGetValue("replaceWith", out var raw) && raw != null)
        {
            JToken? token = raw switch
            {
                JToken t => t,
                string s when s.Trim().Length == 0 => null,
                string s => TryParse(s),
                _ => TryFrom(raw)
            };
            if (token is JObject obj)
            {
                request.ReplaceLineStyle = Text(obj["lineStyle"]);
                request.ReplaceFilledRegionType = Text(obj["filledRegionType"]);
            }
            else if (token != null && token.Type != JTokenType.Null)
                errors.Add("replaceWith must be an object {lineStyle?, filledRegionType?}.");
        }
        return request;
    }

    private static string? Text(JToken? token)
    {
        if (token == null || token.Type == JTokenType.Null) return null;
        var text = token.ToString().Trim();
        return text.Length == 0 ? null : text;
    }

    private static JToken? TryParse(string text)
    {
        try { return JToken.Parse(text); }
        catch (Newtonsoft.Json.JsonException) { return new JValue(text); }
    }

    private static JToken? TryFrom(object value)
    {
        try { return JToken.FromObject(value); }
        catch (Exception) { return null; }
    }
}

/// <summary>Which tab of Object Styles a category belongs to. Pure.</summary>
public static class ObjectStyleKinds
{
    public const string Model = "model";
    public const string Annotation = "annotation";
    public const string Imported = "imported";

    /// <summary>
    /// model | annotation | imported, or null for internal and analytical categories (not in Object Styles). A top-level
    /// category with a positive id is an import (a DWG's layers become its subcategories), as is "Imports in Families".
    /// </summary>
    public static string? Classify(string categoryType, long id, string? builtInCategory)
    {
        if (string.Equals(builtInCategory, "OST_ImportObjectStyles", StringComparison.Ordinal)) return Imported;
        if (id > 0) return Imported;
        return categoryType switch
        {
            "Model" => Model,
            "Annotation" => Annotation,
            _ => null
        };
    }

    /// <summary>A kind filter: model, annotation, imported or empty (all); null when it is none of them.</summary>
    public static string? ParseFilter(string? text)
    {
        var value = (text ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "" or "all" => string.Empty,
            "model" => Model,
            "annotation" or "annotations" => Annotation,
            "imported" or "import" or "imports" => Imported,
            _ => null
        };
    }
}
