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

    /// <summary>
    /// A character stays where it last worked this long before stepping out to the overlook, so a
    /// follow-up command goes straight to its building instead of via the overlook.
    /// </summary>
    public int OverlookAfterSeconds { get; set; } = 15;

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

    /// <summary>
    /// Only categories with a warehouse model of their own get a warehouse
    /// (<c>village.warehouseModelsOnly</c>, default on). Keeps run, sketch and centre-line
    /// categories out of the yard without listing them. Ignored when the models folder has no
    /// warehouse models, so a village without models still shows its yard.
    /// </summary>
    public bool WarehouseModelsOnly { get; set; } = true;

    /// <summary>
    /// Read tool results that return elements are pinned as flyers at the overlook
    /// (<c>village.flyersEnabled</c>, default on). Off: no result is read beyond the counts.
    /// </summary>
    public bool FlyersEnabled { get; set; } = true;

    /// <summary>Elements kept per flyer (<c>village.flyerMaxItems</c>); a larger result is marked truncated.</summary>
    public int FlyerMaxItems { get; set; } = 2000;

    /// <summary>Today's flyers kept on the board (<c>village.maxFlyers</c>); the oldest drop off first.</summary>
    public int MaxFlyers { get; set; } = 30;

    /// <summary>Days an archived flyer is kept (<c>village.flyerArchiveDays</c>).</summary>
    public int FlyerArchiveDays { get; set; } = 30;

    /// <summary>
    /// The page may select a flyer's elements in Revit (<c>village.showInRevit</c>, default on).
    /// Off: flyers stay browsable, and the listener answers no request that touches Revit.
    /// </summary>
    public bool ShowInRevit { get; set; } = true;

    /// <summary>Per-tool area overrides (<c>village.toolAreas</c>).</summary>
    public Dictionary<string, string> ToolAreas { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-tool activity overrides (<c>village.toolActivities</c>).</summary>
    public Dictionary<string, string> ToolActivities { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Folder of optional glTF models (<c>village.modelsFolder</c>). Null means "look next to the
    /// add-in", which is where the deployed package puts them. Anything without a model falls back
    /// to the drawn sprite, so this is always optional.
    /// </summary>
    public string? ModelsFolder { get; set; }

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
        o.OverlookAfterSeconds = Int(user, company, "overlookAfterSeconds", o.OverlookAfterSeconds, 2, 600);
        o.ParkAfterSeconds = Int(user, company, "parkAfterSeconds", o.ParkAfterSeconds, 15, 3600);
        o.AnimationSpeed = Dbl(user, company, "animationSpeed", o.AnimationSpeed, 0.1, 5.0);
        o.MaxViewers = Int(user, company, "maxViewers", o.MaxViewers, 1, 32);
        o.MaxBuildings = Int(user, company, "maxBuildings", o.MaxBuildings, 8, 16);
        o.MaxWarehouses = Int(user, company, "maxWarehouses", o.MaxWarehouses, 0, 20);
        o.MaxEffects = Int(user, company, "maxEffects", o.MaxEffects, 1, 24);
        o.ReconnectBackoffMs = Int(user, company, "reconnectBackoffMs", o.ReconnectBackoffMs, 250, 60000);
        o.ReconnectBackoffMaxMs = Int(user, company, "reconnectBackoffMaxMs", o.ReconnectBackoffMaxMs, o.ReconnectBackoffMs, 300000);
        o.DiagnosticLogging = Bool(user, company, "diagnosticLogging", o.DiagnosticLogging);
        o.FlyersEnabled = Bool(user, company, "flyersEnabled", o.FlyersEnabled);
        o.FlyerMaxItems = Int(user, company, "flyerMaxItems", o.FlyerMaxItems, 50, 10000);
        o.MaxFlyers = Int(user, company, "maxFlyers", o.MaxFlyers, 5, 200);
        o.FlyerArchiveDays = Int(user, company, "flyerArchiveDays", o.FlyerArchiveDays, 1, 365);
        o.ShowInRevit = Bool(user, company, "showInRevit", o.ShowInRevit);

        CopyMap(company, "toolAreas", o.ToolAreas);
        CopyMap(user, "toolAreas", o.ToolAreas);
        CopyMap(company, "toolActivities", o.ToolActivities);
        CopyMap(user, "toolActivities", o.ToolActivities);

        var models = Str(user, company, "modelsFolder", string.Empty);
        o.ModelsFolder = string.IsNullOrWhiteSpace(models) ? null : models.Trim();

        var exclude = Strings(user, company, "warehouseExcludeCategories");
        if (exclude != null) o.WarehouseExcludeCategories = exclude;
        o.WarehouseModelsOnly = Bool(user, company, "warehouseModelsOnly", o.WarehouseModelsOnly);

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
