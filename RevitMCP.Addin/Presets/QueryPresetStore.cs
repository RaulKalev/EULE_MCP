using System.IO;
using Newtonsoft.Json;
using RevitMCP.Addin.Query;

namespace RevitMCP.Addin.Presets;

public class QueryPresetStore
{
    private static readonly string _configDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RKTools", "RevitMCP");

    private static readonly string _configPath = Path.Combine(_configDir, "query-presets.json");

    public QueryPresetFile Load()
    {
        EnsureDefaultFile();

        try
        {
            var json = File.ReadAllText(_configPath);
            return JsonConvert.DeserializeObject<QueryPresetFile>(json) ?? new QueryPresetFile();
        }
        catch
        {
            return new QueryPresetFile();
        }
    }

    public QueryPreset? FindByName(string name)
    {
        var file = Load();
        return file.Presets.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public List<string> GetPresetNames()
    {
        return Load().Presets.Select(p => p.Name).ToList();
    }

    private void EnsureDefaultFile()
    {
        if (File.Exists(_configPath)) return;

        Directory.CreateDirectory(_configDir);

        var defaultFile = new QueryPresetFile
        {
            Version = 1,
            Presets = new List<QueryPreset>
            {
                new()
                {
                    Name = "Fire Alarm Device Report",
                    Description = "Groups fire alarm devices by name and manufacturer.",
                    Category = "Fire Alarm Devices",
                    Filters = new(),
                    Parameters = new() { "ELENEA_Nimetus", "ELENEA_Tähis", "ELENEA_Tootja", "ELENEA_Mudel" },
                    GroupBy = new()
                    {
                        new GroupKeyOptions
                        {
                            Type = "Parameter",
                            ParameterName = "ELENEA_Nimetus",
                            ParameterMatchMode = "ContainsNormalized",
                            Scope = "InstanceAndType"
                        },
                        new GroupKeyOptions
                        {
                            Type = "Parameter",
                            ParameterName = "ELENEA_Tootja",
                            ParameterMatchMode = "ContainsNormalized",
                            Scope = "InstanceAndType"
                        }
                    },
                    DefaultOutputMode = "Both"
                }
            }
        };

        var json = JsonConvert.SerializeObject(defaultFile, Formatting.Indented);
        File.WriteAllText(_configPath, json);
    }
}
