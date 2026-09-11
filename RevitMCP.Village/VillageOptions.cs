using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RevitMCP.Village;

/// <summary>
/// Project Village settings, read from the <c>village</c> object of the user config
/// (<c>%AppData%\RKTools\MCP\user.config.json</c>) with the company config as fallback.
/// Every value is clamped into a safe range; the listener is opt-in and loopback-only.
/// No Revit API dependency.
/// </summary>
public sealed class VillageOptions
{
    public const string ConfigSection = "village";
    public const string LoopbackHost = "127.0.0.1";
    public const int DefaultPort = 47800;
    public const int PortFallbackAttempts = 10;

    /// <summary>Opt-in: the loopback listener changes the connector's local network surface.</summary>
    public bool Enabled { get; set; }

    /// <summary>Always a loopback address. Anything else is coerced back to <see cref="LoopbackHost"/>.</summary>
    public string Host { get; set; } = LoopbackHost;

    public int Port { get; set; } = DefaultPort;

    /// <summary>When the port is busy (another Revit instance), try the next <see cref="PortFallbackAttempts"/> ports.</summary>
    public bool PortFallback { get; set; } = true;

    /// <summary>Bounded event queue between the connector hooks and the village consumer.</summary>
    public int QueueSize { get; set; } = 2000;

    /// <summary>Low-priority events above this rate are dropped before they reach the queue.</summary>
    public int MaxEventsPerSecond { get; set; } = 200;

    /// <summary>Events in the same area within this window merge into one story step.</summary>
    public int AggregationWindowMs { get; set; } = 1500;

    /// <summary>Story steps kept for the feed and for a late-connecting viewer.</summary>
    public int RecentActivityLimit { get; set; } = 200;

    /// <summary>Replay-ready step history retained in memory (never persisted in this version).</summary>
    public int HistoryLimit { get; set; } = 500;

    /// <summary>How often the graph file is checked for changes (size/timestamp only unless changed).</summary>
    public int GraphRefreshSeconds { get; set; } = 60;

    /// <summary>Agents with no activity for this long are shown as disconnected.</summary>
    public int AgentIdleSeconds { get; set; } = 300;

    /// <summary>A character idle this long walks to the park in the village centre.</summary>
    public int ParkAfterSeconds { get; set; } = 120;

    /// <summary>Viewer default; the viewer has its own slider.</summary>
    public double AnimationSpeed { get; set; } = 1.0;

    public int MaxViewers { get; set; } = 8;
    public int MaxBuildings { get; set; } = 14;

    /// <summary>
    /// Category warehouses drawn in the yard, largest categories first. Zero hides the yard; the
    /// map grows taller with every extra row of five, so the default stays modest.
    /// </summary>
    public int MaxWarehouses { get; set; } = VillageWarehouseYard.DefaultMax;

    public int MaxEffects { get; set; } = 6;
    public int ReconnectBackoffMs { get; set; } = 1000;
    public int ReconnectBackoffMaxMs { get; set; } = 15000;

    /// <summary>Writes village diagnostics into the connector's startup log.</summary>
    public bool DiagnosticLogging { get; set; }

    /// <summary>
    /// Revit categories that never get a warehouse (<c>village.warehouseExcludeCategories</c>).
    /// A configured list replaces the defaults in <see cref="VillageWarehouseYard.DefaultExcludedCategories"/>.
    /// </summary>
    public List<string> WarehouseExcludeCategories { get; set; } = new(VillageWarehouseYard.DefaultExcludedCategories);

    /// <summary>Per-tool area overrides (<c>village.toolAreas</c>).</summary>
    public Dictionary<string, string> ToolAreas { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-tool activity overrides (<c>village.toolActivities</c>).</summary>
    public Dictionary<string, string> ToolActivities { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raw <c>village.themes</c> object, parsed by <c>VillageThemeConfig</c>. Null = built-in mappings.</summary>
    public string? ThemesJson { get; set; }

    public string BaseUrl => "http://" + Host + ":" + Port.ToString(CultureInfo.InvariantCulture) + "/";

    public static VillageOptions Default => new();

    /// <summary>
    /// Merges the <c>village</c> sections of the user and company configs (user wins per key)
    /// and clamps everything into range. Missing or malformed values fall back to defaults.
    /// </summary>
    public static VillageOptions FromConfig(JsonObject? userConfig, JsonObject? companyConfig)
    {
        var o = new VillageOptions();
        var company = Section(companyConfig);
        var user = Section(userConfig);

        o.Enabled = Bool(user, company, "enabled", o.Enabled);
        o.Host = CoerceHost(Str(user, company, "host", o.Host));
        o.Port = Int(user, company, "port", o.Port, 1024, 65535);
        o.PortFallback = Bool(user, company, "portFallback", o.PortFallback);
        o.QueueSize = Int(user, company, "queueSize", o.QueueSize, 100, 20000);
        o.MaxEventsPerSecond = Int(user, company, "maxEventsPerSecond", o.MaxEventsPerSecond, 10, 5000);
        o.AggregationWindowMs = Int(user, company, "aggregationWindowMs", o.AggregationWindowMs, 200, 10000);
        o.RecentActivityLimit = Int(user, company, "recentActivityLimit", o.RecentActivityLimit, 20, 2000);
        o.HistoryLimit = Int(user, company, "historyLimit", o.HistoryLimit, 50, 5000);
        o.GraphRefreshSeconds = Int(user, company, "graphRefreshSeconds", o.GraphRefreshSeconds, 10, 3600);
        o.AgentIdleSeconds = Int(user, company, "agentIdleSeconds", o.AgentIdleSeconds, 30, 3600);
        o.ParkAfterSeconds = Int(user, company, "parkAfterSeconds", o.ParkAfterSeconds, 15, 3600);
        o.AnimationSpeed = Dbl(user, company, "animationSpeed", o.AnimationSpeed, 0.1, 5.0);
        o.MaxViewers = Int(user, company, "maxViewers", o.MaxViewers, 1, 32);
        o.MaxBuildings = Int(user, company, "maxBuildings", o.MaxBuildings, 8, 16);
        o.MaxWarehouses = Int(user, company, "maxWarehouses", o.MaxWarehouses, 0, 20);
        o.MaxEffects = Int(user, company, "maxEffects", o.MaxEffects, 1, 24);
        o.ReconnectBackoffMs = Int(user, company, "reconnectBackoffMs", o.ReconnectBackoffMs, 250, 60000);
        o.ReconnectBackoffMaxMs = Int(user, company, "reconnectBackoffMaxMs", o.ReconnectBackoffMaxMs, o.ReconnectBackoffMs, 300000);
        o.DiagnosticLogging = Bool(user, company, "diagnosticLogging", o.DiagnosticLogging);

        CopyMap(company, "toolAreas", o.ToolAreas);
        CopyMap(user, "toolAreas", o.ToolAreas);
        CopyMap(company, "toolActivities", o.ToolActivities);
        CopyMap(user, "toolActivities", o.ToolActivities);

        var exclude = Strings(user, company, "warehouseExcludeCategories");
        if (exclude != null) o.WarehouseExcludeCategories = exclude;

        var themes = user?["themes"] as JsonObject ?? company?["themes"] as JsonObject;
        o.ThemesJson = themes?.ToJsonString();
        return o;
    }

    /// <summary>Only loopback hosts are accepted; anything else is replaced by 127.0.0.1.</summary>
    public static string CoerceHost(string? host)
    {
        var h = (host ?? string.Empty).Trim().ToLowerInvariant();
        return h == "127.0.0.1" || h == "localhost" || h == "::1" || h == "[::1]" ? h : LoopbackHost;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static JsonObject? Section(JsonObject? root) => root?[ConfigSection] as JsonObject;

    private static JsonNode? Pick(JsonObject? user, JsonObject? company, string key)
    {
        if (user != null && user.TryGetPropertyValue(key, out var u) && u != null) return u;
        if (company != null && company.TryGetPropertyValue(key, out var c) && c != null) return c;
        return null;
    }

    private static bool Bool(JsonObject? user, JsonObject? company, string key, bool fallback)
    {
        var node = Pick(user, company, key);
        if (node is not JsonValue value) return fallback;
        if (value.TryGetValue<bool>(out var b)) return b;
        if (value.TryGetValue<string>(out var s) && bool.TryParse(s, out var parsed)) return parsed;
        return fallback;
    }

    private static string Str(JsonObject? user, JsonObject? company, string key, string fallback)
    {
        var node = Pick(user, company, key);
        if (node is JsonValue value && value.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) return s;
        return fallback;
    }

    private static int Int(JsonObject? user, JsonObject? company, string key, int fallback, int min, int max)
    {
        var node = Pick(user, company, key);
        if (node is not JsonValue value) return fallback;
        int result;
        if (value.TryGetValue<int>(out var i)) result = i;
        else if (value.TryGetValue<long>(out var l)) result = l > int.MaxValue ? int.MaxValue : l < int.MinValue ? int.MinValue : (int)l;
        else if (value.TryGetValue<double>(out var d) && !double.IsNaN(d)) result = d > int.MaxValue ? int.MaxValue : d < int.MinValue ? int.MinValue : (int)d;
        else if (value.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)) result = p;
        else return fallback;
        return result < min ? min : result > max ? max : result;
    }

    private static double Dbl(JsonObject? user, JsonObject? company, string key, double fallback, double min, double max)
    {
        var node = Pick(user, company, key);
        if (node is not JsonValue value) return fallback;
        double result;
        if (value.TryGetValue<double>(out var d)) result = d;
        else if (value.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) result = p;
        else return fallback;
        if (double.IsNaN(result) || double.IsInfinity(result)) return fallback;
        return result < min ? min : result > max ? max : result;
    }

    /// <summary>A configured string array, or null when neither config supplies one.</summary>
    private static List<string>? Strings(JsonObject? user, JsonObject? company, string key)
    {
        if (Pick(user, company, key) is not JsonArray array) return null;
        var list = new List<string>();
        foreach (var item in array)
        {
            if (item is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                list.Add(s.Trim());
            if (list.Count >= 200) break;
        }
        return list;
    }

    private static void CopyMap(JsonObject? section, string key, Dictionary<string, string> target)
    {
        if (section?[key] is not JsonObject map) return;
        foreach (var kv in map)
        {
            if (kv.Value is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(kv.Key))
                target[kv.Key] = s;
        }
    }

    /// <summary>Parses a config file body defensively. Returns null for anything that is not a JSON object.</summary>
    public static JsonObject? ParseConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonNode.Parse(json!, null, new JsonDocumentOptions { MaxDepth = 32 }) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
