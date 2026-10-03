using System.Linq;

namespace RevitMCP.Addin.Graph;

/// <summary>
/// Element ids changed in one document since its graph was last built in this session (#61).
/// Ids that are both added and deleted cancel out; a modified id that is later deleted counts as
/// deleted. Past <see cref="MaxTrackedIds"/> the set overflows and the next build is a full one.
/// </summary>
public sealed class GraphChangeSet
{
    public const int MaxTrackedIds = 50_000;

    public HashSet<long> Added { get; } = [];
    public HashSet<long> Modified { get; } = [];
    public HashSet<long> Deleted { get; } = [];
    public bool Overflow { get; private set; }

    /// <summary>built_at of the graph these changes are relative to; null = nothing tracked yet.</summary>
    public string? BaselineBuiltAt { get; private set; }
    public string? BaselinePath { get; private set; }

    public int Count => Added.Count + Modified.Count + Deleted.Count;

    public void Record(IEnumerable<long> added, IEnumerable<long> modified, IEnumerable<long> deleted)
    {
        if (Overflow) return;
        foreach (var id in added) { Added.Add(id); Deleted.Remove(id); }
        foreach (var id in modified) if (!Added.Contains(id)) Modified.Add(id);
        foreach (var id in deleted)
        {
            if (Added.Remove(id)) continue;   // created and removed again since the build: nothing to do
            Modified.Remove(id);
            Deleted.Add(id);
        }
        if (Count > MaxTrackedIds)
        {
            Overflow = true;
            Added.Clear(); Modified.Clear(); Deleted.Clear();
        }
    }

    /// <summary>Starts tracking afresh against the graph that was just written.</summary>
    public void Reset(string builtAt, string databasePath)
    {
        Added.Clear(); Modified.Clear(); Deleted.Clear();
        Overflow = false;
        BaselineBuiltAt = builtAt;
        BaselinePath = databasePath;
    }

    public bool MatchesBaseline(string? builtAt, string databasePath) =>
        BaselineBuiltAt != null && builtAt != null &&
        string.Equals(BaselineBuiltAt, builtAt, StringComparison.Ordinal) &&
        string.Equals(BaselinePath, databasePath, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Facts the incremental decision needs.</summary>
public sealed class IncrementalInputs
{
    public bool GraphExists { get; set; }
    public bool BaselineMatches { get; set; }
    public bool Overflow { get; set; }
    public bool HasEdgeOwners { get; set; }
    public bool ElementLimitReached { get; set; }
    /// <summary>Changes a partial update cannot represent safely (level renamed, room boundary changed, …).</summary>
    public List<string> UnsafeChanges { get; set; } = [];
}

public sealed class IncrementalDecision
{
    public bool Incremental { get; set; }
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Decides between an incremental update and a full rebuild (#61). Errs towards the full rebuild:
/// it is always correct and takes seconds. Pure — unit tested in RevitMCP.Tests.
/// </summary>
public static class GraphIncrementalPlanner
{
    public static IncrementalDecision Decide(IncrementalInputs i)
    {
        string? why = null;
        if (!i.GraphExists) why = "no graph exists yet";
        else if (!i.HasEdgeOwners) why = "the graph predates edge ownership (schema 1); rebuilding it once enables incremental updates";
        else if (!i.BaselineMatches) why = "changes since the last build were not tracked in this session (graph built elsewhere, or the add-in was reloaded since)";
        else if (i.Overflow) why = $"more than {GraphChangeSet.MaxTrackedIds:N0} element changes since the last build";
        else if (i.ElementLimitReached) why = "the last build hit the element limit, so the graph is partial";
        else if (i.UnsafeChanges.Count > 0) why = string.Join("; ", i.UnsafeChanges.Distinct());

        return why == null
            ? new IncrementalDecision { Incremental = true, Reason = "tracked changes applied incrementally" }
            : new IncrementalDecision { Incremental = false, Reason = why };
    }

    /// <summary>
    /// Adds the graph nodes whose stored data derives from a changed element without the element
    /// itself being reported as changed: the previous panel of a changed or deleted circuit (it may
    /// stop being a panel), the circuits of a changed panel (their name and level come from the
    /// panel) and the instances of a changed type (their "type" hint is the type name).
    /// <paramref name="neighbors"/> is (id, rel, outgoing) → ids on the other end, read from the
    /// existing graph. Deleted ids are never returned.
    /// </summary>
    public static HashSet<string> ExpandRefreshSet(
        IEnumerable<string> refreshed,
        IEnumerable<string> deleted,
        Func<string, string?> kindOf,
        Func<string, string, bool, IEnumerable<string>> neighbors)
    {
        var deletedSet = new HashSet<string>(deleted, StringComparer.Ordinal);
        var result = new HashSet<string>(refreshed, StringComparer.Ordinal);
        foreach (var id in result.Concat(deletedSet).ToList())
        {
            switch (kindOf(id))
            {
                case GraphSchema.Kinds.Circuit:
                    foreach (var panel in neighbors(id, GraphSchema.Rels.FedBy, true))
                        if (kindOf(panel) == GraphSchema.Kinds.Panel) result.Add(panel);
                    break;
                case GraphSchema.Kinds.Panel when !deletedSet.Contains(id):
                    foreach (var circuit in neighbors(id, GraphSchema.Rels.FedBy, false))
                        if (kindOf(circuit) == GraphSchema.Kinds.Circuit) result.Add(circuit);
                    break;
                case GraphSchema.Kinds.Type when !deletedSet.Contains(id):
                    foreach (var instance in neighbors(id, GraphSchema.Rels.TypeOf, false))
                        result.Add(instance);
                    break;
            }
        }
        result.ExceptWith(deletedSet);
        return result;
    }

    /// <summary>
    /// Why a changed element makes a partial update unsafe, or null. Levels and rooms/spaces carry
    /// names and containment that unchanged elements depend on (level name on every node; an element's
    /// room changes when a room is added or its boundary moves, without the element itself changing).
    /// </summary>
    public static string? UnsafeReason(string? kind, bool added, bool deleted)
    {
        switch (kind)
        {
            case GraphSchema.Kinds.Level when !added:
                return deleted ? "a level was deleted" : "a level was changed (level names are stored on every node)";
            case GraphSchema.Kinds.Space when !deleted:
                return added
                    ? "a room/space was added (elements inside it change room without changing themselves)"
                    : "a room/space was changed (its boundary may now contain different elements)";
            case GraphSchema.Kinds.Workset:
                return "a workset changed";
            default:
                return null;
        }
    }
}
