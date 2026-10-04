namespace RevitMCP.Addin.Query;

/// <summary>One value group of revit_group_by_parameter.</summary>
public sealed class ParameterValueGroup
{
    public string Name { get; init; } = string.Empty;
    public int Count { get; init; }
    /// <summary>Element ids of the group, capped at the per-group page; null unless requested.</summary>
    public List<long>? ElementIds { get; init; }
    /// <summary>True when <see cref="ElementIds"/> holds fewer ids than <see cref="Count"/>.</summary>
    public bool? ElementIdsTruncated { get; init; }
}

/// <summary>Counts for revit_group_by_parameter, always computed over every grouped element.</summary>
public sealed class ParameterGroupSummary
{
    public const string NotFoundKey = "(not found)";

    /// <summary>Elements the groups were computed from (all matches within the safety cap).</summary>
    public int ElementsGrouped { get; init; }
    /// <summary>Elements that have a parameter matching the requested name.</summary>
    public int MatchedElements { get; init; }
    /// <summary>Elements without a matching parameter (excluded from <see cref="Groups"/>).</summary>
    public int NotFoundElements { get; init; }
    public List<ParameterValueGroup> Groups { get; init; } = new();

    /// <summary>
    /// Builds the summary from grouping rows. Counts are never paged; only the per-group
    /// element id lists are, at <paramref name="maxElementIdsPerGroup"/> ids each.
    /// </summary>
    public static ParameterGroupSummary Build(
        IReadOnlyList<GroupRow> rows,
        int elementsGrouped,
        bool includeElementIds,
        int maxElementIdsPerGroup)
    {
        var found = rows
            .Where(r => (r.Keys.Values.FirstOrDefault() ?? string.Empty) != NotFoundKey)
            .OrderByDescending(r => r.Count)
            .ToList();
        var matched = found.Sum(r => r.Count);
        var idCap = Math.Max(0, maxElementIdsPerGroup);

        var groups = found.Select(r =>
        {
            List<long>? ids = null;
            bool? truncated = null;
            if (includeElementIds)
            {
                var all = r.ElementIds ?? new List<long>();
                ids = all.Take(idCap).ToList();
                truncated = all.Count > ids.Count;
            }

            return new ParameterValueGroup
            {
                Name = r.Keys.Values.FirstOrDefault() ?? string.Empty,
                Count = r.Count,
                ElementIds = ids,
                ElementIdsTruncated = truncated
            };
        }).ToList();

        return new ParameterGroupSummary
        {
            ElementsGrouped = elementsGrouped,
            MatchedElements = matched,
            NotFoundElements = Math.Max(0, elementsGrouped - matched),
            Groups = groups
        };
    }
}
