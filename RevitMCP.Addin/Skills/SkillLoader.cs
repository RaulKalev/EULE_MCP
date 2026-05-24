using System.IO;
using Newtonsoft.Json;
using RevitMCP.Addin.Skills.Models;

namespace RevitMCP.Addin.Skills;

/// <summary>
/// Loads <see cref="SkillDefinition"/> objects from the company skills folder
/// (configured via SkillConfig.json, defaulting to %ProgramData%\RKTools\MCP\Skills\)
/// and maintains a local user-scoped cache at %AppData%\RKTools\RevitMCP\SkillCache\.
/// </summary>
public class SkillLoader
{
    private static readonly string CompanySkillsFallback =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "RKTools", "MCP", "Skills");

    private static readonly string LocalCachePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RKTools", "RevitMCP", "SkillCache");

    private static readonly string SkillConfigPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "RKTools", "MCP", "Config", "SkillConfig.json");

    public List<SkillDefinition> LoadAll()
    {
        var companyPath = ResolveCompanyPath();
        var skills = new List<SkillDefinition>();

        // Try company path first
        if (Directory.Exists(companyPath))
        {
            foreach (var file in Directory.GetFiles(companyPath, "*.skill.json"))
            {
                var skill = TryLoad(file);
                if (skill is not null)
                {
                    skill.SourcePath = file;
                    skills.Add(skill);
                    CacheSkill(skill, file);
                }
            }
        }
        else
        {
            // Fall back to local cache
            if (Directory.Exists(LocalCachePath))
            {
                foreach (var file in Directory.GetFiles(LocalCachePath, "*.skill.json"))
                {
                    var skill = TryLoad(file);
                    if (skill is not null)
                    {
                        skill.SourcePath = file;
                        skills.Add(skill);
                    }
                }
            }
        }

        if (skills.Count == 0)
            EnsureDefaultSkill(companyPath, skills);
        else
            UpdateStaleBuiltins(skills);

        return skills;
    }

    /// <summary>
    /// If a loaded skill matches a built-in default by ID and its version is older,
    /// replace the file on disk with the new default so stale cached configs don't persist.
    /// </summary>
    private static void UpdateStaleBuiltins(List<SkillDefinition> skills)
    {
        foreach (var (_, builtinSkill) in GetAllBuiltinSkills())
        {
            for (int i = 0; i < skills.Count; i++)
            {
                var loaded = skills[i];
                if (loaded.Id != builtinSkill.Id || loaded.SourcePath is null) continue;
                if (!IsOlderVersion(loaded.Version, builtinSkill.Version)) continue;

                builtinSkill.SourcePath = loaded.SourcePath;
                try
                {
                    File.WriteAllText(loaded.SourcePath, JsonConvert.SerializeObject(builtinSkill, Formatting.Indented));
                    skills[i] = builtinSkill;
                }
                catch { /* write failed — use loaded version, non-fatal */ }
            }
        }
    }

    private static List<(string FileName, SkillDefinition Skill)> GetAllBuiltinSkills() =>
    [
        ("LehtedeNimetamiseKontroll.skill.json",  BuildDefaultLehtedeNimetamiseKontrollSkill()),
        ("DeliveryCheck.skill.json",              BuildDefaultDeliveryCheckSkill()),
        ("ParameterQA.skill.json",                BuildDefaultParameterQASkill()),
        ("CoordinationQA.skill.json",             BuildDefaultCoordinationQASkill()),
        ("PreDelivery.skill.json",                BuildDefaultPreDeliverySkill()),
    ];

    private static bool IsOlderVersion(string? loaded, string builtin)
    {
        if (string.IsNullOrEmpty(loaded)) return true;
        return System.Version.TryParse(loaded, out var lv)
            && System.Version.TryParse(builtin, out var bv)
            && lv < bv;
    }

    private void CacheSkill(SkillDefinition skill, string sourceFile)
    {
        try
        {
            Directory.CreateDirectory(LocalCachePath);
            var dest = Path.Combine(LocalCachePath, Path.GetFileName(sourceFile));
            File.Copy(sourceFile, dest, overwrite: true);
        }
        catch { /* non-fatal */ }
    }

    private void EnsureDefaultSkill(string companyPath, List<SkillDefinition> skills)
    {
        // Write to company path if writable, otherwise to cache
        string targetDir;
        try
        {
            Directory.CreateDirectory(companyPath);
            targetDir = companyPath;
        }
        catch
        {
            Directory.CreateDirectory(LocalCachePath);
            targetDir = LocalCachePath;
        }

        foreach (var (fileName, skill) in GetAllBuiltinSkills())
        {
            var targetFile = Path.Combine(targetDir, fileName);
            try
            {
                var json = JsonConvert.SerializeObject(skill, Formatting.Indented);
                File.WriteAllText(targetFile, json);
            }
            catch { /* non-fatal if file is locked */ }

            skill.SourcePath = targetFile;
            skills.Add(skill);
        }
    }

    private string ResolveCompanyPath()
    {
        try
        {
            if (File.Exists(SkillConfigPath))
            {
                var cfg = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(SkillConfigPath));
                if (cfg is not null && cfg.TryGetValue("companySkillsPath", out var path) && !string.IsNullOrWhiteSpace(path))
                    return path;
            }
        }
        catch { /* use fallback */ }

        return CompanySkillsFallback;
    }

    private static SkillDefinition? TryLoad(string filePath)
    {
        try
        {
            var json = File.ReadAllText(filePath);
            return JsonConvert.DeserializeObject<SkillDefinition>(json);
        }
        catch { return null; }
    }

    private static SkillDefinition BuildDefaultLehtedeNimetamiseKontrollSkill() => new()
    {
        Id          = "company.lehed.nimetamise-kontroll",
        Name        = "Lehtede Nimetamise Kontroll",
        Description = "Kontrollib Revit lehtede ja jooniste nimetamise vastavust ettevõtte reeglitele.",
        Version     = "1.2.0",
        Author      = "EULE / RK Tools",
        IsCompanyMaster = true,
        DefaultSettings = new()
        {
            StopOnCriticalFailure = false,
            AllowProjectOverride  = true,
            RequiresUserConfirmationBeforeModelChanges = false,
        },
        Tasks = new()
        {
            new()
            {
                Id = "collect.revit.sheets", Enabled = true,
                Settings = new()
                {
                    ["includePlaceholders"] = false,
                    ["nimetusParamName"]     = "Nimetus",
                    ["markusParamName"]      = "Märkus",
                    ["extraParamNames"]      = new[]
                    {
                        "Peaprojekteerija",
                        "Peaprojekteerija aadress",
                        "Peaprojekteerija e-post",
                        "Peaprojekteerija reg.nr",
                        "Peaprojekteerija telefon",
                        "Projekteerija",
                        "Projekteerija aadress",
                        "Projekteerija e-post",
                        "Projekteerija reg.nr",
                        "Projekteerija telefon",
                    },
                }
            },
            new()
            {
                Id = "read.excel.document-register", Enabled = false,
                Settings = new()
                {
                    ["excelFilePath"] = "",
                    ["documentNumberColumnAliases"]  = new[] { "Dokumendi nr", "Joonise nr", "Lehe number", "Number", "Nr" },
                    ["documentNameColumnAliases"]    = new[] { "Nimetus", "Joonise nimetus", "Lehe nimi", "Nimi" },
                    ["disciplineColumnAliases"]      = new[] { "Eriala", "Osa" },
                    ["stageColumnAliases"]           = new[] { "Staadium", "Etapp" },
                }
            },
            new()
            {
                Id = "validate.sheet-numbers", Enabled = true,
                Settings = new()
                {
                    ["sheetNumberPatterns"] = new[]
                    {
                        "{ProjectNumber}_{Stage}_{Discipline}-{Group}-{Sequence}",
                        "{ProjectNumber}_{Stage}_{Discipline}-{Group}-{Sequence}_{Revision}",
                        "{ProjectNumber}_{Stage}_{Discipline}-{Group}-{Sequence}_{Date}",
                    },
                    ["sequenceNumberMinLength"] = 2,
                    ["sequenceNumberMaxLength"] = 3,
                    ["requireProjectNumber"]    = true,
                }
            },
            new()
            {
                Id = "validate.sheet-names", Enabled = true,
                Settings = new()
                {
                    ["forbiddenCharacters"] = new[] { "\\", "/", ":", "*", "?", "\"", "<", ">", "|" },
                    ["detectDoubleSpaces"]  = false,
                    ["trimWhitespace"]      = false,
                    ["minNameLength"]       = 3,
                    ["checkNimetusSpaces"]  = true,
                    ["checkMarkusSpaces"]   = true,
                }
            },
            new()
            {
                Id = "validate.sheet-parameters", Enabled = true,
                Settings = new()
                {
                    ["requiredParamNames"] = new[]
                    {
                        "Projekteerija",
                        "Projekteerija aadress",
                        "Projekteerija e-post",
                        "Projekteerija reg.nr",
                        "Projekteerija telefon",
                    },
                    ["requiredParamValues"] = new[]
                    {
                        "EULE OÜ",
                        "Mäealuse 2/4, 12618 Tallinn",
                        "eule@eule.ee",
                        "10785189",
                        "+372 53 007 383",
                    },
                    ["consistentParamNames"] = new[]
                    {
                        "Peaprojekteerija",
                        "Peaprojekteerija aadress",
                        "Peaprojekteerija e-post",
                        "Peaprojekteerija reg.nr",
                        "Peaprojekteerija telefon",
                    },
                }
            },
            new()
            {
                Id = "validate.disciplines", Enabled = true,
                Settings = new() { ["allowedDisciplines"] = new[] { "EL", "EN", "EA" } }
            },
            new()
            {
                Id = "validate.stages", Enabled = true,
                Settings = new() { ["allowedStages"] = new[] { "EP", "PP", "TP", "TJ" } }
            },
            new()
            {
                Id = "compare.excel-register", Enabled = false,
                Settings = new()
            },
            new()
            {
                Id = "export.excel-report", Enabled = false,
                Settings = new() { ["fileNamePattern"] = "Lehtede_Nimetamise_Kontroll_{ProjectNumber}.xlsx" }
            },
            new()
            {
                Id = "export.json-report", Enabled = false,
                Settings = new() { ["fileNamePattern"] = "Lehtede_Nimetamise_Kontroll_{ProjectNumber}.json" }
            },
        }
    };

    private static SkillDefinition BuildDefaultDeliveryCheckSkill() => new()
    {
        Id          = "company.delivery.check",
        Name        = "Delivery Check",
        Description = "Scans a delivery folder and compares exported files against Revit sheets. Reports missing, orphaned or duplicate deliverables.",
        Version     = "1.0.0",
        Author      = "EULE / RK Tools",
        IsCompanyMaster = true,
        DefaultSettings = new()
        {
            StopOnCriticalFailure = true,
            AllowProjectOverride  = true,
            RequiresUserConfirmationBeforeModelChanges = false,
        },
        Tasks =
        [
            new() { Id = "delivery.scan.folder",            Enabled = true,  Settings = new() { ["folderPath"] = "", ["recursive"] = true, ["includeExtensions"] = "pdf,dwg" } },
            new() { Id = "delivery.compare.revit-sheets",   Enabled = true,  Settings = new() { ["requiredExtensions"] = "pdf,dwg" } },
            new() { Id = "common.export.excel-report",      Enabled = true,  Settings = new() { ["reportTitle"] = "Delivery Check Report" } },
            new() { Id = "common.export.html-dashboard",    Enabled = true,  Settings = new() { ["reportTitle"] = "Delivery Check Dashboard" } },
        ]
    };

    private static SkillDefinition BuildDefaultParameterQASkill() => new()
    {
        Id          = "company.parameter.qa",
        Name        = "Parameter QA",
        Description = "Runs a parameter QA rule set against the active model, checks completeness of required parameters and exports an issue report.",
        Version     = "1.0.0",
        Author      = "EULE / RK Tools",
        IsCompanyMaster = true,
        DefaultSettings = new()
        {
            StopOnCriticalFailure = false,
            AllowProjectOverride  = true,
            RequiresUserConfirmationBeforeModelChanges = false,
        },
        Tasks =
        [
            new() { Id = "parameterqa.run.rule-set",      Enabled = true,  Settings = new() { ["ruleSetName"] = "", ["limitPerRule"] = 5000 } },
            new() { Id = "common.export.excel-report",    Enabled = true,  Settings = new() { ["reportTitle"] = "Parameter QA Report" } },
            new() { Id = "common.export.html-dashboard",  Enabled = true,  Settings = new() { ["reportTitle"] = "Parameter QA Dashboard" } },
        ]
    };

    private static SkillDefinition BuildDefaultCoordinationQASkill() => new()
    {
        Id          = "company.coordination.qa",
        Name        = "Coordination QA",
        Description = "Runs a clash detection preset and exports a filterable HTML dashboard with detected clashes.",
        Version     = "1.0.0",
        Author      = "EULE / RK Tools",
        IsCompanyMaster = true,
        DefaultSettings = new()
        {
            StopOnCriticalFailure = false,
            AllowProjectOverride  = true,
            RequiresUserConfirmationBeforeModelChanges = false,
        },
        Tasks =
        [
            new() { Id = "coordination.run.clash-preset",  Enabled = true,  Settings = new() { ["presetName"] = "", ["limit"] = 1000 } },
            new() { Id = "common.export.html-dashboard",   Enabled = true,  Settings = new() { ["reportTitle"] = "Coordination QA Dashboard" } },
            new() { Id = "common.export.excel-report",     Enabled = false, Settings = new() { ["reportTitle"] = "Coordination QA Report" } },
        ]
    };

    private static SkillDefinition BuildDefaultPreDeliverySkill() => new()
    {
        Id          = "company.project.pre-delivery",
        Name        = "Pre-Delivery Combined Check",
        Description = "Runs Delivery Check, Parameter QA and Coordination QA in sequence, merges all issues and exports a combined dashboard. Use before milestone deliveries.",
        Version     = "1.0.0",
        Author      = "EULE / RK Tools",
        IsCompanyMaster = true,
        DefaultSettings = new()
        {
            StopOnCriticalFailure = false,
            AllowProjectOverride  = true,
            RequiresUserConfirmationBeforeModelChanges = false,
        },
        Tasks =
        [
            new() { Id = "delivery.scan.folder",            Enabled = true,  Settings = new() { ["folderPath"] = "", ["recursive"] = true, ["includeExtensions"] = "pdf,dwg" } },
            new() { Id = "delivery.compare.revit-sheets",   Enabled = true,  Settings = new() { ["requiredExtensions"] = "pdf,dwg" } },
            new() { Id = "parameterqa.run.rule-set",        Enabled = false, Settings = new() { ["ruleSetName"] = "" } },
            new() { Id = "coordination.run.clash-preset",   Enabled = false, Settings = new() { ["presetName"] = "" } },
            new() { Id = "common.merge.issues",             Enabled = true,  Settings = new() },
            new() { Id = "common.export.excel-report",      Enabled = true,  Settings = new() { ["reportTitle"] = "Pre-Delivery Combined Report" } },
            new() { Id = "common.export.html-dashboard",    Enabled = true,  Settings = new() { ["reportTitle"] = "Pre-Delivery Dashboard" } },
        ]
    };
}
