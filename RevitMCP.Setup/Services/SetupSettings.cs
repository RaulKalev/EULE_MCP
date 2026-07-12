using System.IO;
using Newtonsoft.Json;

namespace RevitMCP.Setup.Services;

/// <summary>Remembers the chosen package folder between runs (%AppData%\EULE-MCP).</summary>
public class SetupSettings
{
    public string? PackageRoot { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EULE-MCP", "setup-settings.json");

    public static SetupSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonConvert.DeserializeObject<SetupSettings>(File.ReadAllText(FilePath)) ?? new SetupSettings();
        }
        catch { /* corrupt settings — start fresh */ }
        return new SetupSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
        catch { /* best effort */ }
    }
}
