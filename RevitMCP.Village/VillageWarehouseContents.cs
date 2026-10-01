using Newtonsoft.Json;

namespace RevitMCP.Village;

/// <summary>
/// One line of a warehouse's contents: how many elements of the category share a type, a level
/// and a workset. Aggregate only — no element id, element name or parameter value.
/// </summary>
public sealed class VillageContentRow
{
    [JsonProperty("type")] public string Type { get; set; } = string.Empty;
    [JsonProperty("level")] public string Level { get; set; } = string.Empty;
    [JsonProperty("workset")] public string Workset { get; set; } = string.Empty;
    [JsonProperty("count")] public long Count { get; set; }
}

/// <summary>What the three row columns mean for one place ("Family", "Category", "Workset").</summary>
public sealed class VillageContentLabels
{
    [JsonProperty("type")] public string Type { get; set; } = "Type";
    [JsonProperty("level")] public string Level { get; set; } = "Level";
    [JsonProperty("workset")] public string Workset { get; set; } = "Workset";
}

/// <summary>
/// What a building holds, as type × level × workset counts. The viewer filters, groups and sorts
/// these rows itself, so one fetch serves every interaction in the panel and nothing is asked of
/// the connector while the user explores. A warehouse holds elements of one category; a landmark
/// holds whatever it stands for (sheets, views, panels…) and says so in <see cref="Labels"/> and
/// <see cref="Noun"/>.
/// </summary>
public sealed class VillageWarehouseContents
{
    /// <summary>Most rows kept per warehouse; the rest are only counted in <see cref="Omitted"/>.</summary>
    public const int MaxRows = 400;

    /// <summary>Longest name published; type names can be long and are never needed in full.</summary>
    public const int MaxNameLength = 120;

    [JsonProperty("labels")] public VillageContentLabels Labels { get; set; } = new();

    /// <summary>What the counted things are called ("elements", "sheets", "issues").</summary>
    [JsonProperty("noun")] public string Noun { get; set; } = "elements";

    [JsonProperty("rows")] public List<VillageContentRow> Rows { get; set; } = new();

    /// <summary>Every element of the category, whether or not it has a row.</summary>
    [JsonProperty("total")] public long Total { get; set; }

    /// <summary>Elements that fell outside the <see cref="MaxRows"/> largest rows.</summary>
    [JsonProperty("omitted")] public long Omitted { get; set; }

    /// <summary>Merges duplicate rows, keeps the largest <paramref name="cap"/> and counts the rest.</summary>
    public static VillageWarehouseContents Build(IEnumerable<VillageContentRow>? rows, int cap = MaxRows)
    {
        var result = new VillageWarehouseContents();
        if (rows == null) return result;

        var merged = new Dictionary<string, VillageContentRow>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row == null || row.Count <= 0) continue;
            var type = Clean(row.Type);
            var level = Clean(row.Level);
            var workset = Clean(row.Workset);
            var key = type + "\u0001" + level + "\u0001" + workset;
            if (merged.TryGetValue(key, out var existing)) existing.Count += row.Count;
            else merged[key] = new VillageContentRow { Type = type, Level = level, Workset = workset, Count = row.Count };
            result.Total += row.Count;
        }

        var ordered = merged.Values
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.Type, StringComparer.Ordinal)
            .ThenBy(r => r.Level, StringComparer.Ordinal)
            .ThenBy(r => r.Workset, StringComparer.Ordinal)
            .ToList();

        var keep = Math.Max(0, cap);
        result.Rows = ordered.Take(keep).ToList();
        result.Omitted = ordered.Skip(keep).Sum(r => r.Count);
        return result;
    }

    /// <summary>Same as <see cref="Build"/> with the column labels and noun a landmark needs.</summary>
    public static VillageWarehouseContents Build(IEnumerable<VillageContentRow>? rows, string noun, string typeLabel, string levelLabel = "Level", string worksetLabel = "Workset", int cap = MaxRows)
    {
        var result = Build(rows, cap);
        result.Noun = noun;
        result.Labels = new VillageContentLabels { Type = typeLabel, Level = levelLabel, Workset = worksetLabel };
        return result;
    }

    private static string Clean(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length > MaxNameLength ? text.Substring(0, MaxNameLength) : text;
    }
}

/// <summary>
/// Pure grouping rules the graph reader applies to node names, so the buildings show a handful of
/// meaningful groups instead of one row per sheet or per type.
/// </summary>
public static class VillageContentGrouping
{
    /// <summary>
    /// Discipline prefix of a sheet number: the letters before the first digit, dash or space
    /// ("E-101" → "E", "ATS201" → "ATS"). Sheets without letters group under "(numeric)".
    /// </summary>
    public static string SheetPrefix(string? sheetNumberOrName)
    {
        var text = (sheetNumberOrName ?? string.Empty).Trim();
        var end = 0;
        while (end < text.Length && char.IsLetter(text[end])) end++;
        return end == 0 ? (text.Length == 0 ? string.Empty : "(numeric)") : text.Substring(0, end).ToUpperInvariant();
    }

    /// <summary>Family part of a "Family: Type" graph name, or the whole name when there is no colon.</summary>
    public static string FamilyOf(string? typeName)
    {
        var text = (typeName ?? string.Empty).Trim();
        var colon = text.IndexOf(':');
        return colon > 0 ? text.Substring(0, colon).Trim() : text;
    }

    /// <summary>First word of a name (up to a space, dash, underscore or dot), for grouping schedules.</summary>
    public static string FirstWord(string? name)
    {
        var text = (name ?? string.Empty).Trim();
        var end = text.IndexOfAny(new[] { ' ', '-', '_', '.' });
        return end > 0 ? text.Substring(0, end) : text;
    }

    /// <summary>Bucket for a panel's circuit count, so panels group by how loaded they are.</summary>
    public static string PanelLoad(long circuits) => circuits switch
    {
        <= 0 => "No circuits",
        <= 10 => "1–10 circuits",
        <= 30 => "11–30 circuits",
        <= 60 => "31–60 circuits",
        _ => "Over 60 circuits"
    };
}
