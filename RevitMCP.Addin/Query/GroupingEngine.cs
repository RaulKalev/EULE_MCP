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

        // Group elements by their composite key
        var grouped = elements
            .GroupBy(e => BuildCompositeKey(e, options.GroupBy))
            .OrderByDescending(g => g.Count())
            .ToList();

        var flat = grouped.Select(g =>
        {
            var row = new GroupRow
            {
                Keys = g.Key,
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
            case "Category": return element.Category;
            case "Family": return element.Family;
            case "Type": return element.Type;
            case "Level": return element.Level;
            default: // Parameter
                foreach (var kv in element.Parameters)
                {
                    var p = kv.Value;
                    if (ScopeMatches(p.Scope, g.Scope) &&
                        ParameterMatcher.Matches(p.Name, g.ParameterName, g.ParameterMatchMode))
                        return p.Value;
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
