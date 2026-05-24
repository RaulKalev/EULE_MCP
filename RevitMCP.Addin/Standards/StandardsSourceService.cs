using System.IO;
using Newtonsoft.Json;

namespace RevitMCP.Addin.Standards;

/// <summary>
/// Loads and saves the <see cref="StandardsSourceConfig"/> from
/// %ProgramData%\RKTools\MCP\Config\StandardsSources.json.
/// </summary>
public class StandardsSourceService
{
    private static readonly string ConfigPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "RKTools", "MCP", "Config", "StandardsSources.json");

    private static readonly JsonSerializerSettings _jsonSettings = new()
    {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Ignore
    };

    /// <summary>Returns the full path to the config file (may not exist yet).</summary>
    public string ConfigFilePath => ConfigPath;

    /// <summary>
    /// Loads the config. Returns an empty config (no sources) if the file does not exist.
    /// </summary>
    public StandardsSourceConfig Load()
    {
        if (!File.Exists(ConfigPath))
            return new StandardsSourceConfig();

        try
        {
            var json = File.ReadAllText(ConfigPath);
            return JsonConvert.DeserializeObject<StandardsSourceConfig>(json)
                   ?? new StandardsSourceConfig();
        }
        catch
        {
            return new StandardsSourceConfig();
        }
    }

    /// <summary>
    /// Returns only the enabled sources from the config.
    /// </summary>
    public List<StandardsSource> GetEnabledSources()
        => Load().Sources.Where(s => s.Enabled).ToList();

    /// <summary>
    /// Validates all sources in the config, checking that paths exist.
    /// Returns a list of (source, errorMessage?) tuples.
    /// </summary>
    public List<(StandardsSource Source, string? Error)> Validate()
    {
        var config = Load();
        var results = new List<(StandardsSource, string?)>();

        foreach (var src in config.Sources)
        {
            if (string.IsNullOrWhiteSpace(src.Id))
            {
                results.Add((src, "Source has no ID."));
                continue;
            }
            if (string.IsNullOrWhiteSpace(src.Path))
            {
                results.Add((src, "Source has no path."));
                continue;
            }
            if (!Directory.Exists(src.Path) && !File.Exists(src.Path))
                results.Add((src, $"Path does not exist: {src.Path}"));
            else
                results.Add((src, null));
        }
        return results;
    }

    /// <summary>
    /// Saves an example config file at the config path (only if the file does not already exist).
    /// </summary>
    public string SaveExample()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ConfigPath)!);

        if (File.Exists(ConfigPath))
            return ConfigPath;

        var example = new StandardsSourceConfig
        {
            Sources =
            [
                new StandardsSource
                {
                    Id          = "company.standards",
                    Name        = "Company Standards",
                    Description = "BIM and design standards documentation",
                    Path        = @"C:\CompanyDocs\Standards",
                    FilePatterns = ["*.md", "*.txt"],
                    Enabled     = true
                }
            ]
        };

        File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(example, _jsonSettings));
        return ConfigPath;
    }
}
