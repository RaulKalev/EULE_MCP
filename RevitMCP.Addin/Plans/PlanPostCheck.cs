namespace RevitMCP.Addin.Plans;

/// <summary>What the post-check needs to know about one changed element, read while the call's group is still open.</summary>
public sealed class ElementFacts
{
    /// <summary>The OST_ name, e.g. OST_ElectricalFixtures; null for elements without a category.</summary>
    public string? BuiltInCategory { get; set; }

    /// <summary>The display name, e.g. "Electrical Fixtures".</summary>
    public string? Category { get; set; }
    public long? LevelId { get; set; }

    /// <summary>The owner view of a view-specific element; null for model elements.</summary>
    public long? OwnerViewId { get; set; }
    public string? Workset { get; set; }
}

/// <summary>The element ids Revit reported during one call, netted (added then deleted inside the call is gone).</summary>
public sealed class CallChanges
{
    public HashSet<long> Added { get; } = new();
    public HashSet<long> Modified { get; } = new();
    public HashSet<long> Deleted { get; } = new();

    public void Record(IEnumerable<long> added, IEnumerable<long> modified, IEnumerable<long> deleted)
    {
        foreach (var id in added)
        {
            if (Deleted.Remove(id)) Modified.Add(id);   // an undone deletion: the element exists again
            else Added.Add(id);
        }
        foreach (var id in modified)
            if (!Added.Contains(id)) Modified.Add(id);
        foreach (var id in deleted)
        {
            if (Added.Remove(id)) continue;   // created and removed within the call
            Modified.Remove(id);
            Deleted.Add(id);
        }
    }
}

public sealed class PostCheckResult
{
    /// <summary>Null when the call stays inside the envelope.</summary>
    public string? Violation { get; set; }
    public List<long> OffendingIds { get; set; } = new();

    /// <summary>Distinct ids the call counts against the plan's limits.</summary>
    public HashSet<long> Counted { get; set; } = new();
    public int Added { get; set; }
    public int Modified { get; set; }
    public int Deleted { get; set; }
}

/// <summary>
/// The authoritative half of a plan check (#99), on the ids Revit reported as changed during the call. A
/// violation rolls the whole call back. Rules: any deletion violates (plans never auto-approve deletes); an
/// element outside the element list, categories, levels, views or worksets violates; creating violates when the
/// plan does not allow it, modifying when it does not allow that; the per-call and total limits are hard.
/// Incidental categories (tags, dimensions, circuits, wires — see <see cref="PlanToolClassification"/>) are
/// exempt from the scope rules unless Strict, but elements added in them still count against the limits.
/// Pure.
/// </summary>
public static class PlanPostCheck
{
    public static PostCheckResult Check(Plan plan, CallChanges changes, Func<long, ElementFacts?> factsOf)
    {
        var env = plan.Envelope;
        var result = new PostCheckResult
        {
            Added = changes.Added.Count,
            Modified = changes.Modified.Count,
            Deleted = changes.Deleted.Count
        };

        if (changes.Deleted.Count > 0)
            return Fail(result, $"the call deleted {changes.Deleted.Count} element(s); a plan never approves deletions", changes.Deleted);

        var outsideScope = new List<long>();
        string? scopeReason = null;
        var created = new List<long>();
        var modified = new List<long>();

        foreach (var (id, isAdded) in changes.Added.Select(i => (i, true)).Concat(changes.Modified.Select(i => (i, false))))
        {
            var facts = factsOf(id);
            var incidental = !env.Strict && PlanToolClassification.IsIncidental(facts?.BuiltInCategory);
            if (incidental)
            {
                if (isAdded) result.Counted.Add(id);
                continue;
            }

            result.Counted.Add(id);
            if (isAdded) created.Add(id); else modified.Add(id);

            var reason = ScopeProblem(env, id, facts, isAdded);
            if (reason != null)
            {
                scopeReason ??= reason;
                outsideScope.Add(id);
            }
        }

        if (!env.AllowCreate && created.Count > 0)
            return Fail(result, $"the call created {created.Count} element(s) and the plan does not allow creating", created);
        if (!env.AllowModify && modified.Count > 0)
            return Fail(result, $"the call modified {modified.Count} element(s) and the plan does not allow modifying", modified);
        if (outsideScope.Count > 0)
            return Fail(result, $"{outsideScope.Count} changed element(s) are outside the plan: {scopeReason}", outsideScope);
        if (result.Counted.Count > env.MaxElementsPerCall)
            return Fail(result, $"the call changed {result.Counted.Count} elements; the plan allows {env.MaxElementsPerCall} per call", result.Counted);

        var total = new HashSet<long>(plan.Touched);
        total.UnionWith(result.Counted);
        if (total.Count > env.MaxElementsTotal)
            return Fail(result, $"the call would bring the plan to {total.Count} elements; it allows {env.MaxElementsTotal} in total", result.Counted.Where(id => !plan.Touched.Contains(id)));
        return result;
    }

    private static string? ScopeProblem(PlanEnvelope env, long id, ElementFacts? facts, bool isAdded)
    {
        // A new element cannot be on an id list written before it existed.
        if (!isAdded && env.ElementIds.Count > 0 && !env.ElementIds.Contains(id))
            return "not in the element list";
        if (env.Categories.Count > 0 &&
            !(facts?.Category is { } c && env.Categories.Contains(c)) &&
            !(facts?.BuiltInCategory is { } b && env.Categories.Contains(b)))
            return $"category {facts?.Category ?? facts?.BuiltInCategory ?? "(none)"} is not one of the plan's categories";
        if (env.LevelIds.Count > 0 && facts?.LevelId is long level && !env.LevelIds.Contains(level))
            return $"level {level} is not one of the plan's levels";
        if (env.ViewIds.Count > 0 && facts?.OwnerViewId is long view && !env.ViewIds.Contains(view))
            return $"it belongs to view {view}, not one of the plan's views";
        if (env.Worksets.Count > 0 && facts?.Workset is { Length: > 0 } ws && !env.Worksets.Contains(ws))
            return $"workset '{ws}' is not one of the plan's worksets";
        return null;
    }

    private static PostCheckResult Fail(PostCheckResult result, string reason, IEnumerable<long> ids)
    {
        result.Violation = reason;
        result.OffendingIds = ids.OrderBy(i => i).Take(200).ToList();
        return result;
    }
}
