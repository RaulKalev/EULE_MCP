using System.IO;
using System.Text.Json;

namespace RevitMCP.Addin.Export.Sheets;

/// <summary>
/// The EliteSheets plugin's settings (<c>C:\ProgramData\RK Tools\EliteSheets\config.json</c>), read so the connector
/// exports with the same DXF template, the same "convert merged drawings to DWG" choice and the same per-project
/// folders as the plugin. Read-only: the connector never writes the plugin's file. Parsing is pure.
/// </summary>
public sealed class EliteSheetsSettings
{
    public static readonly string DefaultConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RK Tools", "EliteSheets", "config.json");

    public string ConfigPath { get; init; } = string.Empty;
    public bool Found { get; init; }
    /// <summary>The DXF template merged drawings are placed into.</summary>
    public string TemplateDxfPath { get; init; } = string.Empty;
    public bool ConvertMergedToDwg { get; init; }
    /// <summary>Folder for both formats when SeparateExportFolders is off.</summary>
    public string ExportPath { get; init; } = string.Empty;
    public string PdfExportPath { get; init; } = string.Empty;
    public string DwgExportPath { get; init; } = string.Empty;
    public bool SeparateExportFolders { get; init; }
    public string? Error { get; init; }

    /// <summary>The PDF folder the plugin would use for this project, empty when none is saved.</summary>
    public string PdfFolder => SeparateExportFolders && PdfExportPath.Length > 0 ? PdfExportPath : ExportPath;

    /// <summary>The DWG/DXF folder the plugin would use for this project, empty when none is saved.</summary>
    public string CadFolder => SeparateExportFolders && DwgExportPath.Length > 0 ? DwgExportPath : ExportPath;

    /// <summary>Reads the plugin's config for the project file <paramref name="projectFileName"/> (e.g. "1626_EL.rvt").</summary>
    public static EliteSheetsSettings Load(string? projectFileName, string? configPath = null)
    {
        var path = configPath ?? DefaultConfigPath;
        try
        {
            if (!File.Exists(path)) return new EliteSheetsSettings { ConfigPath = path };
            return Parse(File.ReadAllText(path), projectFileName, path);
        }
        catch (Exception ex)
        {
            return new EliteSheetsSettings { ConfigPath = path, Error = ex.Message };
        }
    }

    /// <summary>Parses the plugin's JSON. Per-project sections are keyed by the project's file name.</summary>
    public static EliteSheetsSettings Parse(string json, string? projectFileName, string configPath = "")
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new EliteSheetsSettings { ConfigPath = configPath, Found = true, Error = "config.json is not a JSON object." };

            string Project(string section)
            {
                if (string.IsNullOrEmpty(projectFileName) || !root.TryGetProperty(section, out var s) || s.ValueKind != JsonValueKind.Object)
                    return string.Empty;
                foreach (var p in s.EnumerateObject())
                    if (string.Equals(p.Name, projectFileName, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                        return p.Value.GetString() ?? string.Empty;
                return string.Empty;
            }

            return new EliteSheetsSettings
            {
                ConfigPath = configPath,
                Found = true,
                TemplateDxfPath = root.TryGetProperty("TemplateDxfPath", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? string.Empty : string.Empty,
                ConvertMergedToDwg = root.TryGetProperty("ConvertMergedToDwg", out var c) && c.ValueKind == JsonValueKind.True,
                ExportPath = Project("ExportPaths"),
                PdfExportPath = Project("PdfExportPaths"),
                DwgExportPath = Project("DwgExportPaths"),
                SeparateExportFolders = string.Equals(Project("SeparateExportFolders"), "true", StringComparison.OrdinalIgnoreCase)
            };
        }
        catch (JsonException ex)
        {
            return new EliteSheetsSettings { ConfigPath = configPath, Found = true, Error = "config.json is not valid JSON: " + ex.Message };
        }
    }
}
