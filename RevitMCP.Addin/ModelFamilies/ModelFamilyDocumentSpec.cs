using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.ModelFamilies;

/// <summary>How a family setting's value is written: a yes/no parameter, or a text naming an enum value.</summary>
public enum ModelFamilySettingKind
{
    Bool,
    PartType
}

/// <summary>One entry of the family settings table: the argument key, the built-in parameter behind it and its label.</summary>
public sealed class ModelFamilySettingInfo
{
    public string Key { get; init; } = string.Empty;
    /// <summary>The BuiltInParameter name, parsed on the Revit side.</summary>
    public string BuiltInParameter { get; init; } = string.Empty;
    public ModelFamilySettingKind Kind { get; init; }
    /// <summary>What Revit calls it in the Family Category and Parameters dialog.</summary>
    public string Label { get; init; } = string.Empty;
}

/// <summary>A setting the caller asked for, with its parsed value.</summary>
public sealed class ModelFamilySettingRequest
{
    public ModelFamilySettingInfo Info { get; init; } = null!;
    public bool? BoolValue { get; init; }
    /// <summary>The text of a part type, as given; matched against Revit's enum names on the Revit side.</summary>
    public string TextValue { get; init; } = string.Empty;

    public string Describe() => Info.Kind == ModelFamilySettingKind.Bool
        ? $"{Info.Key}={(BoolValue == true ? "true" : "false")}"
        : $"{Info.Key}='{TextValue}'";
}

/// <summary>Element counts of a family before and after an edit: what stayed, and what changed.</summary>
public sealed class ModelFamilyCountDiff
{
    public SortedDictionary<string, int> Unchanged { get; } = new(StringComparer.Ordinal);
    public List<(string Key, int Before, int After)> Changed { get; } = new();

    public JObject ToJson() => new()
    {
        ["unchangedCounts"] = JObject.FromObject(Unchanged),
        ["changedCounts"] = new JArray(Changed.Select(c => new JObject { ["key"] = c.Key, ["before"] = c.Before, ["after"] = c.After }))
    };
}

/// <summary>
/// Pure logic of the model-family document tools (#169): the <c>category</c>, <c>settings</c>, <c>include</c>,
/// <c>limit</c> and id arguments, the table of family settings, category and part-type name matching, the placement
/// rule for "can it be placed without a host", and the before/after element count diff that shows unrelated content
/// was preserved.
/// </summary>
public static class ModelFamilyDocumentSpec
{
    public const int DefaultLimit = 200;
    public const int MaxLimit = 2000;

    /// <summary>The settings an edit can change, in the order they are applied.</summary>
    public static readonly IReadOnlyList<ModelFamilySettingInfo> Settings = new[]
    {
        new ModelFamilySettingInfo { Key = "workPlaneBased", BuiltInParameter = "FAMILY_WORK_PLANE_BASED", Kind = ModelFamilySettingKind.Bool, Label = "Work Plane-Based" },
        new ModelFamilySettingInfo { Key = "alwaysVertical", BuiltInParameter = "FAMILY_ALWAYS_VERTICAL", Kind = ModelFamilySettingKind.Bool, Label = "Always Vertical" },
        new ModelFamilySettingInfo { Key = "shared", BuiltInParameter = "FAMILY_SHARED", Kind = ModelFamilySettingKind.Bool, Label = "Shared" },
        new ModelFamilySettingInfo { Key = "cutsWithVoids", BuiltInParameter = "FAMILY_ALLOW_CUT_WITH_VOIDS", Kind = ModelFamilySettingKind.Bool, Label = "Cut with Voids When Loaded" },
        new ModelFamilySettingInfo { Key = "roomCalculationPoint", BuiltInParameter = "ROOM_CALCULATION_POINT", Kind = ModelFamilySettingKind.Bool, Label = "Room Calculation Point" },
        new ModelFamilySettingInfo { Key = "partType", BuiltInParameter = "FAMILY_CONTENT_PART_TYPE", Kind = ModelFamilySettingKind.PartType, Label = "Part Type" }
    };

    public static string[] SettingKeys => Settings.Select(s => s.Key).ToArray();

    // ── Arguments ────────────────────────────────────────────────────────────

    /// <summary>The <c>category</c> argument: a built-in name or a category name, trimmed; empty when not given.</summary>
    public static string ParseCategory(JToken? token, string source, List<string> problems)
    {
        if (token == null) return string.Empty;
        if (token.Type != JTokenType.String)
        {
            problems.Add($"{source}: give the category as text, a built-in name such as 'OST_ElectricalFixtures' or a category name such as 'Electrical Fixtures'.");
            return string.Empty;
        }
        return ((string?)token)?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// The <c>settings</c> argument: an object with optional <see cref="Settings"/> keys. Unknown keys and wrong value
    /// types are problems. Returned in table order, so the edit applies them the same way every time.
    /// </summary>
    public static List<ModelFamilySettingRequest> ParseSettings(JToken? token, string source, List<string> problems)
    {
        var result = new List<ModelFamilySettingRequest>();
        if (token == null) return result;
        if (token is not JObject obj)
        {
            problems.Add($"{source}: must be an object such as {{\"workPlaneBased\": false, \"shared\": true}}. Keys: {string.Join(", ", SettingKeys)}.");
            return result;
        }
        foreach (var property in obj.Properties())
        {
            if (SettingKeys.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) continue;
            var close = ModelFamilyJson.Suggest(property.Name, SettingKeys);
            problems.Add($"{source}.{property.Name}: unknown setting{(close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(c => $"'{c}'"))}?" : ".")} Known: {string.Join(", ", SettingKeys)}.");
        }
        foreach (var info in Settings)
        {
            var value = ModelFamilyJson.Get(obj, info.Key);
            if (value == null) continue;
            if (info.Kind == ModelFamilySettingKind.Bool)
            {
                var flag = ModelFamilyJson.Bool(obj, info.Key, source, problems);
                if (flag != null) result.Add(new ModelFamilySettingRequest { Info = info, BoolValue = flag });
            }
            else
            {
                var text = value.Type == JTokenType.String ? ((string?)value)?.Trim() ?? string.Empty : string.Empty;
                if (text.Length == 0) problems.Add($"{source}.{info.Key}: give the part type as text, for example 'Normal' or 'Junction Box'.");
                else result.Add(new ModelFamilySettingRequest { Info = info, TextValue = text });
            }
        }
        return result;
    }

    /// <summary>
    /// The <c>include</c> argument of the read tool: section names (a list, or one comma-separated text), returned in
    /// the order of <paramref name="known"/>. Not given or empty: every known section. An unknown name is a problem.
    /// </summary>
    public static List<string> ParseInclude(JToken? token, IReadOnlyList<string> known, List<string> problems)
    {
        if (token == null) return known.ToList();
        var names = new List<string>();
        switch (token)
        {
            case JArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i].Type == JTokenType.String) names.Add(((string?)array[i])?.Trim() ?? string.Empty);
                    else problems.Add($"include[{i}]: must be a section name.");
                }
                break;
            case JValue { Type: JTokenType.String } text:
                names.AddRange(((string?)text ?? string.Empty).Split(',').Select(n => n.Trim()));
                break;
            default:
                problems.Add($"include: must be a list of section names. Known: {string.Join(", ", known)}.");
                return new List<string>();
        }
        names = names.Where(n => n.Length > 0).ToList();
        if (names.Count == 0) return known.ToList();
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (known.Contains(name, StringComparer.OrdinalIgnoreCase)) wanted.Add(name);
            else
            {
                var close = ModelFamilyJson.Suggest(name, known);
                problems.Add($"include: unknown section '{name}'{(close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(c => $"'{c}'"))}?" : ".")} Known: {string.Join(", ", known)}.");
            }
        }
        return known.Where(wanted.Contains).ToList();
    }

    /// <summary>The <c>limit</c> argument: most items per list, default 200, clamped to 1..2000.</summary>
    public static int ParseLimit(JToken? token, List<string> problems)
    {
        if (token == null) return DefaultLimit;
        var value = ModelFamilyJson.AsNumber(token);
        if (value == null)
        {
            problems.Add($"limit: must be a whole number from 1 to {MaxLimit}.");
            return DefaultLimit;
        }
        var whole = (long)Math.Round(value.Value);
        return (int)Math.Max(1, Math.Min(MaxLimit, whole));
    }

    /// <summary>Element ids: a list of numbers (or numeric texts), or one number. Bad entries are problems.</summary>
    public static List<long> ParseIds(JToken? token, string source, List<string>? problems)
    {
        var result = new List<long>();
        if (token == null) return result;
        if (token is not JArray array)
        {
            if (ModelFamilyJson.AsId(token) is { } single) result.Add(single);
            else problems?.Add($"{source}: must be a list of element ids.");
            return result;
        }
        for (var i = 0; i < array.Count; i++)
        {
            if (ModelFamilyJson.AsId(array[i]) is { } id) { if (!result.Contains(id)) result.Add(id); }
            else problems?.Add($"{source}[{i}]: must be an element id (a whole number).");
        }
        return result;
    }

    // ── Names ────────────────────────────────────────────────────────────────

    private static string StripOst(string value)
    {
        var text = value.Trim();
        return text.StartsWith("OST_", StringComparison.OrdinalIgnoreCase) ? text.Substring(4) : text;
    }

    /// <summary>A name reduced for comparison: without the OST_ prefix, spaces, underscores and hyphens, lower case.</summary>
    public static string NormalizeName(string value)
    {
        var text = value.Trim();
        if (text.StartsWith("OST_", StringComparison.OrdinalIgnoreCase)) text = text.Substring(4);
        var chars = text.Where(c => c != ' ' && c != '_' && c != '-').Select(char.ToLowerInvariant).ToArray();
        return new string(chars);
    }

    /// <summary>
    /// Finds a category among <paramref name="candidates"/> (its built-in name, or null, and its display name).
    /// Accepts <c>OST_ElectricalFixtures</c>, <c>ElectricalFixtures</c>, <c>Electrical Fixtures</c>, any case. An exact
    /// built-in or display name wins over a normalised match. Returns the index, or -1 with <paramref name="error"/>.
    /// </summary>
    public static int MatchCategory(string wanted, IReadOnlyList<(string? BuiltIn, string Name)> candidates, out string? error)
    {
        error = null;
        var text = wanted.Trim();
        if (text.Length == 0)
        {
            error = "give a category.";
            return -1;
        }
        int Single(Func<(string? BuiltIn, string Name), bool> predicate, out bool ambiguous)
        {
            var hits = new List<int>();
            for (var i = 0; i < candidates.Count; i++) if (predicate(candidates[i])) hits.Add(i);
            ambiguous = hits.Count > 1;
            return hits.Count == 1 ? hits[0] : -1;
        }

        var exact = Single(c => c.BuiltIn != null && string.Equals(c.BuiltIn, text, StringComparison.OrdinalIgnoreCase), out _);
        if (exact >= 0) return exact;
        exact = Single(c => string.Equals(c.Name, text, StringComparison.OrdinalIgnoreCase), out var nameAmbiguous);
        if (exact >= 0) return exact;
        var key = NormalizeName(text);
        var normal = Single(c => c.BuiltIn != null && NormalizeName(c.BuiltIn) == key || NormalizeName(c.Name) == key, out var normalAmbiguous);
        if (normal >= 0) return normal;
        if (nameAmbiguous || normalAmbiguous)
        {
            error = $"'{text}' matches more than one category; pass its built-in name (OST_…).";
            return -1;
        }
        // Suggest by the name without "OST_": every built-in name shares that prefix, which would make all of them look close.
        var pool = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var shown = candidate.BuiltIn ?? candidate.Name;
            var bare = StripOst(shown);
            if (bare.Length > 0 && !pool.ContainsKey(bare)) pool[bare] = shown;
        }
        var close = ModelFamilyJson.Suggest(StripOst(text), pool.Keys).Select(k => pool[k]).ToList();
        error = $"the family document has no category '{text}'" +
                (close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(c => $"'{c}'"))}?" : ". Pass a built-in name such as 'OST_ElectricalFixtures' or 'OST_GenericModel'.");
        return -1;
    }

    /// <summary>
    /// An enum value name for a text such as <c>Junction Box</c>, <c>junction_box</c> or <c>JunctionBox</c>; null when
    /// none matches. Used for the part type, whose names come from Revit's PartType enum.
    /// </summary>
    public static string? MatchEnumName(string wanted, IEnumerable<string> names)
    {
        var key = NormalizeName(wanted);
        if (key.Length == 0) return null;
        var all = names.ToList();
        return all.FirstOrDefault(n => string.Equals(n, wanted.Trim(), StringComparison.Ordinal))
               ?? all.FirstOrDefault(n => NormalizeName(n) == key);
    }

    // ── Placement ────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether a family with this FamilyPlacementType needs a host (a wall, ceiling, floor or face) to be placed. Only
    /// <c>OneLevelBasedHosted</c> does; a work-plane-based family can be placed on a level or work plane, and the
    /// non-hosted types on a level. Null for a placement type this table does not know.
    /// </summary>
    public static bool? PlacementNeedsHost(string placement) => placement switch
    {
        "OneLevelBasedHosted" => true,
        "OneLevelBased" or "WorkPlaneBased" or "TwoLevelsBased" or "CurveBased" or "CurveDrivenStructural" or "Adaptive" => false,
        "ViewBased" or "CurveBasedDetail" => false,
        _ => null
    };

    /// <summary>One sentence on how instances of the family are placed, for the result.</summary>
    public static string PlacementNote(string placement, bool? workPlaneBased)
    {
        switch (PlacementNeedsHost(placement))
        {
            case true:
                return "Hosted: an instance needs a host (wall, ceiling, floor or face). The category cannot remove the host; start from a non-hosted template such as Metric Generic Model.rft.";
            case false when placement == "OneLevelBased":
                return workPlaneBased == true
                    ? "Not hosted: an instance is placed on a level or work plane without selecting a host face."
                    : "Not hosted: an instance is placed on a level without selecting a host face.";
            case false when placement == "WorkPlaneBased":
                return "Not hosted: an instance is placed on a level or work plane (or a face, if the user chooses one); no host face is required.";
            case false when placement is "ViewBased" or "CurveBasedDetail":
                return "A view-specific family: instances are placed in a view, not in the model.";
            case false:
                return $"Not hosted ({placement}).";
            default:
                return $"Placement type '{placement}': check how instances are placed.";
        }
    }

    // ── Preserved content ────────────────────────────────────────────────────

    /// <summary>Compares element counts before and after an edit. A key missing on one side counts as 0.</summary>
    public static ModelFamilyCountDiff DiffCounts(IReadOnlyDictionary<string, int> before, IReadOnlyDictionary<string, int> after)
    {
        var diff = new ModelFamilyCountDiff();
        foreach (var key in before.Keys.Concat(after.Keys).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
        {
            var b = before.TryGetValue(key, out var x) ? x : 0;
            var a = after.TryGetValue(key, out var y) ? y : 0;
            if (a == b) diff.Unchanged[key] = a;
            else diff.Changed.Add((key, b, a));
        }
        return diff;
    }

    /// <summary>Warnings for counts that changed although the edit was not meant to change them.</summary>
    public static List<string> CountWarnings(ModelFamilyCountDiff diff, IEnumerable<string>? expected = null)
    {
        var allowed = new HashSet<string>(expected ?? Array.Empty<string>(), StringComparer.Ordinal);
        return diff.Changed.Where(c => !allowed.Contains(c.Key))
            .Select(c => $"The number of {c.Key} elements changed from {c.Before} to {c.After}, although this edit names none of them; check the family.")
            .ToList();
    }

    /// <summary>Names present before and missing after, for the warning about parameters a category change dropped.</summary>
    public static List<string> Disappeared(IEnumerable<string> before, IEnumerable<string> after)
    {
        var now = new HashSet<string>(after, StringComparer.Ordinal);
        return before.Where(n => !now.Contains(n)).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    /// <summary>One line for an approval summary or preview: what the create or edit changes.</summary>
    public static string DescribeChanges(string category, IReadOnlyCollection<ModelFamilySettingRequest> settings)
    {
        var parts = new List<string>();
        if (category.Length > 0) parts.Add($"set the category to '{category}'");
        if (settings.Count > 0) parts.Add($"set {string.Join(", ", settings.Select(s => s.Describe()))}");
        return parts.Count == 0 ? "no setting changes" : string.Join("; ", parts);
    }
}
