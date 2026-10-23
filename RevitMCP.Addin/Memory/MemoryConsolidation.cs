using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace RevitMCP.Addin.Memory;

/// <summary>One project's memory as the consolidation pass reads it.</summary>
public sealed class ProjectMemory
{
    public string Root { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;
    public string Project { get; set; } = string.Empty;

    /// <summary>Entries with tallies and effective lifecycle state.</summary>
    public List<MemoryEntry> Entries { get; set; } = new();
    public List<MemoryProposal> Proposals { get; set; } = new();
    public DateTimeOffset? LastRun { get; set; }
}

/// <summary>Entries that probably say the same thing.</summary>
public sealed class DuplicateGroup
{
    public string Project { get; set; } = string.Empty;
    public List<MemoryEntry> Entries { get; set; } = new();
    public double Similarity { get; set; }
}

/// <summary>The same topic and value in several projects: a candidate to leave memory for a company default.</summary>
public sealed class CrossProjectGroup
{
    public string Topic { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public List<string> Projects { get; set; } = new();
    public List<MemoryEntry> Entries { get; set; } = new();
}

public sealed class ConsolidationReport
{
    public DateTimeOffset? Since { get; set; }
    public DateTimeOffset ReadAt { get; set; }
    public List<MemoryEntry> Changed { get; set; } = new();
    public List<DuplicateGroup> Duplicates { get; set; } = new();
    public List<MemoryEntry> FlipFlops { get; set; } = new();
    public List<MemoryEntry> Observed { get; set; } = new();
    public List<MemoryEntry> Dormant { get; set; } = new();
    public List<CrossProjectGroup> CrossProject { get; set; } = new();
    public int PendingProposals { get; set; }
}

/// <summary>
/// The deterministic half of the weekly consolidation pass (docs/shared-memory.md, "Who does what"). It reads only
/// what changed since the last run and finds the work an AI pass is good at: prose entries that say the same thing,
/// flip-flops to explain, raw observations to put in readable words, entries stable across projects to promote, and
/// dormant entries to retire. The agent then writes <see cref="MemoryProposal"/>s — never entries — which a lead
/// accepts or rejects on the review page. Pure — no Revit API, no model calls.
/// </summary>
public static class MemoryConsolidation
{
    public const string LastRunFileName = ".consolidation.json";
    public const double DuplicateThreshold = 0.6;
    public const int MaxGroups = 50;
    public const int MaxEntries = 200;

    /// <summary>What the agent should do with a report. Returned with every read so any client can run the pass.</summary>
    public const string HowTo =
        "Write proposals with action=propose, never entries; a lead accepts or rejects them on the review page (revit_memory_page). " +
        "duplicates: one merge proposal per group that really says the same thing — entryIds, key (topic path), value and reason in one " +
        "clear sentence each. flipFlops: a rewrite proposal with the value the history supports and a reason that explains the disagreement, " +
        "or none if you cannot tell. observed: a rewrite proposal that turns the raw observation into a readable decision (keep the tool, " +
        "argument and value it names). crossProject: a promote proposal (target company, or skill/standard when it is a setting or a rule). " +
        "dormant: a retire proposal only when the entry is clearly obsolete. Skip anything you are unsure of. Then call action=done with readAt.";

    /// <summary>Builds the report over one or more projects. <paramref name="since"/> null = everything.</summary>
    public static ConsolidationReport Analyze(IReadOnlyList<ProjectMemory> projects, DateTimeOffset? since, DateTimeOffset now)
    {
        var report = new ConsolidationReport { Since = since, ReadAt = now };
        foreach (var p in projects)
        {
            var active = p.Entries.Where(e => !e.IsRetired).ToList();
            var changed = active.Where(e => since == null || e.Updated > since).ToList();
            report.Changed.AddRange(changed);
            report.FlipFlops.AddRange(active.Where(e => e.Flags.Contains(MemoryPolicy.FlagFlipFlop)));
            report.Observed.AddRange(changed.Where(e => e.Source == MemorySources.Observed));
            report.Dormant.AddRange(active.Where(e => e.State == MemoryStates.Dormant));
            report.PendingProposals += p.Proposals.Count(x => x.Status == MemoryProposalStatus.Pending);

            // A group is only worth a look when something in it changed since the last run.
            var changedIds = new HashSet<string>(changed.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var group in Duplicates(active))
                if (group.Entries.Any(e => changedIds.Contains(e.Id)))
                {
                    group.Project = p.Project;
                    report.Duplicates.Add(group);
                }
        }
        report.CrossProject = CrossProject(projects);

        report.Changed = report.Changed.OrderByDescending(e => e.Updated).Take(MaxEntries).ToList();
        report.Observed = report.Observed.Take(MaxEntries).ToList();
        report.Dormant = report.Dormant.Take(MaxEntries).ToList();
        report.Duplicates = report.Duplicates.OrderByDescending(g => g.Similarity).Take(MaxGroups).ToList();
        return report;
    }

    /// <summary>Groups of active entries whose values share most of their words (Jaccard ≥ 0.6), joined transitively.</summary>
    public static List<DuplicateGroup> Duplicates(IReadOnlyList<MemoryEntry> entries)
    {
        var words = entries.Select(e => Words(e.Value)).ToList();
        var parent = Enumerable.Range(0, entries.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        var best = new double[entries.Count];

        for (var i = 0; i < entries.Count; i++)
            for (var j = i + 1; j < entries.Count; j++)
            {
                if (!SameTier(entries[i], entries[j])) continue;
                var s = Jaccard(words[i], words[j]);
                if (s < DuplicateThreshold) continue;
                parent[Find(i)] = Find(j);
                best[i] = Math.Max(best[i], s);
                best[j] = Math.Max(best[j], s);
            }

        return Enumerable.Range(0, entries.Count)
            .GroupBy(Find)
            .Where(g => g.Count() > 1)
            .Select(g => new DuplicateGroup
            {
                Entries = g.Select(i => entries[i]).ToList(),
                Similarity = Math.Round(g.Max(i => best[i]), 2)
            })
            .ToList();
    }

    /// <summary>The same topic and value, active in two or more projects.</summary>
    public static List<CrossProjectGroup> CrossProject(IReadOnlyList<ProjectMemory> projects)
    {
        var groups = new Dictionary<string, CrossProjectGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in projects)
            foreach (var e in p.Entries.Where(e => !e.IsRetired && e.Scope == MemoryScopes.Project))
            {
                var topic = TopicOf(e);
                var id = topic + "|" + MemoryRecall.Fold(MemoryEntryParser.OneLine(e.Value));
                if (!groups.TryGetValue(id, out var g)) groups[id] = g = new CrossProjectGroup { Topic = topic, Value = e.Value };
                if (!g.Projects.Contains(p.Project)) g.Projects.Add(p.Project);
                g.Entries.Add(e);
            }
        return groups.Values.Where(g => g.Projects.Count >= 2).OrderByDescending(g => g.Projects.Count).Take(MaxGroups).ToList();
    }

    /// <summary>The key without its tier and project: <c>project/1626/tags/x</c> → <c>tags/x</c>.</summary>
    public static string TopicOf(MemoryEntry e)
    {
        var parts = e.Key.Split('/');
        if (parts.Length > 2 && (parts[0] == MemoryScopes.Project || parts[0] == MemoryScopes.Personal)) return string.Join("/", parts.Skip(2));
        if (parts.Length > 1) return string.Join("/", parts.Skip(1));
        return e.Key;
    }

    private static bool SameTier(MemoryEntry a, MemoryEntry b) =>
        a.Scope == b.Scope && string.Equals(a.Project ?? string.Empty, b.Project ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> Words(string text) =>
        new(MemoryRecall.Fold(text)
            .Split(new[] { ' ', ',', '.', ';', ':', '(', ')', '"', '\'', '-', '/', '=' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3), StringComparer.Ordinal);

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var inter = a.Count(b.Contains);
        return (double)inter / (a.Count + b.Count - inter);
    }

    // ── Last run ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class LastRunDto
    {
        [JsonProperty("lastRun")] public DateTimeOffset LastRun { get; set; }
        [JsonProperty("by")] public string By { get; set; } = string.Empty;
    }

    public static DateTimeOffset? ReadLastRun(string tierFolder)
    {
        try
        {
            var path = Path.Combine(tierFolder, LastRunFileName);
            if (!File.Exists(path)) return null;
            return JsonConvert.DeserializeObject<LastRunDto>(File.ReadAllText(path, Encoding.UTF8))?.LastRun;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void WriteLastRun(string tierFolder, DateTimeOffset when, string by) =>
        MemoryStore.AtomicWrite(tierFolder, LastRunFileName, JsonConvert.SerializeObject(new LastRunDto { LastRun = when, By = by }));
}
