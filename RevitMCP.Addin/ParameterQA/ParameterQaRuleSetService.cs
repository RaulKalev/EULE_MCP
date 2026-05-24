using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevitMCP.Addin.ParameterQA;

/// <summary>
/// Manages the parameter-QA rule sets file at %AppData%\RKTools\RevitMCP\parameter-qa-rules.json.
/// Uses System.Text.Json — no Revit API dependency, safe for unit testing.
/// </summary>
public class ParameterQaRuleSetService
{
    private static readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RKTools", "RevitMCP", "parameter-qa-rules.json");

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    public void EnsureDefaultFile()
    {
        if (File.Exists(_filePath)) return;
        var file = new ParameterQaFile { RuleSets = GetDefaultRuleSets() };
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(file, _jsonOpts));
    }

    public ParameterQaFile Load()
    {
        EnsureDefaultFile();
        var json = File.ReadAllText(_filePath);
        return JsonSerializer.Deserialize<ParameterQaFile>(json, _jsonOpts) ?? new ParameterQaFile();
    }

    public ParameterQaRuleSet? FindByName(string name)
    {
        var file = Load();
        return file.RuleSets.FirstOrDefault(rs =>
            rs.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public List<ParameterQaRuleSet> GetAll()
        => Load().RuleSets;

    // ── Default built-in rule sets ─────────────────────────────────────────────

    public static List<ParameterQaRuleSet> GetDefaultRuleSets() =>
    [
        new ParameterQaRuleSet
        {
            Name = "ELENEA Basic QA",
            Description = "Minimum parameter completeness rules for ELENEA electrical and fire alarm projects.",
            Rules =
            [
                new ParameterQaRule
                {
                    Category = "Fire Alarm Devices",
                    RequiredParameters = ["EL.001_Nimetus", "EL.002_Tähis", "IfcGUID"],
                    IncludeInstanceParameters = true,
                    IncludeTypeParameters = true
                },
                new ParameterQaRule
                {
                    Category = "Lighting Fixtures",
                    RequiredParameters = ["ELENEA_Valgusti 900_Tootja", "ELENEA_Valgusti 905_Mudel", "id"],
                    IncludeInstanceParameters = true,
                    IncludeTypeParameters = true
                }
            ]
        }
    ];
}
