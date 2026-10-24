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
        // A query made only of filler words matches nothing (an empty query still lists by tags).
        if (terms.Count == 0 && !string.IsNullOrWhiteSpace(options.Query)) return new List<MemoryHit>();
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

    /// <summary>Share of the query terms that must be found for an entry to match at all.</summary>
    public const double MinTermShare = 1.0 / 3;

    /// <summary>Words too common in requests to say what they are about (English and Estonian), folded.</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "from", "into", "onto", "that", "this", "these", "those", "which", "what", "when",
        "where", "how", "they", "them", "their", "are", "was", "were", "any", "each", "per", "our", "your", "its", "has", "have", "not", "but",
        "can", "should", "must", "will", "new", "all", "only", "also", "then", "there", "here", "about", "use", "using",
        "ning", "voi", "kui", "see", "need", "mis", "kus", "koik", "oma", "uus", "uued", "uute", "juures", "peal",
        "jaoks", "iga", "igal", "nagu", "siis", "aga", "mida", "kas", "ole", "olla"
    };

    /// <summary>
    /// Words every Revit request shares (tag, type, family, parameter …). They count toward the share, but an entry
    /// that matches only these does not match a query that also names what it is about ("detector tag type" must not
    /// return the camera tag note). Prefix-matched like any term, so "tags" and "tüübid" are generic too.
    /// </summary>
    private static readonly string[] GenericWords =
    {
        "tag", "type", "family", "instance", "element", "parameter", "value", "category", "name", "revit", "model",
        "project", "convention", "rule", "default", "device", "place", "set", "check", "find", "list", "get",
        "silt", "sildi", "tuup", "perekond", "parameeter", "vaartus", "kategooria", "nimi", "mudel", "projekt", "seade",
        "paiguta"
    };

    /// <summary>
    /// Text match of the query terms against the entry's words (key and tags count three times, value twice, reason
    /// once). Zero unless at least <see cref="MinTermShare"/> of the terms are found and, when the query has terms
    /// beyond <see cref="GenericWords"/>, one of those is found — so an unrelated entry never shows up just for being
    /// recent or for sharing "tag" with the query.
    /// </summary>
    private static double TextMatch(MemoryEntry e, List<string> terms)
    {
        var key = Words(e.Key);
        var tags = Words(string.Join(" ", e.Tags));
        var value = Words(e.Value);
        var reason = Words(e.Reason);
        double sum = 0;
        var found = 0;
        var specific = 0;
        var specificFound = 0;
        foreach (var t in terms)
        {
            double w = 0;
            if (AnyMatch(key, t)) w += 3;
            if (AnyMatch(tags, t)) w += 3;
            if (AnyMatch(value, t)) w += 2;
            if (AnyMatch(reason, t)) w += 1;
            var generic = IsGeneric(t);
            if (!generic) specific++;
            if (w > 0)
            {
                found++;
                if (!generic) specificFound++;
            }
            sum += w;
        }
        if (found == 0 || found < terms.Count * MinTermShare) return 0;
        if (specific > 0 && specificFound == 0) return 0;
        return sum / (terms.Count * 9.0) + (double)found / terms.Count;
    }

    /// <summary>The query's search terms: folded words of 3+ characters that are not <see cref="StopWords"/>.</summary>
    public static List<string> Terms(string query)
    {
        var terms = new List<string>();
        foreach (var word in Words(query))
            if (word.Length >= 3 && !StopWords.Contains(word) && !terms.Contains(word)) terms.Add(word);
        return terms;
    }

    /// <summary>
    /// True when <paramref name="term"/> and <paramref name="word"/> are the same word in another form: equal, one
    /// starts with the other and adds at most four letters (kaamera/kaamerate, korgus/korgusel; one for three-letter
    /// words, tag/tags), or both
    /// are 5+ letters and differ only in the last one (kaamerad/kaamerat). Whole words only, so "on" no longer
    /// matches inside "convention".
    /// </summary>
    public static bool WordMatches(string term, string word)
    {
        if (term == word) return true;
        var shorter = Math.Min(term.Length, word.Length);
        var prefix = 0;
        while (prefix < shorter && term[prefix] == word[prefix]) prefix++;
        // Three-letter words only take a one-letter ending (tag/tags), not any longer word (car/cargo).
        if (prefix == shorter) return shorter >= 3 && Math.Abs(term.Length - word.Length) <= (shorter == 3 ? 1 : 4);
        return shorter >= 5 && prefix >= shorter - 1;
    }

    private static bool AnyMatch(List<string> words, string term)
    {
        foreach (var w in words)
            if (WordMatches(term, w)) return true;
        return false;
    }

    private static bool IsGeneric(string term)
    {
        foreach (var g in GenericWords)
            if (WordMatches(term, g)) return true;
        return false;
    }

    /// <summary>Folded words: runs of letters and digits, so keys, snake_case and "Tag:Type" split too.</summary>
    private static List<string> Words(string? text)
    {
        var words = new List<string>();
        if (string.IsNullOrEmpty(text)) return words;
        var sb = new StringBuilder();
        foreach (var c in Fold(text!))
        {
            if (char.IsLetterOrDigit(c)) { sb.Append(c); continue; }
            if (sb.Length > 0) { words.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length > 0) words.Add(sb.ToString());
        return words;
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
