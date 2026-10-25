namespace RevitMCP.Addin.Checkpoints;

/// <summary>One element in a checkpoint diff.</summary>
public sealed class DiffEntry
{
    public long Id { get; set; }
    public NetChangeKind Kind { get; set; }
    public string By { get; set; } = ChangeAttribution.Unknown;
    public bool TouchedByAgent { get; set; }

    /// <summary>From the snapshot or the identity cache; null when the element was never seen.</summary>
    public ElementIdentity? Identity { get; set; }

    /// <summary>Snapshot comparison; null for elements that were not snapshotted or did not differ.</summary>
    public ElementDelta? Delta { get; set; }

    public bool Snapshotted { get; set; }
}

/// <summary>Everything that changed since a checkpoint, before paging and formatting.</summary>
public sealed class CheckpointDiff
{
    public Dictionary<long, DiffEntry> Entries { get; set; } = new();

    public IEnumerable<DiffEntry> Of(NetChangeKind kind) => Entries.Values.Where(e => e.Kind == kind).OrderBy(e => e.Id);

    public int Count(NetChangeKind kind) => Entries.Values.Count(e => e.Kind == kind);
}

/// <summary>
/// Combines the netted batches with the snapshot comparison (#98). The snapshot is authoritative for the
/// elements it covers: a snapshotted element that is gone is deleted, and one that differs is modified even
/// when no batch saw the change (Revit restart, tracking cap). Pure — no Revit API.
/// </summary>
public static class CheckpointDiffBuilder
{
    public static CheckpointDiff Build(Checkpoint checkpoint, IReadOnlyDictionary<long, ElementDelta> snapshotDeltas)
    {
        var diff = new CheckpointDiff();
        foreach (var change in ChangeNetting.Net(checkpoint.Batches).Values)
        {
            diff.Entries[change.Id] = new DiffEntry
            {
                Id = change.Id,
                Kind = change.Kind,
                By = change.By,
                TouchedByAgent = change.TouchedByAgent
            };
        }

        foreach (var pair in snapshotDeltas)
        {
            if (!pair.Value.HasChanges) continue;
            if (!diff.Entries.TryGetValue(pair.Key, out var entry))
            {
                entry = new DiffEntry { Id = pair.Key, Kind = NetChangeKind.Modified, By = ChangeAttribution.Unknown };
                diff.Entries[pair.Key] = entry;
            }
            if (pair.Value.Missing) entry.Kind = NetChangeKind.Deleted;
            else if (entry.Kind == NetChangeKind.Deleted) entry.Kind = NetChangeKind.Modified;   // it exists: the deletion was undone
            entry.Delta = pair.Value.Missing ? null : pair.Value;
        }

        foreach (var entry in diff.Entries.Values)
        {
            entry.Snapshotted = checkpoint.Snapshot.ContainsKey(entry.Id);
            if (checkpoint.Snapshot.TryGetValue(entry.Id, out var snap)) entry.Identity = snap;
            else if (checkpoint.Identities.TryGetValue(entry.Id, out var identity)) entry.Identity = identity;
        }
        return diff;
    }

    public static string? CategoryOf(Checkpoint checkpoint, long id)
    {
        if (checkpoint.Snapshot.TryGetValue(id, out var snap)) return snap.Category;
        return checkpoint.Identities.TryGetValue(id, out var identity) ? identity.Category : null;
    }
}
