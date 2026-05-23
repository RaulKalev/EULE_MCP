using System.IO;
using Newtonsoft.Json;
using RevitMCP.Addin.Skills.Models;

namespace RevitMCP.Addin.Skills;

/// <summary>
/// Loads, saves and deletes project-level <see cref="SkillOverride"/> files.
/// Path pattern: %AppData%\RKTools\RevitMCP\ProjectSkillOverrides\{projectId}\{skillId}.override.skill.json
/// </summary>
public class SkillOverrideService
{
    private static readonly string BasePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RKTools", "RevitMCP", "ProjectSkillOverrides");

    public SkillOverride? Load(string skillId, string projectId)
    {
        var path = GetPath(skillId, projectId);
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<SkillOverride>(json);
        }
        catch { return null; }
    }

    public void Save(SkillOverride skillOverride)
    {
        var path = GetPath(skillOverride.BaseSkillId, skillOverride.ProjectId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonConvert.SerializeObject(skillOverride, Formatting.Indented);
        File.WriteAllText(path, json);
    }

    public void Delete(string skillId, string projectId)
    {
        var path = GetPath(skillId, projectId);
        if (File.Exists(path)) File.Delete(path);
    }

    public bool Exists(string skillId, string projectId) => File.Exists(GetPath(skillId, projectId));

    private static string GetPath(string skillId, string projectId)
    {
        var safeProject = SanitiseName(projectId);
        var safeSkill = SanitiseName(skillId);
        return Path.Combine(BasePath, safeProject, $"{safeSkill}.override.skill.json");
    }

    private static string SanitiseName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
