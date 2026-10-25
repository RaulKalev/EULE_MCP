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
    /// <summary>
    /// Host-model element count per category in the graph: what a category-wide live read would return, roughly.
    /// Linked-model elements are not in it (host tools cannot read them); they are in <see cref="LinkedElementsByCategory"/>.
    /// </summary>
    public Dictionary<string, long> ElementsByCategory { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Linked-model element count per category (ids link:&lt;instance&gt;:&lt;id&gt;); only categories with any.</summary>
    public Dictionary<string, long> LinkedElementsByCategory { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Usable => Exists && !Stale;
    public long Count(string kind) => NodesByKind.TryGetValue(kind, out var n) ? n : 0;
    public long HostCount(string? category) =>
        category != null && ElementsByCategory.TryGetValue(category.Trim(), out var n) ? n : 0;
    public long LinkedCount(string? category) =>
        category != null && LinkedElementsByCategory.TryGetValue(category.Trim(), out var n) ? n : 0;

    /// <summary>
    /// Fills the counts from <see cref="GraphDatabase.RoutingCounts"/>: categories (host or linked, largest first),
    /// host and linked counts per category, levels and nodes per kind.
    /// </summary>
    public void SetCounts(GraphSummaryResult counts)
    {
        foreach (var c in counts.NodesByKind) NodesByKind[c.Key] = c.Count;
        foreach (var c in counts.ElementsByCategory.Where(c => !string.IsNullOrEmpty(c.Key)))
        {
            Categories.Add(c.Key);
            ElementsByCategory[c.Key] = c.Host;
            if (c.Linked > 0) LinkedElementsByCategory[c.Key] = c.Linked;
        }
        Levels = counts.ElementsByLevel.Select(c => c.Key).Where(k => !string.IsNullOrEmpty(k) && k != "(none)").ToList();
    }
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
        // A category that exists only in linked models: host tools would find nothing, so route to the links.
        if (plan.Route == "category" && state.HostCount(plan.Category) == 0 && state.LinkedCount(plan.Category) > 0)
        {
            plan.Route = "linked";
            plan.Notes.Add($"'{plan.Category}' is only in linked models in the graph ({state.LinkedCount(plan.Category)} elements, " +
                           "none in the host), so host tools return nothing for it.");
        }

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
                    Args(("operation", "neighbors"), ("id", "<space id>"), ("rel", "located_in"), ("direction", "in"), ("link", "host"))));
                plan.Steps.Add(LiveRead("Read live values only for those ids.", plan.Category));
                plan.Notes.Add("Linked-model elements in the room: the same neighbors call with link=links; their ids (link:<instance>:<id>) " +
                               "are read with revit_query_linked_elements, not the host tools.");
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
                plan.Summary = "Linked models: the graph indexes loaded links (ids link:<instance>:<id>); narrow there, then read the link live.";
                plan.Steps.Add(Step("revit_graph_query", "Link instances (id = linkInstanceId, extra.document = linked file).",
                    Args(("operation", "find"), ("kind", "link"))));
                plan.Steps.Add(Step("revit_graph_query", "Linked elements of the category; link=<instance id or part of the link name> narrows to one link.",
                    Args(("operation", "find"), ("category", plan.Category), ("level", plan.Level), ("link", "links"), ("pageSize", 100))));
                plan.Steps.Add(Step("revit_query_linked_elements", "Live values from the chosen link (host tools cannot read linked ids).",
                    Args(("linkInstanceId", "<link id>"), ("category", plan.Category), ("limit", 100))));
                plan.Notes.Add("Linked content reflects the links as loaded at the last graph build; reloading a link does not mark the graph stale.");
                break;

            case "category":
            case "level":
                plan.Summary = plan.Category != null
                    ? $"Category '{plan.Category}'{(plan.Level != null ? $" on '{plan.Level}'" : "")}: get ids from the graph, then read only those live."
                    : $"Level '{plan.Level}': orient with the summary, then narrow by category and level.";
                if (plan.Category == null)
                    plan.Steps.Add(Step("revit_graph_summary", "Which categories exist on that level.", Args(("topN", 15))));
                plan.Steps.Add(Step("revit_graph_query", "Host-model ids without reading parameters.",
                    Args(("operation", "find"), ("kind", "element"), ("category", plan.Category ?? "<category>"), ("level", plan.Level),
                        ("link", "host"), ("pageSize", 200))));
                plan.Steps.Add(LiveRead("Read live values for the ids you need (page through large sets).", plan.Category));
                if (state.LinkedCount(plan.Category) > 0)
                    plan.Notes.Add($"The graph also holds {state.LinkedCount(plan.Category)} '{plan.Category}' elements from linked models: " +
                                   "find with link=links, read them with revit_query_linked_elements.");
                break;

            default:
                plan.Summary = "Orient first: the summary lists categories, levels and panels; then narrow ids with find/subtree/neighbors.";
                plan.Steps.Add(Step("revit_graph_summary", "Cheap overview of the model.", Args(("topN", 15))));
                plan.Steps.Add(Step("revit_graph_query", "Narrow to the ids you need.",
                    Args(("operation", "find"), ("kind", "element"), ("category", "<category>"), ("link", "host"), ("pageSize", 100))));
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
                   (string.IsNullOrWhiteSpace(level) ? "" : $" level=\"{level}\"") +
                   " link=host";
        var tail = $" then call {tool} with elementIds (compact=true) for just the elements you need. Pass graphHint=false to silence this.";

        if (state == null || !state.Exists)
            return $"{matched} elements were read live. Building the model graph (revit_graph_build, seconds) lets you get ids first with {find};{tail}";
        if (state.Stale)
            return $"{matched} elements were read live. The model graph is stale ({state.StaleReason}); after revit_graph_build, {find} returns the ids without reading parameters;{tail}";
        return $"{matched} elements were read live. Graph-first is cheaper: {find} returns the ids without reading parameters;{tail}";
    }

    // ── graph-first gate (#96) ────────────────────────────────────────────────

    /// <summary>A category-wide live read of at least this many elements is answered with the graph route instead.</summary>
    public const int GateThreshold = 200;

    /// <summary>Status of a gated result: the tool did not run, the graph route is in Data.</summary>
    public const string GateStatus = "graph_first";

    /// <summary>Per-call bypass argument: false reads the whole category live after all.</summary>
    public const string GateArg = "graphFirst";

    /// <summary>Tools that return one record per element, so a whole category costs the most.</summary>
    public static readonly HashSet<string> GatedTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "revit_get_elements_info",
        "revit_find_elements_by_parameter"
    };

    /// <summary>What a live read asks for, as far as the gate cares.</summary>
    public sealed class BroadRead
    {
        public string Tool { get; set; } = string.Empty;
        public string? Category { get; set; }
        public bool HasElementIds { get; set; }
        public bool UseSelection { get; set; }
        /// <summary>Parameter filters narrow by live values, which the graph does not hold: never gated.</summary>
        public bool HasFilters { get; set; }
        public bool SummaryOnly { get; set; }
        /// <summary>The caller's graphFirst argument; false is the bypass.</summary>
        public bool GraphFirst { get; set; } = true;
    }

    /// <summary>
    /// True when the read is an unnarrowed scan of a whole category that the graph can narrow first: no ids, no
    /// selection, no parameter filters, not a summary. Whether it is big enough is <see cref="GateCount"/>'s call.
    /// </summary>
    public static bool IsGateCandidate(BroadRead read) =>
        GatedTools.Contains(read.Tool) && read.GraphFirst && !read.HasElementIds && !read.UseSelection && !read.HasFilters
        && !read.SummaryOnly && !string.IsNullOrWhiteSpace(read.Category);

    /// <summary>
    /// The number of host-model elements the graph holds for the read's category when the read should be gated, else
    /// null: only with a fresh graph (a missing or stale one cannot be trusted to route), and only from
    /// <see cref="GateThreshold"/> host elements up. Linked-model elements never count: the live tool reads the host only.
    /// </summary>
    public static long? GateCount(BroadRead read, GraphRoutingState? state)
    {
        if (!IsGateCandidate(read) || state == null || !state.Usable) return null;
        var count = state.HostCount(read.Category);
        return count >= GateThreshold ? count : null;
    }

    /// <summary>The two calls that replace a gated read: host ids from the graph, then live values for the ids needed.</summary>
    public static List<RouteStep> GateSteps(BroadRead read) => new()
    {
        Step("revit_graph_query", "Host-model ids, names and levels of the category without reading parameters; add level or nameContains to narrow.",
            Args(("operation", "find"), ("kind", "element"), ("category", read.Category), ("link", "host"), ("pageSize", 200))),
        Step(read.Tool, "Live values only for the ids you need.",
            Args(("elementIds", "<ids from the previous step>"), ("compact", true)))
    };

    /// <param name="linked">Elements of the category in linked models, which the gate did not count; mentioned when above 0.</param>
    public static string GateMessage(BroadRead read, long count, long linked = 0) =>
        $"Not read: '{read.Category}' has about {count} host-model elements in the model graph" +
        (linked > 0 ? $" (plus {linked} in linked models, which this tool does not read)" : "") + ". Narrow in the graph first " +
        $"(revit_graph_query operation=find kind=element category=\"{read.Category}\" link=host), then call {read.Tool} with elementIds. " +
        $"To read the whole category live anyway, repeat this call with {GateArg}=false.";

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
