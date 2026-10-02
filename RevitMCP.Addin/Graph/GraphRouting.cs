using System.Text.RegularExpressions;

namespace RevitMCP.Addin.Graph;

/// <summary>What the router knows about the open model's graph (all routing data, never facts).</summary>
public sealed class GraphRoutingState
{
    public bool Exists { get; set; }
    public bool Stale { get; set; }
    public string? StaleReason { get; set; }
    public Dictionary<string, long> NodesByKind { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Element categories present in the graph (largest first).</summary>
    public List<string> Categories { get; set; } = [];
    public List<string> Levels { get; set; } = [];

    public bool Usable => Exists && !Stale;
    public long Count(string kind) => NodesByKind.TryGetValue(kind, out var n) ? n : 0;
}

/// <summary>One suggested tool call. Values in angle brackets come from the previous step's result.</summary>
public sealed class RouteStep
{
    public string Tool { get; set; } = string.Empty;
    public Dictionary<string, object?> Args { get; set; } = new();
    public string Why { get; set; } = string.Empty;
}

public sealed class RoutePlan
{
    /// <summary>room | panel | level | category | selection | linked | general</summary>
    public string Route { get; set; } = "general";
    public string Summary { get; set; } = string.Empty;
    public List<RouteStep> Steps { get; set; } = [];
    public List<string> Notes { get; set; } = [];
    public bool GraphUsable { get; set; }
    public string? Category { get; set; }
    public string? Level { get; set; }
    public string? NameHint { get; set; }
}

/// <summary>
/// Graph-first routing (#64): turns an intent into the cheapest discovery plan —
/// summary → narrow ids in the graph → live read by id → act — and phrases the hint attached to
/// broad live queries. Pure logic, no Revit API — unit tested in RevitMCP.Tests.
/// </summary>
public static class GraphRouting
{
    /// <summary>Broad live results with at least this many matched elements get a graph-first hint.</summary>
    public const int BroadQueryThreshold = 50;

    /// <summary>Live tools that scan a category and benefit from graph-narrowed ids.</summary>
    public static readonly HashSet<string> BroadLiveTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "revit_get_elements_info",
        "revit_find_elements_by_parameter",
        "revit_group_by_parameter",
        "revit_group_elements",
        "revit_export_query_to_excel"
    };

    private static readonly (string Route, string[] Words)[] Keywords =
    {
        ("linked", new[] { "linked", "link", "links", "lingitud", "lingis", "lingist", "ifc" }),
        ("selection", new[] { "selected", "selection", "valitud", "valik", "valiku" }),
        ("panel", new[] { "panel", "panels", "circuit", "circuits", "fed", "feeds", "feeder", "loop", "loops", "board",
                          "kilp", "kilbi", "kilbist", "ahel", "ahela", "ahelad", "toide", "silmus", "silmuse" }),
        ("room", new[] { "room", "rooms", "space", "spaces", "ruum", "ruumi", "ruumis", "ruumid", "ruumide" }),
        ("level", new[] { "level", "levels", "floor", "storey", "story", "korrus", "korruse", "korrusel" })
    };

    /// <summary>The route an intent belongs to; category is detected separately against the graph's categories.</summary>
    public static string Classify(string? intent)
    {
        var words = Words(intent);
        foreach (var (route, keys) in Keywords)
            if (keys.Any(words.Contains)) return route;
        return "general";
    }

    public static RoutePlan Plan(string? intent, GraphRoutingState state, string? category = null, string? level = null, string? name = null)
    {
        var text = intent ?? string.Empty;
        var plan = new RoutePlan
        {
            Route = Classify(text),
            GraphUsable = state.Usable,
            Category = category ?? MatchName(text, state.Categories),
            Level = level ?? MatchName(text, state.Levels),
            NameHint = name ?? ExtractNameHint(text)
        };
        if (plan.Route == "general" && plan.Category != null) plan.Route = "category";
        if (plan.Route == "level" && plan.Category != null) plan.Route = "category";

        if (!state.Exists)
        {
            plan.Summary = "No graph for this model yet. Build it (seconds), then route through it; or use live tools directly.";
            plan.Steps.Add(Step("revit_graph_build", "Index ids and relationships (writes only the graph file).", new()));
            plan.Steps.Add(Step("revit_graph_route", "Re-plan once the graph exists.", new() { ["intent"] = text }));
            plan.Notes.Add("Live fallback: revit_count_elements (cheap) or a category-scoped live tool with compact=true and a small pageSize.");
            return plan;
        }

        if (state.Stale)
        {
            plan.Notes.Add($"The graph is stale ({state.StaleReason}). Rebuild with revit_graph_build, or treat every id as a hint and verify it live.");
            plan.Steps.Add(Step("revit_graph_build", "Refresh the stale graph first.", new()));
        }

        switch (plan.Route)
        {
            case "room":
                plan.Summary = "Room/space → devices in it: find the space node, follow located_in edges, read the devices live by id.";
                plan.Steps.Add(Step("revit_graph_query", "Find the room/space id.",
                    Args(("operation", "find"), ("kind", "space"), ("nameContains", plan.NameHint), ("level", plan.Level), ("pageSize", 20))));
                plan.Steps.Add(Step("revit_graph_query", "Everything located in it.",
                    Args(("operation", "neighbors"), ("id", "<space id>"), ("rel", "located_in"), ("direction", "in"))));
                plan.Steps.Add(LiveRead("Read live values only for those ids.", plan.Category));
                break;

            case "panel":
                plan.Summary = "Panel/circuit → fed elements: find the panel, take its fed_by subtree, read live by id.";
                plan.Steps.Add(Step("revit_graph_query", "Find the panel id.",
                    Args(("operation", "find"), ("kind", "panel"), ("nameContains", plan.NameHint), ("pageSize", 20))));
                plan.Steps.Add(Step("revit_graph_query", "Circuits and elements fed by it (circuits point at the panel, elements at their circuit).",
                    Args(("operation", "subtree"), ("id", "<panel id>"), ("rel", "fed_by"), ("depth", 2))));
                plan.Steps.Add(Step("revit_get_circuit_info", "Live circuit values for the circuit ids you need.", Args(("circuitId", "<circuit id>"))));
                if (state.Count("panel") == 0) plan.Notes.Add("The graph has no panel nodes — the model may have no electrical circuits.");
                break;

            case "selection":
                plan.Summary = "Selection → context: read the selection live, then use graph neighbours for its relationships.";
                plan.Steps.Add(Step("revit_get_selected_elements", "Ids of the current selection.", new()));
                plan.Steps.Add(Step("revit_graph_query", "Relationships of one selected element (room, circuit, host, level, type).",
                    Args(("operation", "neighbors"), ("id", "<element id>"))));
                break;

            case "linked":
                plan.Summary = "Linked models: query the link directly — linked elements are not in this graph version.";
                plan.Steps.Add(Step("ifc_list_links", "Link instance ids.", Args(("includeAllRevitLinks", true))));
                plan.Steps.Add(Step("revit_query_linked_elements", "Elements of one link by category or name.",
                    Args(("linkInstanceId", "<link id>"), ("category", plan.Category), ("limit", 100))));
                break;

            case "category":
            case "level":
                plan.Summary = plan.Category != null
                    ? $"Category '{plan.Category}'{(plan.Level != null ? $" on '{plan.Level}'" : "")}: get ids from the graph, then read only those live."
                    : $"Level '{plan.Level}': orient with the summary, then narrow by category and level.";
                if (plan.Category == null)
                    plan.Steps.Add(Step("revit_graph_summary", "Which categories exist on that level.", Args(("topN", 15))));
                plan.Steps.Add(Step("revit_graph_query", "Ids without reading parameters.",
                    Args(("operation", "find"), ("kind", "element"), ("category", plan.Category ?? "<category>"), ("level", plan.Level), ("pageSize", 200))));
                plan.Steps.Add(LiveRead("Read live values for the ids you need (page through large sets).", plan.Category));
                break;

            default:
                plan.Summary = "Orient first: the summary lists categories, levels and panels; then narrow ids with find/subtree/neighbors.";
                plan.Steps.Add(Step("revit_graph_summary", "Cheap overview of the model.", Args(("topN", 15))));
                plan.Steps.Add(Step("revit_graph_query", "Narrow to the ids you need.",
                    Args(("operation", "find"), ("kind", "element"), ("category", "<category>"), ("pageSize", 100))));
                plan.Steps.Add(LiveRead("Read live values by id.", null));
                break;
        }

        plan.Notes.Add("Graph names and hints are routing data, not facts: quote live values only, and verify ids live before writing.");
        return plan;
    }

    /// <summary>
    /// The hint attached to a broad live query, or null when none applies (already narrowed,
    /// small result, opt-out). Mentions the stale/missing graph so the agent knows the cost.
    /// </summary>
    public static string? BroadQueryHint(string tool, string? category, string? level, long matched, GraphRoutingState? state)
    {
        if (!BroadLiveTools.Contains(tool) || matched < BroadQueryThreshold) return null;

        var find = "revit_graph_query operation=find kind=element" +
                   (string.IsNullOrWhiteSpace(category) ? "" : $" category=\"{category}\"") +
                   (string.IsNullOrWhiteSpace(level) ? "" : $" level=\"{level}\"");
        var tail = $" then call {tool} with elementIds (compact=true) for just the elements you need. Pass graphHint=false to silence this.";

        if (state == null || !state.Exists)
            return $"{matched} elements were read live. Building the model graph (revit_graph_build, seconds) lets you get ids first with {find};{tail}";
        if (state.Stale)
            return $"{matched} elements were read live. The model graph is stale ({state.StaleReason}); after revit_graph_build, {find} returns the ids without reading parameters;{tail}";
        return $"{matched} elements were read live. Graph-first is cheaper: {find} returns the ids without reading parameters;{tail}";
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static RouteStep LiveRead(string why, string? category) =>
        Step("revit_get_elements_info", why, Args(("elementIds", "<ids from the previous step>"), ("compact", true),
            ("parameterNames", "<only the parameters you need>")));

    private static RouteStep Step(string tool, string why, Dictionary<string, object?> args) =>
        new() { Tool = tool, Why = why, Args = args };

    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] pairs)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (k, v) in pairs)
            if (v != null && !(v is string s && s.Length == 0)) d[k] = v;
        return d;
    }

    private static HashSet<string> Words(string? text) =>
        new(Regex.Split((text ?? string.Empty).ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length > 0));

    /// <summary>The longest known name (category or level) that appears in the text, case-insensitively.</summary>
    public static string? MatchName(string? text, IEnumerable<string> names)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return names
            .Where(n => !string.IsNullOrWhiteSpace(n) && n.Length >= 3)
            .Where(n => text!.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderByDescending(n => n.Length)
            .FirstOrDefault();
    }

    /// <summary>A quoted name, or a room/panel style token such as "1.12", "08", "JK-1".</summary>
    public static string? ExtractNameHint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var quoted = Regex.Match(text!, "[\"'“”„](?<v>[^\"'“”„]{1,60})[\"'“”„]");
        if (quoted.Success) return quoted.Groups["v"].Value.Trim();
        var token = Regex.Match(text!, @"\b(?<v>[A-Za-zÕÄÖÜõäöü]{0,4}[-_]?\d+(?:[.\-_]\d+)*[A-Za-z]?)\b");
        return token.Success ? token.Groups["v"].Value : null;
    }
}
