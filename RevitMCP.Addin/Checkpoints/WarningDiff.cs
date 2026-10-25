namespace RevitMCP.Addin.Checkpoints;

public sealed class WarningDiffResult
{
    public List<WarningKey> New { get; set; } = new();
    public List<WarningKey> Resolved { get; set; } = new();
    public int Unchanged { get; set; }
}

public sealed class WarningGroup
{
    public string Description { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public int Count { get; set; }

    /// <summary>Distinct element ids over the group, capped at <see cref="WarningDiff.MaxGroupIds"/>.</summary>
    public List<long> ElementIds { get; set; } = new();
    public int ElementIdCount { get; set; }

    /// <summary>The element ids of the first few warnings, one array per warning.</summary>
    public List<long[]> Sample { get; set; } = new();
}

/// <summary>
/// Set difference and grouping of Revit warnings (#98). A warning's identity is its failure definition plus
/// the sorted element ids, so the same definition on different elements counts as different warnings.
/// Pure — no Revit API.
/// </summary>
public static class WarningDiff
{
    public const int MaxGroupIds = 200;
    public const int SampleSize = 5;

    public static WarningDiffResult Compare(IEnumerable<WarningKey> before, IEnumerable<WarningKey> now)
    {
        var beforeByKey = Distinct(before);
        var nowByKey = Distinct(now);
        var result = new WarningDiffResult();
        foreach (var pair in nowByKey)
        {
            if (beforeByKey.ContainsKey(pair.Key)) result.Unchanged++;
            else result.New.Add(pair.Value);
        }
        foreach (var pair in beforeByKey)
        {
            if (!nowByKey.ContainsKey(pair.Key)) result.Resolved.Add(pair.Value);
        }
        return result;
    }

    /// <summary>Warnings in <paramref name="now"/> that are not in <paramref name="before"/>.</summary>
    public static List<WarningKey> Since(IEnumerable<WarningKey> before, IEnumerable<WarningKey> now)
    {
        var known = new HashSet<string>(before.Select(w => w.Key), StringComparer.Ordinal);
        return now.Where(w => !known.Contains(w.Key)).ToList();
    }

    /// <summary>Groups by description and severity, largest group first.</summary>
    public static List<WarningGroup> Group(IEnumerable<WarningKey> warnings)
    {
        var groups = new List<WarningGroup>();
        foreach (var g in warnings.GroupBy(w => (w.Description ?? string.Empty, w.Severity ?? string.Empty)))
        {
            var ids = g.SelectMany(w => w.ElementIds).Distinct().ToList();
            groups.Add(new WarningGroup
            {
                Description = g.Key.Item1,
                Severity = g.Key.Item2,
                Count = g.Count(),
                ElementIds = ids.Take(MaxGroupIds).ToList(),
                ElementIdCount = ids.Count,
                Sample = g.Take(SampleSize).Select(w => w.ElementIds).ToList()
            });
        }
        return groups
            .OrderBy(g => SeverityRank(g.Severity))
            .ThenByDescending(g => g.Count)
            .ThenBy(g => g.Description, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Filters by severity: "all" (or empty), "warning", "error" (Error and DocumentCorruption).</summary>
    public static bool MatchesSeverity(WarningKey warning, string? severity)
    {
        if (string.IsNullOrWhiteSpace(severity) || string.Equals(severity, "all", StringComparison.OrdinalIgnoreCase)) return true;
        var isWarning = string.Equals(warning.Severity, "Warning", StringComparison.OrdinalIgnoreCase);
        return string.Equals(severity, "warning", StringComparison.OrdinalIgnoreCase) ? isWarning : !isWarning;
    }

    public static bool IsErrorSeverity(WarningKey warning) => !MatchesSeverity(warning, "warning");

    /// <summary>Errors first, then warnings, then anything else.</summary>
    public static int SeverityRank(string? severity) => severity switch
    {
        "DocumentCorruption" => 0,
        "Error" => 1,
        "Warning" => 2,
        _ => 3
    };

    private static Dictionary<string, WarningKey> Distinct(IEnumerable<WarningKey> warnings)
    {
        var map = new Dictionary<string, WarningKey>(StringComparer.Ordinal);
        foreach (var w in warnings)
        {
            var key = w.Key;
            if (!map.ContainsKey(key)) map[key] = w;
        }
        return map;
    }
}
