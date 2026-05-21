namespace RevitMCP.Addin.Query;

/// <summary>
/// Groups ElementInfoDto rows by one or more key definitions.
/// Returns both flat rows (for Excel/AI) and nested dictionary (for AI summaries).
/// </summary>
public class GroupingEngine
{
    public GroupingResult Group(IReadOnlyList<ElementInfoDto> elements, GroupingOptions options)
    {
        if (options.GroupBy.Count == 0)
            return GroupingResult.Failure("At least one groupBy key is required.");

        var keyNames = options.GroupBy.Select(GetKeyLabel).ToList();

        // Group elements by a string composite key.
        // Dictionary<string,string> does NOT override Equals/GetHashCode, so it cannot be
        // used directly as a GroupBy key — every instance would be treated as unique.
        var grouped = elements
            .GroupBy(e => string.Join("\x1F", options.GroupBy.Select(g => GetKeyValue(e, g))))
            .OrderByDescending(g => g.Count())
            .ToList();

        var flat = grouped.Select(g =>
        {
            // Rebuild the key dictionary from the first element in the group
            var first = g.First();
            var row = new GroupRow
            {
                Keys = keyNames
                    .Zip(options.GroupBy, (name, gk) => (name, val: GetKeyValue(first, gk)))
                    .ToDictionary(x => x.name, x => x.val),
                Count = g.Count()
            };
            if (options.IncludeElements)
                row.ElementIds = g.Select(e => e.ElementId).ToList();
            return row;
        }).ToList();

        var nested = BuildNested(flat, keyNames);

        return new GroupingResult
        {
            Success = true,
            Message = $"{flat.Count} groups from {elements.Count} elements.",
            GroupsFlat = flat,
            GroupsNested = nested,
            TotalElements = elements.Count,
            TotalGroups = flat.Count
        };
    }

    private static Dictionary<string, string> BuildCompositeKey(ElementInfoDto element, List<GroupKeyOptions> groupBy)
    {
        var key = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var g in groupBy)
            key[GetKeyLabel(g)] = GetKeyValue(element, g);
        return key;
    }

    private static string GetKeyLabel(GroupKeyOptions g) =>
        g.Type == "Parameter" ? g.ParameterName : g.Type;

    private static string GetKeyValue(ElementInfoDto element, GroupKeyOptions g)
    {
        switch (g.Type)
        {
            case "Category": return element.Category?.Trim() ?? string.Empty;
            case "Family":   return element.Family?.Trim()   ?? string.Empty;
            case "Type":     return element.Type?.Trim()     ?? string.Empty;
            case "Level":    return element.Level?.Trim()    ?? string.Empty;
            default: // Parameter
                foreach (var kv in element.Parameters)
                {
                    var p = kv.Value;
                    if (ScopeMatches(p.Scope, g.Scope) &&
                        ParameterMatcher.Matches(p.Name, g.ParameterName, g.ParameterMatchMode))
                    {
                        // Trim to prevent whitespace-only differences from producing duplicate groups
                        var val = p.Value?.Trim() ?? string.Empty;
                        return string.IsNullOrEmpty(val) ? "(empty)" : val;
                    }
                }
                return "(not found)";
        }
    }

    private static bool ScopeMatches(string paramScope, string filterScope) =>
        filterScope switch
        {
            "Instance" => paramScope == "Instance",
            "Type" => paramScope == "Type",
            _ => true
        };

    /// <summary>
    /// Builds a recursive Dictionary from flat rows.
    /// Leaf nodes are { "count": n } or { "count": n, "elementIds": [...] }.
    /// </summary>
    private static object BuildNested(List<GroupRow> rows, List<string> keyNames)
    {
        if (keyNames.Count == 0)
            return new object();

        var root = new Dictionary<string, object>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var node = root;
            for (var i = 0; i < keyNames.Count; i++)
            {
                var keyName = keyNames[i];
                var keyValue = row.Keys.TryGetValue(keyName, out var v) ? v : "(unknown)";

                if (i == keyNames.Count - 1)
                {
                    // Leaf
                    object leaf = row.ElementIds != null
                        ? new { count = row.Count, elementIds = row.ElementIds }
                        : (object)new { count = row.Count };
                    node[keyValue] = leaf;
                }
                else
                {
                    if (!node.TryGetValue(keyValue, out var child) || child is not Dictionary<string, object> childDict)
                    {
                        childDict = new Dictionary<string, object>(StringComparer.Ordinal);
                        node[keyValue] = childDict;
                    }
                    node = childDict;
                }
            }
        }

        return root;
    }
}
