namespace RevitMCP.Addin.Checkpoints;

public enum NetChangeKind
{
    Added,
    Modified,
    Deleted
}

/// <summary>The net effect on one element of every batch since a checkpoint.</summary>
public sealed class NetChange
{
    public long Id { get; set; }
    public NetChangeKind Kind { get; set; }

    /// <summary>Attribution of the latest batch that touched the element.</summary>
    public string By { get; set; } = ChangeAttribution.Unknown;

    /// <summary>True when an agent batch touched the element at some point.</summary>
    public bool TouchedByAgent { get; set; }
}

/// <summary>
/// Turns the batches recorded since a checkpoint into one net change per element (#98). Pure — no Revit API.
/// <para>
/// Rules: an element added and later deleted (including an undone addition) nets out; an element added and
/// then modified stays added; a deletion that is undone (the element reappears in a later batch's added
/// list) leaves the pre-existing element modified; the latest batch's attribution wins.
/// </para>
/// </summary>
public static class ChangeNetting
{
    public static Dictionary<long, NetChange> Net(IEnumerable<ChangeBatch> batches)
    {
        var net = new Dictionary<long, NetChange>();
        foreach (var batch in batches)
        {
            var by = string.IsNullOrEmpty(batch.Attribution) ? ChangeAttribution.Unknown : batch.Attribution;
            var agent = ChangeAttribution.IsAgent(by);

            foreach (var id in batch.Added)
            {
                if (net.TryGetValue(id, out var existing))
                {
                    // A deleted pre-existing element coming back is an undo of its deletion: it exists again, touched.
                    if (existing.Kind == NetChangeKind.Deleted) existing.Kind = NetChangeKind.Modified;
                    Touch(existing, by, agent);
                }
                else
                {
                    net[id] = New(id, NetChangeKind.Added, by, agent);
                }
            }

            foreach (var id in batch.Modified)
            {
                if (net.TryGetValue(id, out var existing))
                    Touch(existing, by, agent);   // added stays added; deleted cannot be modified, keep it
                else
                    net[id] = New(id, NetChangeKind.Modified, by, agent);
            }

            foreach (var id in batch.Deleted)
            {
                if (net.TryGetValue(id, out var existing))
                {
                    if (existing.Kind == NetChangeKind.Added)
                    {
                        net.Remove(id);   // created and removed again since the checkpoint: no net change
                        continue;
                    }
                    existing.Kind = NetChangeKind.Deleted;
                    Touch(existing, by, agent);
                }
                else
                {
                    net[id] = New(id, NetChangeKind.Deleted, by, agent);
                }
            }
        }
        return net;
    }

    /// <summary>Net change counts by attribution, e.g. {"agent:revit_move_elements": 340, "user": 12}.</summary>
    public static Dictionary<string, int> CountByAttribution(IEnumerable<NetChange> changes)
        => changes.GroupBy(c => c.By, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    /// <summary>Net change counts by category; elements with no known category count under "(unknown)".</summary>
    public static Dictionary<string, int> CountByCategory(IEnumerable<NetChange> changes, Func<long, string?> categoryOf)
        => changes.GroupBy(c => categoryOf(c.Id) is { Length: > 0 } cat ? cat : "(unknown)", StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    /// <summary>True for an attribution filter value ("agent" | "user" | "page" | "all" | a full attribution).</summary>
    public static bool MatchesAttribution(string by, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter) || string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(filter, "agent", StringComparison.OrdinalIgnoreCase)) return ChangeAttribution.IsAgent(by);
        if (string.Equals(filter, "user", StringComparison.OrdinalIgnoreCase)) return by == ChangeAttribution.User;
        if (string.Equals(filter, "page", StringComparison.OrdinalIgnoreCase)) return by == ChangeAttribution.Page;
        return string.Equals(by, filter, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ChangeAttribution.AgentTool(by), filter, StringComparison.OrdinalIgnoreCase);
    }

    private static NetChange New(long id, NetChangeKind kind, string by, bool agent)
        => new() { Id = id, Kind = kind, By = by, TouchedByAgent = agent };

    private static void Touch(NetChange change, string by, bool agent)
    {
        change.By = by;
        change.TouchedByAgent |= agent;
    }
}
