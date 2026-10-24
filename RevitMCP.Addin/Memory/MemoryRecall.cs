using System.Globalization;
using System.Text;

namespace RevitMCP.Addin.Memory;

/// <summary>The open model a recall runs against.</summary>
public sealed class MemoryRecallContext
{
    public string? Project { get; set; }
    public string? Model { get; set; }
    public string? ModelVersion { get; set; }
    public string User { get; set; } = string.Empty;
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MemoryRecallOptions
{
    public string Query { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public int Limit { get; set; } = 5;
    public bool IncludeRetired { get; set; }

    /// <summary>Hints leave dormant entries out; a deliberate search finds them.</summary>
    public bool IncludeDormant { get; set; } = true;

    /// <summary>Hints: only confirmed entries, and candidates written by this user.</summary>
    public string? VisibleTo { get; set; }
}

public sealed class MemoryHit
{
    public MemoryEntry Entry { get; set; } = new();
    public double Score { get; set; }

    /// <summary>Written in this model against an older version of it.</summary>
    public bool Stale { get; set; }
    public string? StaleReason { get; set; }
}

/// <summary>
/// Matching and ranking (docs/shared-memory.md, "Recall"): text match on key, tags, value and reason ×
/// tier weight (project &gt; company &gt; personal) × recency × usefulness × same-user bonus × state, plus
/// staleness against the open model's version. Deterministic and pure — no Revit API.
/// </summary>
public static class MemoryRecall
{
    public const string Note = "Memory entries are hints written by people and agents at a point in time; verify live before writing.";

    public static List<MemoryHit> Rank(IEnumerable<MemoryEntry> entries, MemoryRecallOptions options, MemoryRecallContext context)
    {
        var terms = Terms(options.Query);
        var tags = MemoryPolicy.NormalizeTags(options.Tags);
        var hits = new List<MemoryHit>();
        foreach (var e in entries)
        {
            if (e.IsRetired && !options.IncludeRetired) continue;
            if (e.State == MemoryStates.Dormant && !options.IncludeDormant) continue;
            if (options.VisibleTo != null && !MemoryLifecycle.VisibleInHints(e, options.VisibleTo)) continue;
            if (!InProject(e, context.Project)) continue;
            if (tags.Count > 0 && !tags.All(t => e.Tags.Contains(t))) continue;

            var match = terms.Count == 0 ? 1.0 : TextMatch(e, terms);
            if (match <= 0) continue;

            var score = match
                        * TierWeight(e.Scope)
                        * MemoryPolicy.Recency(e.Updated, context.Now)
                        * (0.5 + MemoryPolicy.Usefulness(e.Counters))
                        * (string.Equals(e.Author, context.User, StringComparison.OrdinalIgnoreCase) ? 1.2 : 1.0)
                        * StateWeight(e);
            var (stale, reason) = Staleness(e, context);
            hits.Add(new MemoryHit { Entry = e, Score = Math.Round(score, 4), Stale = stale, StaleReason = reason });
        }

        return hits
            .OrderByDescending(h => h.Score)
            .ThenByDescending(h => h.Entry.Updated)
            .Take(Math.Max(1, options.Limit))
            .ToList();
    }

    /// <summary>Personal entries without a project apply everywhere; anything else only in its own project.</summary>
    public static bool InProject(MemoryEntry e, string? project)
    {
        if (string.IsNullOrWhiteSpace(e.Project)) return true;
        return !string.IsNullOrWhiteSpace(project) &&
               string.Equals(MemoryPolicy.Slug(e.Project!), MemoryPolicy.Slug(project!), StringComparison.Ordinal);
    }

    public static (bool Stale, string? Reason) Staleness(MemoryEntry e, MemoryRecallContext context)
    {
        if (string.IsNullOrEmpty(e.ModelVersion) || string.IsNullOrEmpty(context.ModelVersion)) return (false, null);
        if (!string.Equals(e.Model, context.Model, StringComparison.OrdinalIgnoreCase)) return (false, null);
        return e.ModelVersion == context.ModelVersion
            ? (false, null)
            : (true, "written against an earlier model");
    }

    public static double TierWeight(string scope) => scope switch
    {
        MemoryScopes.Project => 1.0,
        MemoryScopes.Company => 0.9,
        _ => 0.8
    };

    private static double StateWeight(MemoryEntry e)
    {
        var weight = e.State switch
        {
            MemoryStates.Confirmed => 1.0,
            MemoryStates.Candidate => 0.7,
            MemoryStates.Dormant => 0.4,
            _ => 0.2
        };
        return e.Flags.Contains(MemoryPolicy.FlagFlipFlop) ? weight * 0.5 : weight;
    }

    /// <summary>
    /// The share of query terms found (key and tags count three times, value twice, reason once). Zero when none
    /// is found, so an unrelated entry never shows up just for being recent.
    /// </summary>
    private static double TextMatch(MemoryEntry e, List<string> terms)
    {
        var key = Fold(e.Key.Replace('/', ' ').Replace('-', ' '));
        var tags = Fold(string.Join(" ", e.Tags).Replace('-', ' '));
        var value = Fold(e.Value);
        var reason = Fold(e.Reason);
        double sum = 0;
        var found = 0;
        foreach (var t in terms)
        {
            double w = 0;
            if (key.Contains(t)) w += 3;
            if (tags.Contains(t)) w += 3;
            if (value.Contains(t)) w += 2;
            if (reason.Contains(t)) w += 1;
            if (w > 0) found++;
            sum += w;
        }
        if (found == 0) return 0;
        return sum / (terms.Count * 9.0) + (double)found / terms.Count;
    }

    private static List<string> Terms(string query)
    {
        var terms = new List<string>();
        foreach (var raw in Fold(query).Split(new[] { ' ', ',', ';', '/', '-', '_', '.', ':', '"', '\'', '(', ')' }, StringSplitOptions.RemoveEmptyEntries))
            if (raw.Length >= 2 && !terms.Contains(raw)) terms.Add(raw);
        return terms;
    }

    /// <summary>Lowercase, diacritics dropped, so "silt" finds "Silt" and "õ" matches "o".</summary>
    public static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
