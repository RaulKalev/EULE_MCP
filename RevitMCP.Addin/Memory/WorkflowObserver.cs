using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Memory;

/// <summary>One completed tool call as the observer sees it: allow-listed arguments only, nothing else retained.</summary>
public sealed class ObservedCall
{
    public string Tool { get; set; } = string.Empty;
    public string Client { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;

    /// <summary>Project number (or name) of the active model; empty when unknown.</summary>
    public string? Project { get; set; }

    /// <summary>Central (else local) model path, to find the project memory folder.</summary>
    public string? ModelPath { get; set; }

    /// <summary>The tool changes the model or a file (its permission is not read-only).</summary>
    public bool IsWrite { get; set; }
    public bool Success { get; set; }
    public string? Status { get; set; }
    public bool IsApproved { get; set; }

    /// <summary>Allow-listed argument values (<see cref="WorkflowObserver.ExtractArguments"/>).</summary>
    public Dictionary<string, string> Arguments { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>revit_graph_route: the route and detected category, e.g. <c>category/fire-alarm-devices</c>.</summary>
    public string? RouteKey { get; set; }
    public DateTimeOffset Time { get; set; } = DateTimeOffset.UtcNow;

    public bool Rejected => Status == WorkflowObserver.RejectedStatus;
}

/// <summary>A pattern that just crossed the support threshold (or changed value) and should become a candidate.</summary>
public sealed class ObservedPattern
{
    public const string KindDefault = "default";
    public const string KindRecipe = "recipe";
    public const string KindCorrection = "correction";

    [JsonProperty("kind")] public string Kind { get; set; } = string.Empty;
    [JsonProperty("topic")] public string Topic { get; set; } = string.Empty;
    [JsonProperty("value")] public string Value { get; set; } = string.Empty;
    [JsonProperty("reason")] public string Reason { get; set; } = string.Empty;
    [JsonProperty("tags")] public List<string> Tags { get; set; } = new();
    [JsonProperty("match", NullValueHandling = NullValueHandling.Ignore)] public MemoryMatch? Match { get; set; }
    [JsonProperty("requires")] public List<string> Requires { get; set; } = new();
}

/// <summary>Sightings of one pattern value.</summary>
public sealed class PatternStats
{
    [JsonProperty("count")] public int Count { get; set; }
    [JsonProperty("sessions")] public List<string> Sessions { get; set; } = new();
    [JsonProperty("last")] public DateTimeOffset Last { get; set; }

    /// <summary>Corrections: the rejected value.</summary>
    [JsonProperty("extra", NullValueHandling = NullValueHandling.Ignore)] public string? Extra { get; set; }

    public void See(string session, DateTimeOffset time)
    {
        Count++;
        Last = time;
        if (!Sessions.Contains(session)) Sessions.Add(session);
        if (Sessions.Count > WorkflowObserver.MaxSessionsKept) Sessions.RemoveAt(0);
    }
}

/// <summary>Values one argument of one tool was called with.</summary>
public sealed class ArgumentStats
{
    [JsonProperty("total")] public int Total { get; set; }
    [JsonProperty("values")] public Dictionary<string, PatternStats> Values { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>What the observer has counted for one project and one user. Persisted locally, never shared as is.</summary>
public sealed class ObserverState
{
    /// <summary><c>tool|argument</c> → values.</summary>
    [JsonProperty("defaults")] public Dictionary<string, ArgumentStats> Defaults { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Route key → tool chain ("a&gt;b&gt;c") → sightings.</summary>
    [JsonProperty("recipes")] public Dictionary<string, Dictionary<string, PatternStats>> Recipes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><c>tool|argument|accepted</c> → sightings (rejected value in Extra).</summary>
    [JsonProperty("corrections")] public Dictionary<string, PatternStats> Corrections { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Topic → value already turned into a candidate, so a restart does not write it again.</summary>
    [JsonProperty("emitted")] public Dictionary<string, string> Emitted { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Derives memory from what people do, deterministically and without any model call (docs/shared-memory.md,
/// "Learning from users' workflows"):
/// <list type="bullet">
/// <item><b>Recipes</b> — the tool chain that follows a revit_graph_route of the same kind, recurring per project.</item>
/// <item><b>Project defaults</b> — an allow-listed argument value a write tool is called with most of the time.</item>
/// <item><b>Corrections</b> — a rejected write followed by an accepted one with a different allow-listed value.</item>
/// </list>
/// A default or recipe is reported once it is seen <c>minSupport</c> times across <c>minSessions</c> sessions; a
/// correction already on its first sighting (a person rejecting a value is the signal). Only allow-listed argument
/// keys are read, each value capped in length; paths and free text never enter. The per-client session and chain
/// tracking lives here; the counts live in <see cref="ObserverState"/>. Pure — no Revit API.
/// </summary>
public sealed class WorkflowObserver
{
    public const string RejectedStatus = "approval_rejected";
    public const int MaxSessionsKept = 10;
    public const int MaxValueLength = 64;
    public const int MaxChainLength = 8;
    public static readonly TimeSpan SessionGap = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan CorrectionWindow = TimeSpan.FromMinutes(30);
    private const double DominantShare = 0.6;
    private const int MaxValuesPerArgument = 20;
    private const int MaxArgumentKeys = 300;
    private const int MaxRouteKeys = 100;
    private const int MaxChainsPerRoute = 10;
    private const int MaxCorrections = 200;

    /// <summary>Argument keys whose values may enter memory: names, types, categories, offsets — never free text.</summary>
    public static readonly HashSet<string> AllowedArguments = new(StringComparer.OrdinalIgnoreCase)
    {
        "category", "categoryName", "familyName", "family", "typeName", "type", "familyType", "typeId",
        "tagTypeId", "tagType", "tagTypeName", "tagFamily", "viewName", "viewTemplate", "viewTemplateName", "viewTemplateId",
        "titleblock", "titleblockName", "parameterName", "parameter", "level", "levelName",
        "offsetMm", "heightMm", "elevationMm", "offsetFromWallMm", "offsetFromCeilingMm", "spacingMm", "rotationDeg",
        "mode", "side", "alignment", "orientation", "deviceCode", "wireType", "wireTypeName", "cableType", "cableTypeName"
    };

    /// <summary>Calls that say nothing about how a project is worked: the connector's own plumbing and memory itself.</summary>
    public static bool IsIgnored(string tool) =>
        tool.StartsWith("revit_memory_", StringComparison.OrdinalIgnoreCase) ||
        tool.StartsWith("revit_tools_", StringComparison.OrdinalIgnoreCase) ||
        tool is "revit_get_approval_status" or "revit_get_connection_status" or "revit_list_instances" or "revit_select_instance";

    private sealed class ClientTrack
    {
        public string Session = string.Empty;
        public DateTimeOffset Last;
        public string? RouteKey;
        public List<string> Chain = new();
        public Dictionary<string, (Dictionary<string, string> Args, DateTimeOffset Time)> Rejected = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly Dictionary<string, ClientTrack> _clients = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Allow-listed scalar arguments as strings. Arrays, objects, empty values, long values and anything that
    /// looks like a path are skipped.
    /// </summary>
    public static Dictionary<string, string> ExtractArguments(IReadOnlyDictionary<string, object?>? args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (args == null) return result;
        foreach (var pair in args)
        {
            if (!AllowedArguments.Contains(pair.Key)) continue;
            var value = Scalar(pair.Value);
            if (string.IsNullOrWhiteSpace(value)) continue;
            value = value!.Trim();
            if (value.Length > MaxValueLength || value.IndexOfAny(new[] { '\\', '/', '\n', '\r' }) >= 0 || value.Contains("://")) continue;
            if (value == "0" && pair.Key.EndsWith("Id", StringComparison.OrdinalIgnoreCase)) continue;
            result[pair.Key] = value;
        }
        return result;
    }

    private static string? Scalar(object? value) => value switch
    {
        null => null,
        string s => s,
        bool b => b ? "true" : "false",
        IFormattable f when value is not DateTime and not DateTimeOffset => f.ToString(null, CultureInfo.InvariantCulture),
        JValue j when j.Type is JTokenType.String or JTokenType.Integer or JTokenType.Float or JTokenType.Boolean =>
            Convert.ToString(j.Value, CultureInfo.InvariantCulture),
        _ => null
    };

    /// <summary>Counts one call into <paramref name="state"/> and returns the patterns that should become candidates now.</summary>
    public List<ObservedPattern> Observe(ObserverState state, ObservedCall call, int minSupport, int minSessions)
    {
        var emitted = new List<ObservedPattern>();
        if (IsIgnored(call.Tool)) return emitted;

        var track = Track(call, emitted, state, minSupport, minSessions);

        if (call.Rejected)
        {
            if (call.Arguments.Count > 0) track.Rejected[call.Tool] = (new Dictionary<string, string>(call.Arguments, StringComparer.OrdinalIgnoreCase), call.Time);
            return emitted;
        }
        if (!call.Success) return emitted;

        if (call.Tool == "revit_graph_route")
        {
            CloseChain(track, state, emitted, minSupport, minSessions);
            track.RouteKey = string.IsNullOrWhiteSpace(call.RouteKey) ? null : call.RouteKey;
            if (track.RouteKey != null) track.Chain.Add(call.Tool);
            return emitted;
        }

        if (track.RouteKey != null && (track.Chain.Count == 0 || track.Chain[track.Chain.Count - 1] != call.Tool))
        {
            track.Chain.Add(call.Tool);
            if (track.Chain.Count >= MaxChainLength) CloseChain(track, state, emitted, minSupport, minSessions);
        }

        if (!call.IsWrite) return emitted;

        if (track.Rejected.TryGetValue(call.Tool, out var rejected))
        {
            track.Rejected.Remove(call.Tool);
            if (call.Time - rejected.Time <= CorrectionWindow) Corrections(state, call, rejected.Args, track.Session, emitted);
        }
        Defaults(state, call, track.Session, emitted, minSupport, minSessions);
        return emitted;
    }

    private ClientTrack Track(ObservedCall call, List<ObservedPattern> emitted, ObserverState state, int minSupport, int minSessions)
    {
        var key = call.Client + "|" + call.User + "|" + (call.Project ?? string.Empty);
        if (!_clients.TryGetValue(key, out var track))
        {
            track = new ClientTrack();
            _clients[key] = track;
        }
        if (track.Session.Length == 0 || call.Time - track.Last > SessionGap)
        {
            CloseChain(track, state, emitted, minSupport, minSessions);
            track.Rejected.Clear();
            track.Session = call.Client + "@" + call.Time.ToUniversalTime().ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
        }
        track.Last = call.Time;
        return track;
    }

    // ── Recipes ──────────────────────────────────────────────────────────────────────────────────────────

    private static void CloseChain(ClientTrack track, ObserverState state, List<ObservedPattern> emitted, int minSupport, int minSessions)
    {
        var chain = track.Chain;
        var routeKey = track.RouteKey;
        track.Chain = new List<string>();
        track.RouteKey = null;
        if (routeKey == null || chain.Count < 3) return;

        if (!state.Recipes.TryGetValue(routeKey, out var chains))
        {
            if (state.Recipes.Count >= MaxRouteKeys) DropOldest(state.Recipes, c => c.Values.Select(v => v.Last).DefaultIfEmpty().Max());
            state.Recipes[routeKey] = chains = new Dictionary<string, PatternStats>(StringComparer.Ordinal);
        }
        var signature = string.Join(">", chain);
        if (!chains.TryGetValue(signature, out var stats))
        {
            if (chains.Count >= MaxChainsPerRoute) DropOldest(chains, v => v.Last);
            chains[signature] = stats = new PatternStats();
        }
        stats.See(track.Session, track.Last);
        if (stats.Count < minSupport || stats.Sessions.Count < minSessions) return;

        var topic = "workflow/" + routeKey;
        var value = $"For {Readable(routeKey)} agents in this project use: {string.Join(" → ", chain)}.";
        if (state.Emitted.TryGetValue(topic, out var already) && already == value) return;
        state.Emitted[topic] = value;
        emitted.Add(new ObservedPattern
        {
            Kind = ObservedPattern.KindRecipe,
            Topic = topic,
            Value = value,
            Reason = $"Observed {stats.Count} times across {stats.Sessions.Count} sessions.",
            Tags = new List<string> { "workflow" },
            Requires = chain.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        });
    }

    /// <summary>A route key for revit_graph_route: <c>&lt;route&gt;/&lt;category&gt;</c>, else the first words of the intent.</summary>
    public static string? RouteKeyOf(string? route, string? category, string? intent)
    {
        var routeSlug = MemoryPolicy.Slug(route ?? string.Empty);
        var categorySlug = MemoryPolicy.Slug(category ?? string.Empty);
        if (routeSlug.Length > 0 && categorySlug.Length > 0) return routeSlug + "/" + categorySlug;
        var words = (intent ?? string.Empty).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Take(4);
        var intentSlug = MemoryPolicy.Slug(string.Join(" ", words));
        if (intentSlug.Length == 0) return routeSlug.Length > 0 ? routeSlug : null;
        return routeSlug.Length > 0 ? routeSlug + "/" + intentSlug : intentSlug;
    }

    private static string Readable(string routeKey)
    {
        var parts = routeKey.Split('/');
        return parts.Length == 2 ? $"{parts[1].Replace('-', ' ')} ({parts[0].Replace('-', ' ')} route)" : routeKey.Replace('-', ' ');
    }

    // ── Defaults ─────────────────────────────────────────────────────────────────────────────────────────

    private static void Defaults(ObserverState state, ObservedCall call, string session, List<ObservedPattern> emitted, int minSupport, int minSessions)
    {
        foreach (var arg in call.Arguments)
        {
            var key = call.Tool + "|" + arg.Key;
            if (!state.Defaults.TryGetValue(key, out var stats))
            {
                if (state.Defaults.Count >= MaxArgumentKeys) DropOldest(state.Defaults, a => a.Values.Values.Select(v => v.Last).DefaultIfEmpty().Max());
                state.Defaults[key] = stats = new ArgumentStats();
            }
            stats.Total++;
            if (!stats.Values.TryGetValue(arg.Value, out var seen))
            {
                if (stats.Values.Count >= MaxValuesPerArgument)
                    stats.Values.Remove(stats.Values.OrderBy(v => v.Value.Count).ThenBy(v => v.Value.Last).First().Key);
                stats.Values[arg.Value] = seen = new PatternStats();
            }
            seen.See(session, call.Time);

            if (seen.Count < minSupport || seen.Sessions.Count < minSessions || seen.Count < DominantShare * stats.Total) continue;
            var topic = $"defaults/{Short(call.Tool)}/{MemoryPolicy.Slug(arg.Key)}";
            if (state.Emitted.TryGetValue(topic, out var already) && already == arg.Value) continue;
            state.Emitted[topic] = arg.Value;
            emitted.Add(new ObservedPattern
            {
                Kind = ObservedPattern.KindDefault,
                Topic = topic,
                Value = $"In this project {call.Tool} is called with {arg.Key} = {arg.Value}.",
                Reason = $"Observed in {seen.Count} of {stats.Total} successful calls across {seen.Sessions.Count} sessions.",
                Tags = new List<string> { "defaults" },
                Match = new MemoryMatch { Tool = call.Tool, Argument = arg.Key, Value = arg.Value }
            });
        }
    }

    // ── Corrections ──────────────────────────────────────────────────────────────────────────────────────

    private static void Corrections(ObserverState state, ObservedCall call, Dictionary<string, string> rejected, string session, List<ObservedPattern> emitted)
    {
        foreach (var arg in call.Arguments)
        {
            if (!rejected.TryGetValue(arg.Key, out var before) || before == arg.Value) continue;
            var key = $"{call.Tool}|{arg.Key}|{arg.Value}";
            if (!state.Corrections.TryGetValue(key, out var stats))
            {
                if (state.Corrections.Count >= MaxCorrections) DropOldest(state.Corrections, c => c.Last);
                state.Corrections[key] = stats = new PatternStats();
            }
            stats.See(session, call.Time);
            stats.Extra = before;

            var topic = $"corrections/{Short(call.Tool)}/{MemoryPolicy.Slug(arg.Key)}";
            if (state.Emitted.TryGetValue(topic, out var already) && already == arg.Value) continue;
            state.Emitted[topic] = arg.Value;
            emitted.Add(new ObservedPattern
            {
                Kind = ObservedPattern.KindCorrection,
                Topic = topic,
                Value = $"{call.Tool}: {arg.Key} = {before} was rejected; {arg.Value} was accepted.",
                Reason = $"The user rejected the call with {arg.Key} = {before} and approved it with {arg.Value}.",
                Tags = new List<string> { "correction" },
                Match = new MemoryMatch { Tool = call.Tool, Argument = arg.Key, Value = arg.Value }
            });
        }
    }

    private static string Short(string tool) => MemoryPolicy.Slug(tool.StartsWith("revit_", StringComparison.Ordinal) ? tool.Substring(6) : tool);

    private static void DropOldest<T>(Dictionary<string, T> map, Func<T, DateTimeOffset> last)
    {
        if (map.Count == 0) return;
        map.Remove(map.OrderBy(p => last(p.Value)).First().Key);
    }
}
