using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevitMCP.Addin.Families;

/// <summary>
/// Loads DWG detail-item presets from
/// %ProgramData%\RKTools\MCP\Config\DwgDetailItemPresets.json.
/// Creates a default config file on first use if the file does not exist.
/// </summary>
public static class DwgDetailItemPresetConfig
{
    private static readonly string _configPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "RKTools", "MCP", "Config", "DwgDetailItemPresets.json");

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    public static string ConfigPath => _configPath;

    /// <summary>
    /// Returns the named preset, or an error string if it cannot be found.
    /// Creates a default config file if none exists.
    /// </summary>
    public static (DwgDetailItemPreset? preset, string? error) GetPreset(string presetName)
    {
        EnsureDefaultConfig();

        if (!File.Exists(_configPath))
            return (null, $"Preset config file could not be created at: {_configPath}");

        try
        {
            var json = File.ReadAllText(_configPath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, DwgDetailItemPreset>>(json, _jsonOpts);
            if (dict == null)
                return (null, "Preset config file could not be parsed.");

            if (!dict.TryGetValue(presetName, out var preset))
                return (null,
                    $"Preset '{presetName}' not found in config. Available: {string.Join(", ", dict.Keys)}. " +
                    $"Config file: {_configPath}");

            return (preset, null);
        }
        catch (Exception ex)
        {
            return (null, $"Failed to read preset config: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates the default config file if it does not already exist.
    /// The default preset points to placeholder paths the user must update.
    /// </summary>
    public static void EnsureDefaultConfig()
    {
        if (File.Exists(_configPath)) return;

        try
        {
            var dir = Path.GetDirectoryName(_configPath)!;
            Directory.CreateDirectory(dir);

            var presetsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "RKTools", "MCP", "Presets");
            var outputDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "RKTools", "MCP", "GeneratedFamilies", "PanelSchematics");

            var defaults = new Dictionary<string, DwgDetailItemPreset>
            {
                ["DefaultPanelSchematicSymbol"] = new DwgDetailItemPreset
                {
                    PresetFamilyPath = Path.Combine(presetsDir, "PanelSchematicSymbolPreset.rfa"),
                    OutputFolder     = outputDir,
                    FamilyPrefix     = "Kilp_",
                    ImportUnit       = "Millimeter",
                    DefaultViewName  = null,
                    DeletePlaceholderGeometry = false
                }
            };

            File.WriteAllText(_configPath,
                JsonSerializer.Serialize(defaults, _jsonOpts),
                System.Text.Encoding.UTF8);
        }
        catch { /* non-fatal: tool will report missing file at execution time */ }
    }
}
