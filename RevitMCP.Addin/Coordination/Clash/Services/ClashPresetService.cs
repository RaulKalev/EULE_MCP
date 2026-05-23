using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RevitMCP.Addin.Coordination.Clash.DTOs;

namespace RevitMCP.Addin.Coordination.Clash.Services;

/// <summary>
/// Manages clash detection presets stored at %AppData%\RKTools\RevitMCP\clash-presets.json.
/// Uses System.Text.Json — no Revit API dependency, safe for unit testing.
/// </summary>
public class ClashPresetService
{
    private static readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RKTools", "RevitMCP", "clash-presets.json");

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public void EnsureDefaultFile()
    {
        if (File.Exists(_filePath)) return;
        var file = new ClashPresetFile { Presets = GetDefaultPresets() };
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(file, _jsonOpts));
    }

    public ClashPresetFile Load()
    {
        EnsureDefaultFile();
        var json = File.ReadAllText(_filePath);
        return JsonSerializer.Deserialize<ClashPresetFile>(json, _jsonOpts) ?? new ClashPresetFile();
    }

    public ClashPresetDto? FindByName(string name)
    {
        var file = Load();
        return file.Presets.FirstOrDefault(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public (bool valid, List<string> errors) Validate(ClashPresetDto preset)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(preset.Name))
            errors.Add("Preset name is required.");
        if (preset.Rules == null || preset.Rules.Count == 0)
            errors.Add("Preset must contain at least one rule.");
        foreach (var rule in preset.Rules ?? [])
        {
            if (string.IsNullOrWhiteSpace(rule.Name))
                errors.Add("Each rule must have a name.");
            if (rule.SourceCategories == null || rule.SourceCategories.Count == 0)
                errors.Add($"Rule '{rule.Name}': SourceCategories is required.");
            if (rule.TargetCategories == null || rule.TargetCategories.Count == 0)
                errors.Add($"Rule '{rule.Name}': TargetCategories is required.");
            if (rule.ClashType != "HardClash" && rule.ClashType != "Clearance")
                errors.Add($"Rule '{rule.Name}': ClashType must be HardClash or Clearance.");
            if (rule.ClearanceMm < 0)
                errors.Add($"Rule '{rule.Name}': ClearanceMm cannot be negative.");
            if (rule.ToleranceMm < 0)
                errors.Add($"Rule '{rule.Name}': ToleranceMm cannot be negative.");
        }
        return (errors.Count == 0, errors);
    }

    /// <summary>Returns the built-in default presets. Pure logic — no file I/O.</summary>
    public static List<ClashPresetDto> GetDefaultPresets() =>
    [
        new ClashPresetDto
        {
            Name = "Electrical vs HVAC",
            Description = "Detects hard clashes and clearance violations between electrical and mechanical/HVAC elements.",
            Rules =
            [
                new ClashRuleDto
                {
                    Name = "Cable Trays vs Ducts — Hard Clash",
                    SourceCategories = ["Cable Trays"],
                    TargetCategories = ["Ducts"],
                    ClashType = "HardClash", ToleranceMm = 5, Severity = "High",
                    TargetScope = "HostAndLinks", IncludeGenericModels = true, IncludeImportedGeometry = true
                },
                new ClashRuleDto
                {
                    Name = "Cable Trays vs Ducts — Clearance 50mm",
                    SourceCategories = ["Cable Trays"],
                    TargetCategories = ["Ducts"],
                    ClashType = "Clearance", ClearanceMm = 50, Severity = "Medium",
                    TargetScope = "HostAndLinks", IncludeGenericModels = true, IncludeImportedGeometry = true
                },
                new ClashRuleDto
                {
                    Name = "Cable Trays vs Pipes — Clearance 50mm",
                    SourceCategories = ["Cable Trays"],
                    TargetCategories = ["Pipes"],
                    ClashType = "Clearance", ClearanceMm = 50, Severity = "Medium",
                    TargetScope = "HostAndLinks", IncludeGenericModels = true, IncludeImportedGeometry = true
                },
                new ClashRuleDto
                {
                    Name = "Conduits vs Ducts — Hard Clash",
                    SourceCategories = ["Conduits"],
                    TargetCategories = ["Ducts"],
                    ClashType = "HardClash", ToleranceMm = 5, Severity = "High",
                    TargetScope = "HostAndLinks", IncludeGenericModels = true, IncludeImportedGeometry = true
                },
                new ClashRuleDto
                {
                    Name = "Conduits vs Pipes — Hard Clash",
                    SourceCategories = ["Conduits"],
                    TargetCategories = ["Pipes"],
                    ClashType = "HardClash", ToleranceMm = 5, Severity = "High",
                    TargetScope = "HostAndLinks", IncludeGenericModels = true, IncludeImportedGeometry = true
                },
                new ClashRuleDto
                {
                    Name = "Conduits vs Pipes — Clearance 50mm",
                    SourceCategories = ["Conduits"],
                    TargetCategories = ["Pipes"],
                    ClashType = "Clearance", ClearanceMm = 50, Severity = "Medium",
                    TargetScope = "HostAndLinks", IncludeGenericModels = true, IncludeImportedGeometry = true
                }
            ]
        },
        new ClashPresetDto
        {
            Name = "Fire Alarm Devices Placement QA",
            Description = "Verifies that fire alarm devices do not clash with ceilings, walls, or structural elements.",
            Rules =
            [
                new ClashRuleDto
                {
                    Name = "Fire Alarm vs Ceilings — Hard Clash",
                    SourceCategories = ["Fire Alarm Devices"],
                    TargetCategories = ["Ceilings"],
                    ClashType = "HardClash", ToleranceMm = 2, Severity = "Critical",
                    TargetScope = "HostAndLinks", IncludeGenericModels = true, IncludeImportedGeometry = true
                },
                new ClashRuleDto
                {
                    Name = "Fire Alarm vs Walls — Hard Clash",
                    SourceCategories = ["Fire Alarm Devices"],
                    TargetCategories = ["Walls"],
                    ClashType = "HardClash", ToleranceMm = 2, Severity = "High",
                    TargetScope = "HostAndLinks", IncludeGenericModels = true, IncludeImportedGeometry = true
                }
            ]
        }
    ];
}
