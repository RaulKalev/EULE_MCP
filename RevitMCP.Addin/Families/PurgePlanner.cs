namespace RevitMCP.Addin.Families;

/// <summary>A loadable family type as the purge sees it. FamilyId 0 means a system type (purged only as "other").</summary>
public sealed class PurgeTypeInfo
{
    public long TypeId { get; set; }
    public string TypeName { get; set; } = string.Empty;
    public long FamilyId { get; set; }
    public string FamilyName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int InstanceCount { get; set; }
    public bool IsInPlace { get; set; }
    /// <summary>What Revit's own purge says (Document.GetUnusedElements): true unused, false still used, null unknown.</summary>
    public bool? UnusedPerRevit { get; set; }
    /// <summary>Why this type must never be purged (deviceMap, titleBlockOnSheet, …); null when nothing protects it.</summary>
    public string? Protection { get; set; }
}

/// <summary>A loaded family as the purge sees it.</summary>
public sealed class PurgeFamilyInfo
{
    public long FamilyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool IsInPlace { get; set; }
    public bool? UnusedPerRevit { get; set; }
    public string? Protection { get; set; }
}

/// <summary>Any other element Revit's purge lists as unused (line and fill patterns, materials, system types, …).</summary>
public sealed class PurgeOtherInfo
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? Protection { get; set; }
}

public sealed class PurgeOptions
{
    public List<string> Categories { get; set; } = new();
    public string FamilyNameContains { get; set; } = string.Empty;
    public int Passes { get; set; } = 1;
    public bool IncludeOther { get; set; }
}

public sealed class PurgeCandidate
{
    public long Id { get; set; }
    /// <summary>familyType | family | other.</summary>
    public string Kind { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string FamilyName { get; set; } = string.Empty;
    public long FamilyId { get; set; }
    public string Category { get; set; } = string.Empty;
    /// <summary>Protected items only: why it stays.</summary>
    public string? Reason { get; set; }
}

/// <summary>What one purge pass removes, and what it leaves on purpose.</summary>
public sealed class PurgePlan
{
    public List<PurgeCandidate> Types { get; } = new();
    public List<PurgeCandidate> Families { get; } = new();
    public List<PurgeCandidate> Other { get; } = new();
    public List<PurgeCandidate> Protected { get; } = new();
    public List<string> Notes { get; } = new();

    public bool IsEmpty => Types.Count == 0 && Families.Count == 0 && Other.Count == 0;

    /// <summary>Distinct families the purged types belong to (the M of "N types in M families").</summary>
    public int FamiliesTouched => Types.Select(t => t.FamilyId).Distinct().Count();

    /// <summary>
    /// The ids to hand to Document.Delete: each purged family (which takes its types along), the purged types of the
    /// families that stay, and the other elements. Families first, so their types are not deleted twice.
    /// </summary>
    public List<long> DeletionIds()
    {
        var purgedFamilies = new HashSet<long>(Families.Select(f => f.Id));
        return Families.Select(f => f.Id)
            .Concat(Types.Where(t => !purgedFamilies.Contains(t.FamilyId)).Select(t => t.Id))
            .Concat(Other.Select(o => o.Id))
            .Distinct()
            .ToList();
    }
}

/// <summary>
/// Decides what revit_preview_purge_unused / revit_purge_unused remove (#102), from type and family snapshots with
/// instance counts, Revit's own unused list where available, and protections the Revit side computed (company device
/// map, title blocks on sheets, view templates). Never purged: in-place families and their types, anything protected,
/// a type Revit says is still used, and the last type of a family that stays. No Revit API: unit tested.
/// </summary>
public static class PurgePlanner
{
    public const int MaxPasses = 3;
    public const int DeleteBatchSize = 500;

    public const string ReasonInPlace = "inPlace";
    public const string ReasonUsedPerRevit = "usedPerRevit";
    public const string ReasonLastType = "lastTypeOfKeptFamily";

    public static int NormalizePasses(int passes) => passes <= 0 ? 1 : Math.Min(passes, MaxPasses);

    public static PurgePlan Plan(
        IEnumerable<PurgeTypeInfo> types,
        IEnumerable<PurgeFamilyInfo> families,
        IEnumerable<PurgeOtherInfo> others,
        PurgeOptions options)
    {
        var plan = new PurgePlan();
        var familyById = families.GroupBy(f => f.FamilyId).ToDictionary(g => g.Key, g => g.First());
        var inScopeTypes = types
            .Where(t => t.FamilyId != 0)
            .Where(t => InCategories(t.Category, options.Categories))
            .Where(t => Contains(t.FamilyName, options.FamilyNameContains))
            .ToList();

        // Types: zero instances, nothing protecting them, and Revit does not object.
        var candidatesByFamily = new Dictionary<long, List<PurgeCandidate>>();
        var typesByFamily = inScopeTypes.GroupBy(t => t.FamilyId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var type in inScopeTypes.OrderBy(t => t.FamilyName, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.TypeName, StringComparer.OrdinalIgnoreCase))
        {
            if (type.InstanceCount > 0) continue;
            var family = familyById.TryGetValue(type.FamilyId, out var f) ? f : null;
            var reason = type.IsInPlace || family?.IsInPlace == true ? ReasonInPlace
                : type.Protection
                ?? (type.UnusedPerRevit == false ? ReasonUsedPerRevit : null);
            var candidate = TypeCandidate(type, reason);
            if (reason != null)
            {
                plan.Protected.Add(candidate);
                continue;
            }
            if (!candidatesByFamily.TryGetValue(type.FamilyId, out var list))
                candidatesByFamily[type.FamilyId] = list = new List<PurgeCandidate>();
            list.Add(candidate);
        }

        foreach (var (familyId, candidates) in candidatesByFamily.OrderBy(p => p.Key))
        {
            var allTypes = typesByFamily[familyId];
            var family = familyById.TryGetValue(familyId, out var f) ? f : null;
            var everyTypeUnused = candidates.Count == allTypes.Count;
            var familyReason = family == null ? "familyUnknown"
                : family.Protection ?? (family.UnusedPerRevit == false ? ReasonUsedPerRevit : null);

            if (everyTypeUnused && familyReason == null)
            {
                plan.Families.Add(new PurgeCandidate
                {
                    Id = familyId, Kind = "family", Name = family!.Name, FamilyName = family.Name, FamilyId = familyId,
                    Category = family.Category
                });
                plan.Types.AddRange(candidates);
                continue;
            }

            if (everyTypeUnused)
            {
                // The family stays, so it keeps one type: Revit cannot delete the last type of a family.
                var kept = candidates[0];
                kept.Reason = ReasonLastType;
                plan.Protected.Add(kept);
                plan.Types.AddRange(candidates.Skip(1));
                if (family != null && familyReason != null)
                    plan.Protected.Add(new PurgeCandidate
                    {
                        Id = familyId, Kind = "family", Name = family.Name, FamilyName = family.Name, FamilyId = familyId,
                        Category = family.Category, Reason = familyReason
                    });
                continue;
            }

            plan.Types.AddRange(candidates);
        }

        // Families with no types at all (left over after their types were purged in an earlier pass).
        foreach (var family in familyById.Values.Where(f => !typesByFamily.ContainsKey(f.FamilyId)))
        {
            if (!InCategories(family.Category, options.Categories) || !Contains(family.Name, options.FamilyNameContains)) continue;
            if (types.Any(t => t.FamilyId == family.FamilyId)) continue; // out-of-scope types still use it
            var reason = family.IsInPlace ? ReasonInPlace : family.Protection ?? (family.UnusedPerRevit == false ? ReasonUsedPerRevit : null);
            if (reason != null) continue; // nothing to report: an empty protected family is left as it is
            plan.Families.Add(new PurgeCandidate
            {
                Id = family.FamilyId, Kind = "family", Name = family.Name, FamilyName = family.Name, FamilyId = family.FamilyId,
                Category = family.Category
            });
        }

        if (options.IncludeOther)
        {
            if (options.FamilyNameContains.Length > 0)
                plan.Notes.Add("familyNameContains narrows the purge to families, so other unused elements were not included.");
            else
            {
                var listed = new HashSet<long>(plan.Types.Select(t => t.Id).Concat(plan.Families.Select(f => f.Id))
                    .Concat(plan.Protected.Select(p => p.Id)).Concat(types.Select(t => t.TypeId)).Concat(familyById.Keys));
                foreach (var other in others.Where(o => !listed.Contains(o.Id)).Where(o => InCategories(o.Category, options.Categories))
                             .OrderBy(o => o.Kind, StringComparer.OrdinalIgnoreCase).ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var candidate = new PurgeCandidate
                    {
                        Id = other.Id, Kind = "other", Name = other.Name, Category = other.Category, FamilyName = other.Kind,
                        Reason = other.Protection
                    };
                    (other.Protection != null ? plan.Protected : plan.Other).Add(candidate);
                }
            }
        }

        return plan;
    }

    /// <summary>
    /// Runs up to <paramref name="passes"/> purge passes: plan, apply, plan again. Purging a type can orphan a nested
    /// family that only the next pass sees, which is why Revit's own Purge Unused is run several times. Stops early
    /// when a pass finds nothing or removes nothing. <paramref name="apply"/> returns how many elements it removed.
    /// </summary>
    public static List<PurgePlan> RunPasses(int passes, Func<int, PurgePlan> plan, Func<PurgePlan, int> apply)
    {
        var results = new List<PurgePlan>();
        for (var pass = 1; pass <= NormalizePasses(passes); pass++)
        {
            var current = plan(pass);
            if (current.IsEmpty)
            {
                if (results.Count == 0) results.Add(current);
                break;
            }
            results.Add(current);
            if (apply(current) == 0) break;
        }
        return results;
    }

    public static IEnumerable<List<long>> Batches(IReadOnlyList<long> ids, int size = DeleteBatchSize)
    {
        if (size <= 0) size = DeleteBatchSize;
        for (var i = 0; i < ids.Count; i += size)
            yield return ids.Skip(i).Take(size).ToList();
    }

    /// <summary>The approval summary of revit_purge_unused.</summary>
    public static string Summary(int types, int families, int other)
    {
        var text = $"DESTRUCTIVE: purge {types} unused family type{(types == 1 ? "" : "s")} in {families} famil{(families == 1 ? "y" : "ies")}";
        if (other > 0) text += $" (+ {other} other unused element{(other == 1 ? "" : "s")})";
        return text + ". Manual approval required.";
    }

    public static string Summary(PurgePlan plan) => Summary(plan.Types.Count, plan.FamiliesTouched, plan.Other.Count);

    private static PurgeCandidate TypeCandidate(PurgeTypeInfo type, string? reason) => new()
    {
        Id = type.TypeId, Kind = "familyType", Name = type.TypeName, FamilyName = type.FamilyName, FamilyId = type.FamilyId,
        Category = type.Category, Reason = reason
    };

    private static bool InCategories(string category, List<string> categories) =>
        categories.Count == 0 || categories.Any(c => string.Equals(c.Trim(), category, StringComparison.OrdinalIgnoreCase));

    private static bool Contains(string value, string filter) =>
        string.IsNullOrEmpty(filter) || value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
}
