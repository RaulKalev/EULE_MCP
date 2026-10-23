using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RevitMCP.Addin.Memory;

/// <summary>What a write does with the entries already stored under the same key.</summary>
public enum MemoryWriteKind
{
    /// <summary>No active entry has the key: a new entry.</summary>
    New,

    /// <summary>The active entry already says the same: it is reaffirmed (counter + author), no new file.</summary>
    Reaffirm,

    /// <summary>The active entry says something else: a new entry replaces it, the old one is retired as history.</summary>
    Supersede
}

public sealed class MemoryWriteDecision
{
    public MemoryWriteKind Kind { get; set; }

    /// <summary>The active entry that is reaffirmed or superseded.</summary>
    public MemoryEntry? Existing { get; set; }

    /// <summary>The value went back to one an earlier entry held, with more than one author involved.</summary>
    public bool FlipFlop { get; set; }
}

/// <summary>
/// The write-time rules of shared memory (docs/shared-memory.md, "Entry format"): key normalisation,
/// length caps, "decisions, not facts", the privacy allow-list, merge by key, supersede, the flip-flop flag
/// and the per-project cap. Deterministic and pure — no Revit API, no model calls.
/// </summary>
public static class MemoryPolicy
{
    public const int DefaultValueMaxLength = 400;
    public const int DefaultMaxEntriesPerProject = 500;
    public const int KeyMaxLength = 120;
    public const int MaxTags = 8;
    public const int TagMaxLength = 32;
    public const string FlagFlipFlop = "flip-flop";

    private static readonly Regex Email = new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);
    private static readonly Regex Credential = new(@"\b(password|passwd|pwd|secret|token|api[ _-]?key|parool|salasõna)\b\s*[:=]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PrivateKey = new(@"-----BEGIN [A-Z ]*KEY-----", RegexOptions.Compiled);
    // Base64/hex runs; underscores and hyphens are left out so long family and type names pass.
    private static readonly Regex LongSecret = new(@"[A-Za-z0-9+/=]{40,}", RegexOptions.Compiled);
    private static readonly Regex Phone = new(@"\+\d[\d ()-]{7,}\d", RegexOptions.Compiled);
    private static readonly Regex LongNumber = new(@"\b\d{4,}\b", RegexOptions.Compiled);

    /// <summary>
    /// A key segment path: lowercase ASCII letters, digits, '.', '_' and '-', segments joined by '/'.
    /// Diacritics are dropped and spaces become hyphens. A leading <c>project/&lt;id&gt;/</c>,
    /// <c>personal/</c> or <c>company/</c> is removed, because <see cref="FullKey"/> adds the tier itself.
    /// Returns null when nothing usable is left.
    /// </summary>
    public static string? NormalizeTopic(string? key, string? project = null)
    {
        var segments = new List<string>();
        foreach (var raw in (key ?? string.Empty).Replace('\\', '/').Split('/'))
        {
            var seg = Slug(raw);
            if (seg.Length > 0) segments.Add(seg);
        }

        if (segments.Count > 0 && (segments[0] == MemoryScopes.Project || segments[0] == MemoryScopes.Personal || segments[0] == MemoryScopes.Company))
        {
            var tier = segments[0];
            segments.RemoveAt(0);
            var projectSeg = Slug(project ?? string.Empty);
            if (tier != MemoryScopes.Company && segments.Count > 1 && projectSeg.Length > 0 && segments[0] == projectSeg)
                segments.RemoveAt(0);
        }
        if (segments.Count == 0) return null;
        var topic = string.Join("/", segments);
        return topic.Length > KeyMaxLength ? topic.Substring(0, KeyMaxLength).TrimEnd('/', '-') : topic;
    }

    /// <summary><c>project/&lt;project&gt;/&lt;topic&gt;</c>, <c>personal/[&lt;project&gt;/]&lt;topic&gt;</c> or <c>company/&lt;topic&gt;</c>.</summary>
    public static string FullKey(string scope, string? project, string topic)
    {
        var projectSeg = Slug(project ?? string.Empty);
        if (projectSeg.Length == 0) projectSeg = "_unfiled";
        return scope switch
        {
            MemoryScopes.Project => $"project/{projectSeg}/{topic}",
            MemoryScopes.Company => $"company/{topic}",
            _ => string.IsNullOrWhiteSpace(project) ? $"personal/{topic}" : $"personal/{projectSeg}/{topic}"
        };
    }

    public static List<string> NormalizeTags(IEnumerable<string>? tags)
    {
        var list = new List<string>();
        foreach (var t in tags ?? Array.Empty<string>())
        {
            var slug = Slug(t);
            if (slug.Length > TagMaxLength) slug = slug.Substring(0, TagMaxLength).TrimEnd('-');
            if (slug.Length > 0 && !list.Contains(slug)) list.Add(slug);
            if (list.Count == MaxTags) break;
        }
        return list;
    }

    /// <summary>
    /// The reasons a note is refused, empty when it may be stored. <paramref name="value"/> and
    /// <paramref name="reason"/> are expected as stored (<see cref="MemoryEntryParser.OneLine"/>).
    /// </summary>
    public static List<string> Validate(string value, string reason, int maxLength = DefaultValueMaxLength)
    {
        var problems = new List<string>();
        if (value.Length == 0) problems.Add("value is required: the decision, preference, exception or correction itself.");
        if (reason.Length == 0) problems.Add("reason is required: memory keeps why a choice was made, not what the model contains.");
        if (value.Length > maxLength) problems.Add($"value is {value.Length} characters; the limit is {maxLength}. Keep one decision per entry.");
        if (reason.Length > maxLength) problems.Add($"reason is {reason.Length} characters; the limit is {maxLength}. An entry is a fact about the project, not a session diary.");
        if (problems.Count > 0) return problems;

        if (LooksLikeModelData(value))
            problems.Add("value looks like data readable from the model (ids, numbers, counts). Read it live instead; remember why a choice was made.");

        foreach (var text in new[] { value, reason })
        {
            if (Email.IsMatch(text) || Phone.IsMatch(text))
            {
                problems.Add("Personal contact data (e-mail addresses, phone numbers) is not stored in memory.");
                break;
            }
            if (Credential.IsMatch(text) || PrivateKey.IsMatch(text) || LongSecret.IsMatch(text))
            {
                problems.Add("Credentials, keys and long opaque strings are not stored in memory.");
                break;
            }
        }
        return problems;
    }

    /// <summary>
    /// "Decisions, not facts": text with almost no words, or a list of several long numbers (element ids,
    /// parameter values), is model data. A single id cited inside a sentence is allowed.
    /// </summary>
    public static bool LooksLikeModelData(string value)
    {
        var letters = 0;
        foreach (var c in value) if (char.IsLetter(c)) letters++;
        if (letters < 3) return true;
        if (LongNumber.Matches(value).Count >= 4) return true;
        var words = value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Count(w => w.Any(char.IsLetter));
        return words < 2;
    }

    /// <summary>Two values say the same when they match ignoring case and whitespace.</summary>
    public static bool SameValue(string a, string b) =>
        string.Equals(MemoryEntryParser.OneLine(a), MemoryEntryParser.OneLine(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Decides a write against every stored entry with the same key (active and retired history).
    /// A value that returns to one a retired entry held, with two or more authors on the key, is a flip-flop.
    /// </summary>
    public static MemoryWriteDecision Decide(IReadOnlyCollection<MemoryEntry> sameKey, string value, string author)
    {
        var active = sameKey.Where(e => !e.IsRetired).OrderByDescending(e => e.Updated).FirstOrDefault();
        if (active != null && SameValue(active.Value, value))
            return new MemoryWriteDecision { Kind = MemoryWriteKind.Reaffirm, Existing = active };

        var authors = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { author };
        foreach (var e in sameKey)
        {
            if (!string.IsNullOrEmpty(e.Author)) authors.Add(e.Author);
            foreach (var a in e.Authors) authors.Add(a);
        }
        var returning = sameKey.Any(e => e.IsRetired && SameValue(e.Value, value));
        return new MemoryWriteDecision
        {
            Kind = active == null ? MemoryWriteKind.New : MemoryWriteKind.Supersede,
            Existing = active,
            FlipFlop = active != null && returning && authors.Count >= 2
        };
    }

    /// <summary>(used + worked + 1) / (shown + 2): 0.5 for an unseen entry, towards 1 when hints are acted on.</summary>
    public static double Usefulness(MemoryCounters c) => (c.Used + c.Worked + 1.0) / (c.Shown + 2.0);

    /// <summary>
    /// The per-project cap: when <paramref name="active"/> already holds <paramref name="max"/> entries, the
    /// lowest-scoring dormant entry is the one to retire. Null with <paramref name="full"/> = true means the cap
    /// is reached and nothing can make room.
    /// </summary>
    public static MemoryEntry? EntryToRetireForCap(IReadOnlyCollection<MemoryEntry> active, int max, DateTimeOffset now, out bool full)
    {
        full = false;
        if (max <= 0 || active.Count < max) return null;
        var victim = active
            .Where(e => e.State == MemoryStates.Dormant)
            .OrderBy(e => Usefulness(e.Counters) * Recency(e.Updated, now))
            .ThenBy(e => e.Updated)
            .FirstOrDefault();
        full = victim == null;
        return victim;
    }

    /// <summary>1 for today, 0.5 after 90 days, approaching 0 for very old entries.</summary>
    public static double Recency(DateTimeOffset updated, DateTimeOffset now)
    {
        var days = Math.Max(0, (now - updated).TotalDays);
        return 1.0 / (1.0 + days / 90.0);
    }

    /// <summary>Lowercase ASCII, diacritics dropped, anything else becomes a single hyphen.</summary>
    public static string Slug(string text)
    {
        var sb = new StringBuilder();
        foreach (var raw in text.Trim().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(raw) == UnicodeCategory.NonSpacingMark) continue;
            var c = char.ToLowerInvariant(raw);
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '_') sb.Append(c);
            else if (sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-', '.');
    }
}
