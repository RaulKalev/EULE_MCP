using System.Globalization;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Graph;

/// <summary>
/// The pure part of revit_graph_query's <c>read</c> argument: which graph nodes can be read live by the host
/// tools, and the <c>live</c> block that carries their values. One call then answers "find these and tell me
/// their X" instead of a graph query followed by a live read. No Revit API.
/// </summary>
public static class GraphLiveRead
{
    public const string Arg = "read";

    /// <summary>Most elements read live in one call; the rest are named in the block as truncated.</summary>
    public const int MaxElements = 500;

    public sealed class Selection
    {
        /// <summary>Host element ids to read, in graph order, without duplicates.</summary>
        public List<long> HostIds { get; } = new();
        /// <summary>Linked-model nodes (link:&lt;instance&gt;:&lt;id&gt;): host tools cannot read them.</summary>
        public int Linked { get; set; }
        /// <summary>Nodes that are not Revit elements (worksets and the like).</summary>
        public int NotElements { get; set; }
        /// <summary>Host ids left out because of <see cref="MaxElements"/>.</summary>
        public int Truncated { get; set; }
    }

    /// <summary>Parameter names asked for: trimmed, without blanks or duplicates (case-insensitive).</summary>
    public static List<string> Names(IEnumerable<string>? names)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names ?? Array.Empty<string>())
        {
            var trimmed = (name ?? string.Empty).Trim();
            if (trimmed.Length > 0 && seen.Add(trimmed)) result.Add(trimmed);
        }
        return result;
    }

    /// <summary>Splits the node ids of a graph result into what is read live and what is not.</summary>
    public static Selection Select(IEnumerable<string?> nodeIds, int max = MaxElements)
    {
        var selection = new Selection();
        var seen = new HashSet<long>();
        foreach (var nodeId in nodeIds)
        {
            if (GraphLinkIds.IsLinked(nodeId))
            {
                selection.Linked++;
                continue;
            }
            if (!long.TryParse(nodeId, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                selection.NotElements++;
                continue;
            }
            if (!seen.Add(id)) continue;
            if (selection.HostIds.Count >= max) selection.Truncated++;
            else selection.HostIds.Add(id);
        }
        return selection;
    }

    /// <summary>
    /// The <c>live</c> block: <c>values</c> maps element id to its parameter name/value pairs, read from the model
    /// now. Counts that are zero and lists that are empty are left out.
    /// </summary>
    /// <param name="values">Per element that was read: its values (possibly none of the asked names exist on it).</param>
    public static JObject Block(Selection selection, IReadOnlyList<string> names, IReadOnlyDictionary<long, Dictionary<string, string>> values)
    {
        var byId = new JObject();
        var notRead = new JArray();
        foreach (var id in selection.HostIds)
        {
            if (!values.TryGetValue(id, out var parameters))
            {
                notRead.Add(id);
                continue;
            }
            var entry = new JObject();
            foreach (var pair in parameters) entry[pair.Key] = pair.Value;
            byId[id.ToString(CultureInfo.InvariantCulture)] = entry;
        }

        var block = new JObject
        {
            ["parameters"] = new JArray(names),
            ["read"] = byId.Count,
            ["values"] = byId
        };
        if (notRead.Count > 0)
        {
            block["notRead"] = notRead;
            block["notReadNote"] = "In the graph but not readable in the live model (deleted since the graph was built, or not a model element).";
        }
        if (selection.Linked > 0) block["linkedSkipped"] = selection.Linked;
        if (selection.NotElements > 0) block["nonElementSkipped"] = selection.NotElements;
        if (selection.Truncated > 0) block["truncated"] = selection.Truncated;
        return block;
    }

    /// <summary>What the caller should know about a partial read; empty when everything asked for was read.</summary>
    public static List<string> Warnings(Selection selection, int read)
    {
        var warnings = new List<string>();
        if (selection.Truncated > 0)
            warnings.Add($"read: live values cover the first {selection.HostIds.Count} elements; {selection.Truncated} more were not read. Narrow the query or page through it.");
        if (selection.Linked > 0)
            warnings.Add($"read: {selection.Linked} linked-model node(s) were not read (host tools cannot); use revit_query_linked_elements for them.");
        var missing = selection.HostIds.Count - read;
        if (missing > 0)
            warnings.Add($"read: {missing} element(s) from the graph could not be read live (see live.notRead); the graph may be out of date.");
        return warnings;
    }
}
