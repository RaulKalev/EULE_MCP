using System.Text.RegularExpressions;

namespace RevitMCP.Bridge;

/// <summary>Compact, searchable metadata of one connector tool.</summary>
public sealed class ToolEntry
{
    public string Name { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool ReadOnly { get; set; }

    /// <summary>First sentence of the description, at most <paramref name="max"/> characters.</summary>
    public string Summary(int max = 160)
    {
        var d = Description.Trim();
        var dot = d.IndexOf(". ", StringComparison.Ordinal);
        if (dot > 0) d = d.Substring(0, dot + 1);
        return d.Length <= max ? d : d.Substring(0, max - 1).TrimEnd() + "…";
    }
}

/// <summary>
/// Natural-language search over the tool catalog (#65). Scores query words against tool names,
/// groups and descriptions, with a small English/Estonian synonym table for the domain.
/// Pure — unit tested in RevitMCP.Tests.
/// </summary>
public static class ToolSearchIndex
{
    private static readonly Dictionary<string, string[]> Synonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ahel"] = ["circuit"], ["ahela"] = ["circuit"], ["ahelad"] = ["circuit"], ["kilp"] = ["panel"], ["kilbi"] = ["panel"],
        ["ruum"] = ["room"], ["ruumi"] = ["room"], ["ruumid"] = ["room"], ["korrus"] = ["level"], ["korruse"] = ["level"],
        ["leht"] = ["sheet"], ["lehed"] = ["sheet"], ["vaade"] = ["view"], ["vaated"] = ["view"], ["silt"] = ["tag"], ["sildid"] = ["tag"],
        ["mõõt"] = ["dimension"], ["kaabel"] = ["cable"], ["juhe"] = ["wire"], ["seade"] = ["device"], ["seadmed"] = ["device"],
        ["tulekahju"] = ["fire", "alarm"], ["ats"] = ["fire", "alarm"], ["andur"] = ["detector", "device"], ["silmus"] = ["loop", "circuit"],
        ["parameeter"] = ["parameter"], ["parameetrid"] = ["parameter"], ["link"] = ["linked"], ["lingitud"] = ["linked"],
        ["kokkupõrge"] = ["clash"], ["koopia"] = ["duplicate"], ["kustuta"] = ["delete"], ["nimeta"] = ["rename"],
        ["loop"] = ["circuit"], ["circuits"] = ["circuit"], ["panels"] = ["panel"], ["rooms"] = ["room"], ["sheets"] = ["sheet"],
        ["views"] = ["view"], ["tags"] = ["tag"], ["devices"] = ["device"], ["elements"] = ["element"], ["parameters"] = ["parameter"],
        ["levels"] = ["level"], ["wires"] = ["wire"], ["cables"] = ["cable"], ["clashes"] = ["clash"], ["spaces"] = ["space", "room"],
        ["space"] = ["room"], ["detector"] = ["device", "fire"], ["sounder"] = ["device", "fire"], ["excel"] = ["excel", "export"]
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "of", "to", "in", "on", "for", "and", "or", "with", "all", "my", "me", "i", "want", "need", "how",
        "do", "can", "is", "are", "revit", "tool", "tools", "ja", "või", "kõik", "mis", "kuidas"
    };

    public static List<string> QueryTerms(string? query)
    {
        var terms = new List<string>();
        foreach (var raw in Regex.Split((query ?? string.Empty).ToLowerInvariant(), @"[^\p{L}\p{N}]+"))
        {
            if (raw.Length < 2 || StopWords.Contains(raw)) continue;
            terms.Add(raw);
            if (Synonyms.TryGetValue(raw, out var syn)) terms.AddRange(syn);
            else if (raw.Length > 4 && raw.EndsWith("s")) terms.Add(raw.Substring(0, raw.Length - 1));
        }
        return terms.Distinct().ToList();
    }

    public static double Score(ToolEntry tool, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0) return 0;
        var nameTokens = tool.Name.ToLowerInvariant().Split('_');
        var name = tool.Name.ToLowerInvariant();
        var description = tool.Description.ToLowerInvariant();
        double score = 0;
        var hits = 0;
        foreach (var t in terms)
        {
            var s = 0.0;
            if (nameTokens.Contains(t)) s += 3;
            else if (nameTokens.Any(n => SameStem(n, t))) s += 2.5;   // clash ~ clashes, detect ~ detection
            else if (name.Contains(t)) s += 2;
            if (string.Equals(tool.Group, t, StringComparison.OrdinalIgnoreCase)) s += 2;
            if (Regex.IsMatch(description, @"\b" + Regex.Escape(t))) s += 1;
            if (s > 0) hits++;
            score += s;
        }
        // Reward covering more of the query; mildly prefer preview/read-only tools for discovery.
        score *= 1 + (double)hits / terms.Count;
        if (tool.ReadOnly) score += 0.1;
        return score;
    }

    /// <summary>Word forms of one stem: one word starts with the other and they share at least 4 letters.</summary>
    private static bool SameStem(string a, string b)
    {
        if (Math.Min(a.Length, b.Length) < 4) return false;
        return a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal) ||
               (a.Length >= 5 && b.Length >= 5 && string.CompareOrdinal(a, 0, b, 0, 5) == 0);
    }

    /// <summary>Top matches for a query, optionally limited to one group. Empty query returns nothing.</summary>
    public static List<ToolEntry> Search(IEnumerable<ToolEntry> tools, string? query, string? group = null, int limit = 10)
    {
        var terms = QueryTerms(query);
        return tools
            .Where(t => string.IsNullOrWhiteSpace(group) || string.Equals(t.Group, group, StringComparison.OrdinalIgnoreCase))
            .Select(t => (Tool: t, Score: Score(t, terms)))
            .Where(x => x.Score > 0.1 || (terms.Count == 0 && !string.IsNullOrWhiteSpace(group)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Tool.Name, StringComparer.Ordinal)
            .Take(Math.Max(1, Math.Min(50, limit)))
            .Select(x => x.Tool)
            .ToList();
    }
}
