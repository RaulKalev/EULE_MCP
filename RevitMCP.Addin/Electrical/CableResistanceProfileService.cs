using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevitMCP.Addin.Electrical;

public sealed class CableResistanceProfile
{
    [JsonPropertyName("matchNameContains")]
    public string MatchNameContains { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("roundTripOhmPerMeter")]
    public double RoundTripOhmPerMeter { get; set; }
}

public sealed class CableResistanceProfileCollection
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("profiles")]
    public List<CableResistanceProfile> Profiles { get; set; } = [];
}

/// <summary>
/// Loads and queries cable resistance profiles from
/// %AppData%\RKTools\RevitMCP\electrical-cable-profiles.json.
/// Falls back to built-in defaults when the file is missing or invalid.
/// </summary>
public static class CableResistanceProfileService
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RKTools", "RevitMCP", "electrical-cable-profiles.json");

    private static readonly CableResistanceProfileCollection BuiltInDefaults = new()
    {
        Version = 1,
        Profiles =
        [
            new() { MatchNameContains = "2×0.8 CCA",  Description = "Approximate CCA fire alarm loop cable",                       RoundTripOhmPerMeter = 0.068 },
            new() { MatchNameContains = "2x0.8 CCA",  Description = "Approximate CCA fire alarm loop cable (alt notation)",        RoundTripOhmPerMeter = 0.068 },
            new() { MatchNameContains = "1×2×1.0",    Description = "Approximate copper fire resistant cable",                     RoundTripOhmPerMeter = 0.035 },
            new() { MatchNameContains = "1x2x1.0",    Description = "Approximate copper fire resistant cable (alt notation)",      RoundTripOhmPerMeter = 0.035 },
            new() { MatchNameContains = "FE180",       Description = "Fallback fire resistant cable profile",                       RoundTripOhmPerMeter = 0.035 },
            new() { MatchNameContains = "PH90",        Description = "Fallback fire resistant cable profile (PH90)",                RoundTripOhmPerMeter = 0.035 },
            new() { MatchNameContains = "E90",         Description = "Fallback fire resistant cable profile (E90)",                 RoundTripOhmPerMeter = 0.035 },
        ]
    };

    /// <summary>Loads the profile collection (file → defaults on error).</summary>
    public static CableResistanceProfileCollection Load()
    {
        if (File.Exists(ConfigPath))
        {
            try
            {
                var json = File.ReadAllText(ConfigPath);
                var loaded = JsonSerializer.Deserialize<CableResistanceProfileCollection>(json);
                if (loaded?.Profiles?.Count > 0)
                    return loaded;
            }
            catch { }
        }
        return BuiltInDefaults;
    }

    /// <summary>
    /// Creates the default config file if it does not already exist.
    /// Call once during addin startup or on first profile query.
    /// </summary>
    public static void EnsureDefaultFileExists()
    {
        if (File.Exists(ConfigPath)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            var json = JsonSerializer.Serialize(BuiltInDefaults, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch { }
    }

    /// <summary>
    /// Returns the first profile whose MatchNameContains is found (case-insensitive) in cableTypeName,
    /// or null when no profile matches.
    /// </summary>
    public static CableResistanceProfile? FindMatch(string cableTypeName, CableResistanceProfileCollection? collection = null)
    {
        if (string.IsNullOrWhiteSpace(cableTypeName)) return null;
        collection ??= Load();
        return collection.Profiles
            .FirstOrDefault(p => !string.IsNullOrEmpty(p.MatchNameContains)
                                  && cableTypeName.Contains(p.MatchNameContains, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Config file path (informational).</summary>
    public static string ConfigFilePath => ConfigPath;
}
