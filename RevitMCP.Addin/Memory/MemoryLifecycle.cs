namespace RevitMCP.Addin.Memory;

/// <summary>
/// Lifecycle transitions (docs/shared-memory.md, "Keeping only what is useful"), computed from the counters, the
/// authors and the dates every time entries are read. Nothing is rewritten to move an entry between states, so the
/// shared folder stays conflict-free and the result is the same on every machine. The stored state only records
/// what a person or a write decided (retired by supersede, confirmed on review); the effective state adds the
/// evidence. Deterministic and pure — no Revit API.
/// </summary>
public static class MemoryLifecycle
{
    /// <summary>The state an entry is in now, and why (null when it is simply as stored).</summary>
    public static (string State, string? Why) Effective(MemoryEntry e, DateTimeOffset now, MemorySettings settings, Func<string, bool>? toolExists = null)
    {
        if (e.State == MemoryStates.Retired) return (MemoryStates.Retired, null);

        if (toolExists != null)
        {
            var missing = e.Requires.FirstOrDefault(t => !toolExists(t));
            if (missing != null) return (MemoryStates.Retired, $"names the tool {missing}, which no longer exists");
        }

        // Being shown counts as activity: a note that keeps matching what people ask stays in hints, even when its use
        // cannot be detected from tool arguments (most prose notes). Contradictions below still make it dormant.
        var lastActivity = e.Updated;
        if (e.Counters.LastUsed != null && e.Counters.LastUsed > lastActivity) lastActivity = e.Counters.LastUsed.Value;
        if (e.Counters.LastShown != null && e.Counters.LastShown > lastActivity) lastActivity = e.Counters.LastShown.Value;
        var idleDays = (now - lastActivity).TotalDays;
        if (idleDays >= settings.DormantAfterDays + settings.RetireAfterDays)
            return (MemoryStates.Retired, $"not used or shown for {(int)idleDays} days");
        if (idleDays >= settings.DormantAfterDays)
            return (MemoryStates.Dormant, $"not used or shown for {(int)idleDays} days");

        var c = e.Counters;
        if (c.Contradicted >= settings.ConfirmAfterUses && c.Contradicted > c.Worked)
            return (MemoryStates.Dormant, $"contradicted {c.Contradicted} times, worked {c.Worked}");

        // A disagreement between people is never promoted by usage; a person settles it on the review page.
        if (e.Flags.Contains(MemoryPolicy.FlagFlipFlop) && e.State != MemoryStates.Confirmed)
            return (MemoryStates.Candidate, "people disagree on this value");

        if (e.State == MemoryStates.Confirmed) return (MemoryStates.Confirmed, null);
        if (c.Worked >= settings.ConfirmAfterUses)
            return (MemoryStates.Confirmed, $"used successfully {c.Worked} times");
        var authors = new HashSet<string>(e.Authors.Append(e.Author).Where(a => !string.IsNullOrWhiteSpace(a)), StringComparer.OrdinalIgnoreCase);
        if (authors.Count >= 2)
            return (MemoryStates.Confirmed, $"written by {authors.Count} people");
        return (MemoryStates.Candidate, null);
    }

    /// <summary>Sets <see cref="MemoryEntry.State"/> and <see cref="MemoryEntry.Lifecycle"/> on every entry.</summary>
    public static void Apply(IEnumerable<MemoryEntry> entries, DateTimeOffset now, MemorySettings settings, Func<string, bool>? toolExists = null)
    {
        foreach (var e in entries)
        {
            var (state, why) = Effective(e, now, settings, toolExists);
            e.State = state;
            e.Lifecycle = why;
        }
    }

    /// <summary>Hints show confirmed entries, and candidates only to the people who wrote them.</summary>
    public static bool VisibleInHints(MemoryEntry e, string user) =>
        e.State == MemoryStates.Confirmed ||
        (e.State == MemoryStates.Candidate &&
         (string.Equals(e.Author, user, StringComparison.OrdinalIgnoreCase) || e.Authors.Contains(user, StringComparer.OrdinalIgnoreCase)));
}
