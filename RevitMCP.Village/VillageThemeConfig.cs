using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RevitMCP.Village;

/// <summary>Evidence rules for one system: Revit category names and family/type name keywords.</summary>
public sealed class VillageThemeSystem
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public double Weight { get; set; } = 1.0;

    /// <summary>Exact Revit category names (case-insensitive). Localised names can be added in the config.</summary>
    public List<string> Categories { get; set; } = new();

    /// <summary>Keywords matched against "family: type" names. Four letters or fewer must match a whole token.</summary>
    public List<string> Keywords { get; set; } = new();

    /// <summary>Viewer colours: base and accent.</summary>
    public string Primary { get; set; } = "#7a8b99";
    public string Accent { get; set; } = "#c9d3dc";
}

/// <summary>
/// Configurable system-theme mapping (<c>village.themes</c> in the user/company config).
/// Everything is deterministic data; no AI, no model access. See docs/project-village.md.
/// </summary>
public sealed class VillageThemeConfig
{
    public const string FireAlarm  = VillageAreas.FireAlarm;
    public const string Security   = VillageAreas.Security;
    public const string Lighting   = VillageAreas.Lighting;
    public const string ItAv       = VillageAreas.ItAv;
    public const string Electrical = VillageAreas.Electrical;
    public const string Mixed      = "mixed";
    public const string Neutral    = "neutral";

    /// <summary>Below this many classified elements the project stays neutral.</summary>
    public int MinSampleCount { get; set; } = 20;

    /// <summary>A system needs at least this many points to be visible, and this much category evidence to dominate.</summary>
    public int MinSystemCount { get; set; } = 10;

    /// <summary>Share of the total score at which the top system becomes the dominant theme.</summary>
    public double DominantShare { get; set; } = 0.45;

    /// <summary>Share at which a system's district becomes visible.</summary>
    public double VisibleShare { get; set; } = 0.10;

    public double CategoryWeight { get; set; } = 1.0;

    /// <summary>Name evidence is worth less than category evidence and is capped (see <see cref="VillageThemeScorer"/>).</summary>
    public double NameWeight { get; set; } = 0.5;

    /// <summary>Where the rules came from: "default" or "config".</summary>
    public string Source { get; set; } = "default";

    public List<VillageThemeSystem> Systems { get; set; } = new();

    public static VillageThemeConfig Default() => new()
    {
        Systems =
        {
            new VillageThemeSystem
            {
                Id = FireAlarm, Label = "Fire alarm", Primary = "#b8432f", Accent = "#ff8a75",
                Categories = { "Fire Alarm Devices" },
                Keywords =
                {
                    "ATS", "fire alarm", "fire_alarm", "tulekahju", "suitsuandur", "smoke detector", "heat detector",
                    "sireen", "siren", "beacon", "manual call", "aspirat", "flame", "leegiandur", "häirenupp", "sounder"
                }
            },
            new VillageThemeSystem
            {
                Id = Security, Label = "Security / access control", Primary = "#5d6d7e", Accent = "#aab7b8",
                Categories = { "Security Devices" },
                Keywords =
                {
                    "LPS", "VVS", "valve", "läbipääs", "kaardilugeja", "card reader", "access control", "kaamera",
                    "camera", "CCTV", "intrusion", "magnet", "PIR", "glass break", "klaasipurunemis", "paanika", "panic",
                    "liikumisandur", "videofon", "intercom"
                }
            },
            new VillageThemeSystem
            {
                Id = Lighting, Label = "Lighting / DALI", Primary = "#c99a1e", Accent = "#ffe08a",
                Categories = { "Lighting Fixtures", "Lighting Devices" },
                Keywords =
                {
                    "DALI", "valgusti", "luminaire", "lamp", "light", "hädavalgust", "emergency light", "lüliti",
                    "switch", "downlight", "spot", "led"
                }
            },
            new VillageThemeSystem
            {
                Id = ItAv, Label = "IT / AV / data", Primary = "#2e86c1", Accent = "#7fc8ff",
                Categories = { "Data Devices", "Communication Devices", "Telephone Devices", "Nurse Call Devices" },
                Keywords =
                {
                    "RJ45", "data", "side", "network", "võrk", "patch", "rack", "antenn", "wifi", "wlan", "AV", "kõlar",
                    "speaker", "ekraan", "display", "mikrofon", "microphone", "projector", "HDMI", "keystone", "optic"
                }
            },
            new VillageThemeSystem
            {
                Id = Electrical, Label = "General electrical", Primary = "#8e6b3e", Accent = "#e0b070",
                Categories =
                {
                    "Electrical Fixtures", "Electrical Equipment", "Electrical Circuits", "Cable Trays",
                    "Cable Tray Fittings", "Conduits", "Conduit Fittings", "Wires"
                },
                Keywords =
                {
                    "pistikupesa", "socket", "kilp", "panel", "jaotus", "distribution", "generaator", "generator", "UPS",
                    "trafo", "transformer", "kaablitee", "cable tray", "maandus", "earthing", "piksekaitse", "lightning",
                    "põrandakarp", "floor box"
                }
            }
        }
    };

    /// <summary>
    /// Applies a <c>village.themes</c> object on top of the defaults. Thresholds and weights are
    /// clamped; a system listed in the config replaces its default lists; unknown systems and
    /// malformed values are ignored. Returns the defaults when <paramref name="json"/> is null or invalid.
    /// </summary>
    public static VillageThemeConfig FromJson(string? json)
    {
        var config = Default();
        if (string.IsNullOrWhiteSpace(json)) return config;

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json!, null, new JsonDocumentOptions { MaxDepth = 16 }) as JsonObject;
        }
        catch (JsonException)
        {
            return config;
        }
        if (root == null) return config;

        config.Source = "config";
        config.MinSampleCount = Int(root, "minSampleCount", config.MinSampleCount, 1, 100000);
        config.MinSystemCount = Int(root, "minSystemCount", config.MinSystemCount, 1, 100000);
        config.DominantShare = Dbl(root, "dominantShare", config.DominantShare, 0.2, 1.0);
        config.VisibleShare = Dbl(root, "visibleShare", config.VisibleShare, 0.01, 1.0);
        config.CategoryWeight = Dbl(root, "categoryWeight", config.CategoryWeight, 0.0, 10.0);
        config.NameWeight = Dbl(root, "nameWeight", config.NameWeight, 0.0, 10.0);

        if (root["systems"] is JsonObject systems)
        {
            foreach (var kv in systems)
            {
                var system = config.Systems.FirstOrDefault(s => string.Equals(s.Id, kv.Key, StringComparison.OrdinalIgnoreCase));
                if (system == null || kv.Value is not JsonObject body) continue;

                system.Weight = Dbl(body, "weight", system.Weight, 0.0, 10.0);
                if (body["label"] is JsonValue label && label.TryGetValue<string>(out var l) && !string.IsNullOrWhiteSpace(l))
                    system.Label = l.Trim();
                if (body["primary"] is JsonValue primary && primary.TryGetValue<string>(out var p) && IsColor(p))
                    system.Primary = p.Trim();
                if (body["accent"] is JsonValue accent && accent.TryGetValue<string>(out var a) && IsColor(a))
                    system.Accent = a.Trim();
                if (body["categories"] is JsonArray categories)
                    system.Categories = Strings(categories);
                if (body["keywords"] is JsonArray keywords)
                    system.Keywords = Strings(keywords);
            }
        }

        return config;
    }

    public VillageThemeSystem? FindSystem(string id) =>
        Systems.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    private static bool IsColor(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value!.Trim().Length is 7 or 4 && value.Trim()[0] == '#' &&
        value.Trim().Substring(1).All(c => Uri.IsHexDigit(c));

    private static List<string> Strings(JsonArray array)
    {
        var list = new List<string>();
        foreach (var item in array)
        {
            if (item is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                list.Add(s.Trim());
            if (list.Count >= 200) break;
        }
        return list;
    }

    private static int Int(JsonObject obj, string key, int fallback, int min, int max)
    {
        if (obj[key] is not JsonValue value) return fallback;
        int result;
        if (value.TryGetValue<int>(out var i)) result = i;
        else if (value.TryGetValue<double>(out var d) && !double.IsNaN(d)) result = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, d));
        else if (value.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)) result = p;
        else return fallback;
        return result < min ? min : result > max ? max : result;
    }

    private static double Dbl(JsonObject obj, string key, double fallback, double min, double max)
    {
        if (obj[key] is not JsonValue value) return fallback;
        double result;
        if (value.TryGetValue<double>(out var d)) result = d;
        else if (value.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) result = p;
        else return fallback;
        if (double.IsNaN(result) || double.IsInfinity(result)) return fallback;
        return result < min ? min : result > max ? max : result;
    }
}
