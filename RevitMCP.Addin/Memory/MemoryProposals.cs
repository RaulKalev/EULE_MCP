using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace RevitMCP.Addin.Memory;

/// <summary>What a consolidation proposal asks the reviewer to do.</summary>
public static class MemoryProposalKinds
{
    /// <summary>Several entries say the same: replace them with one entry.</summary>
    public const string Merge = "merge";

    /// <summary>One entry rewritten: a raw observation in readable words, or a flip-flop with its explanation.</summary>
    public const string Rewrite = "rewrite";

    /// <summary>Entries no longer useful.</summary>
    public const string Retire = "retire";

    /// <summary>Stable entries that should leave memory for a company default, a skill setting or a standard.</summary>
    public const string Promote = "promote";

    public static readonly string[] All = { Merge, Rewrite, Retire, Promote };
}

public static class MemoryProposalStatus
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
}

/// <summary>Where a promotion goes: company memory, a skill setting or a standards document.</summary>
public static class MemoryPromotionTargets
{
    public const string Company = "company";
    public const string Skill = "skill";
    public const string Standard = "standard";

    public static bool IsKnown(string? target) => target is Company or Skill or Standard;
}

/// <summary>
/// A change the consolidation pass suggests (docs/shared-memory.md, "Who does what"). It never changes an entry by
/// itself: it waits in <c>.proposals\</c> of the tier folder until a person accepts or rejects it on the review page.
/// </summary>
public sealed class MemoryProposal
{
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;
    [JsonProperty("kind")] public string Kind { get; set; } = string.Empty;
    [JsonProperty("entryIds")] public List<string> EntryIds { get; set; } = new();

    /// <summary>Merge and rewrite: the topic path of the resulting entry (empty = the first entry's key).</summary>
    [JsonProperty("key", NullValueHandling = NullValueHandling.Ignore)] public string? Key { get; set; }
    [JsonProperty("value", NullValueHandling = NullValueHandling.Ignore)] public string? Value { get; set; }
    [JsonProperty("reason", NullValueHandling = NullValueHandling.Ignore)] public string? Reason { get; set; }

    /// <summary>Promote: company, skill or standard.</summary>
    [JsonProperty("target", NullValueHandling = NullValueHandling.Ignore)] public string? Target { get; set; }

    /// <summary>Why the pass suggests it — shown to the reviewer.</summary>
    [JsonProperty("note")] public string Note { get; set; } = string.Empty;
    [JsonProperty("status")] public string Status { get; set; } = MemoryProposalStatus.Pending;
    [JsonProperty("createdAt")] public DateTimeOffset CreatedAt { get; set; }
    [JsonProperty("createdBy")] public string CreatedBy { get; set; } = string.Empty;
    [JsonProperty("client", NullValueHandling = NullValueHandling.Ignore)] public string? Client { get; set; }
    [JsonProperty("decidedAt", NullValueHandling = NullValueHandling.Ignore)] public DateTimeOffset? DecidedAt { get; set; }
    [JsonProperty("decidedBy", NullValueHandling = NullValueHandling.Ignore)] public string? DecidedBy { get; set; }

    /// <summary>The entry or proposal file accepting it produced.</summary>
    [JsonProperty("result", NullValueHandling = NullValueHandling.Ignore)] public string? Result { get; set; }
}

/// <summary>
/// Proposals of one tier folder, one JSON file each under <c>.proposals\</c> — written atomically, so the pass and a
/// reviewer never conflict. Pure — no Revit API.
/// </summary>
public sealed class MemoryProposalStore
{
    public const string FolderName = ".proposals";

    public MemoryProposalStore(string tierFolder) => Folder = Path.Combine(tierFolder, FolderName);

    public string Folder { get; }

    public static bool IsValidId(string? id) =>
        !string.IsNullOrEmpty(id) && id!.Length <= 64 && id.All(c => char.IsLetterOrDigit(c) || c == '-');

    public void Save(MemoryProposal proposal) =>
        MemoryStore.AtomicWrite(Folder, proposal.Id + ".json", JsonConvert.SerializeObject(proposal, Formatting.Indented));

    public MemoryProposal? Get(string id)
    {
        if (!IsValidId(id)) return null;
        return Read(Path.Combine(Folder, id + ".json"));
    }

    /// <summary>Every proposal, newest first; unreadable files are skipped.</summary>
    public List<MemoryProposal> List()
    {
        var list = new List<MemoryProposal>();
        try
        {
            if (!Directory.Exists(Folder)) return list;
            foreach (var path in Directory.GetFiles(Folder, "*.json"))
            {
                var p = Read(path);
                if (p != null) list.Add(p);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        list.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        return list;
    }

    private static MemoryProposal? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var p = JsonConvert.DeserializeObject<MemoryProposal>(File.ReadAllText(path, Encoding.UTF8));
            return p != null && IsValidId(p.Id) ? p : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>What makes a proposal acceptable: known kind, existing active entries, text that passes the write rules.</summary>
public static class MemoryProposalRules
{
    public const int NoteMaxLength = 400;

    /// <summary>Normalises <paramref name="p"/> in place and returns the reasons it is refused (empty = fine).</summary>
    public static List<string> Check(MemoryProposal p, IReadOnlyDictionary<string, MemoryEntry> entries, int maxLength = MemoryPolicy.DefaultValueMaxLength)
    {
        var problems = new List<string>();
        p.Kind = (p.Kind ?? string.Empty).Trim().ToLowerInvariant();
        p.EntryIds = (p.EntryIds ?? new List<string>()).Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        p.Note = MemoryEntryParser.OneLine(p.Note);
        p.Value = p.Value == null ? null : MemoryEntryParser.OneLine(p.Value);
        p.Reason = p.Reason == null ? null : MemoryEntryParser.OneLine(p.Reason);
        p.Target = p.Target?.Trim().ToLowerInvariant();

        if (!MemoryProposalKinds.All.Contains(p.Kind))
        {
            problems.Add($"kind must be one of {string.Join(", ", MemoryProposalKinds.All)}.");
            return problems;
        }
        if (p.Note.Length > NoteMaxLength) problems.Add($"note is longer than {NoteMaxLength} characters.");

        var min = p.Kind == MemoryProposalKinds.Merge ? 2 : 1;
        if (p.EntryIds.Count < min) problems.Add($"{p.Kind} needs at least {min} entry id{(min == 1 ? "" : "s")}.");
        if (p.Kind == MemoryProposalKinds.Rewrite && p.EntryIds.Count > 1) problems.Add("rewrite takes exactly one entry id; use merge for several.");
        foreach (var id in p.EntryIds)
        {
            if (!entries.TryGetValue(id, out var e)) problems.Add($"No entry with id {id}.");
            else if (e.IsRetired) problems.Add($"Entry {id} is already retired.");
            else if (e.Scope == MemoryScopes.Company) problems.Add($"Entry {id} is company memory, which an administrator changes.");
        }

        if (p.Kind is MemoryProposalKinds.Merge or MemoryProposalKinds.Rewrite)
        {
            problems.AddRange(MemoryPolicy.Validate(p.Value ?? string.Empty, p.Reason ?? string.Empty, maxLength));
            if (!string.IsNullOrWhiteSpace(p.Key) && MemoryPolicy.NormalizeTopic(p.Key) == null) problems.Add("key is not a usable topic path.");
        }
        if (p.Kind == MemoryProposalKinds.Promote)
        {
            if (string.IsNullOrEmpty(p.Target)) p.Target = MemoryPromotionTargets.Company;
            if (!MemoryPromotionTargets.IsKnown(p.Target)) problems.Add("target must be company, skill or standard.");
            // Without a value the entries' own text is promoted; a reworded value must pass the write rules.
            if (!string.IsNullOrEmpty(p.Value))
                problems.AddRange(MemoryPolicy.Validate(p.Value!, string.IsNullOrEmpty(p.Reason) ? "Promoted from project memory." : p.Reason!, maxLength));
        }
        return problems;
    }
}
