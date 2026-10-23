namespace RevitMCP.Addin.Memory;

/// <summary>Memory tiers. Company exists in the format; writing it arrives with the proposal flow (phase 2).</summary>
public static class MemoryScopes
{
    public const string Personal = "personal";
    public const string Project = "project";
    public const string Company = "company";

    public static bool IsKnown(string? scope) => scope is Personal or Project or Company;
}

/// <summary>Lifecycle states (docs/shared-memory.md, "Keeping only what is useful").</summary>
public static class MemoryStates
{
    public const string Candidate = "candidate";
    public const string Confirmed = "confirmed";
    public const string Dormant = "dormant";
    public const string Retired = "retired";

    public static bool IsKnown(string? state) => state is Candidate or Confirmed or Dormant or Retired;
}

/// <summary>Who produced an entry, so provenance is never ambiguous.</summary>
public static class MemorySources
{
    public const string Observed = "observed";
    public const string Agent = "agent";
    public const string Human = "human";

    public static bool IsKnown(string? source) => source is Observed or Agent or Human;
}

/// <summary>Usage counters. Recall adds to them through per-user tally files, never by rewriting the entry.</summary>
public sealed class MemoryCounters
{
    public int Shown { get; set; }
    public int Used { get; set; }
    public int Worked { get; set; }
    public int Contradicted { get; set; }

    /// <summary>How many times the same key and value were written again (by anyone).</summary>
    public int Remembered { get; set; }

    public MemoryCounters Clone() => (MemoryCounters)MemberwiseClone();

    public void Add(MemoryCounters other)
    {
        Shown += other.Shown;
        Used += other.Used;
        Worked += other.Worked;
        Contradicted += other.Contradicted;
        Remembered += other.Remembered;
    }
}

/// <summary>
/// One durable note: a decision, preference, exception or correction and why. Stored as one Markdown file
/// with front matter per entry (<see cref="MemoryEntryParser"/>), named by <see cref="Id"/>. Pure — no Revit API.
/// </summary>
public sealed class MemoryEntry
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Merge key, e.g. <c>project/1626/tags/detector-tag-type</c> (<see cref="MemoryPolicy.FullKey"/>).</summary>
    public string Key { get; set; } = string.Empty;
    public string Scope { get; set; } = MemoryScopes.Project;
    public string? Project { get; set; }

    /// <summary>EP | PP | TP | TJ, or null.</summary>
    public string? Stage { get; set; }
    public string? Model { get; set; }

    /// <summary>The model version the entry was written against (the graph's version signal).</summary>
    public string? ModelVersion { get; set; }
    public string Source { get; set; } = MemorySources.Agent;
    public string Author { get; set; } = string.Empty;
    public string? Client { get; set; }
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Updated { get; set; }
    public string State { get; set; } = MemoryStates.Candidate;
    public string? Supersedes { get; set; }
    public List<string> Tags { get; set; } = new();

    /// <summary>Everyone who wrote this key and value; "seen from two users" is a confirmation signal.</summary>
    public List<string> Authors { get; set; } = new();

    /// <summary>Review flags, e.g. <see cref="MemoryPolicy.FlagFlipFlop"/>.</summary>
    public List<string> Flags { get; set; } = new();
    public MemoryCounters Counters { get; set; } = new();
    public string Value { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;

    public bool IsRetired => State == MemoryStates.Retired;

    public MemoryEntry Clone()
    {
        var copy = (MemoryEntry)MemberwiseClone();
        copy.Tags = new List<string>(Tags);
        copy.Authors = new List<string>(Authors);
        copy.Flags = new List<string>(Flags);
        copy.Counters = Counters.Clone();
        return copy;
    }
}
