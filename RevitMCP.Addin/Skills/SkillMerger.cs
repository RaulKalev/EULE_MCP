using RevitMCP.Addin.Skills.Models;

namespace RevitMCP.Addin.Skills;

/// <summary>
/// Merges a company-master <see cref="SkillDefinition"/> with an optional project
/// <see cref="SkillOverride"/> to produce the effective merged skill for a run.
/// Rules:
///  1. If a task override has Enabled set, it replaces the master value.
///  2. Each setting key in the override replaces the corresponding master setting.
///  3. Tasks present in the override but not in the master are ignored (logged as warning).
///  4. Master tasks not present in the override are used as-is.
/// </summary>
public class SkillMerger
{
    public (SkillDefinition Merged, List<string> Warnings) Merge(SkillDefinition master, SkillOverride? projectOverride)
    {
        if (projectOverride is null)
            return (DeepCopy(master), new List<string>());

        var warnings = new List<string>();
        var merged = DeepCopy(master);

        // Override default settings
        foreach (var (key, value) in projectOverride.Overrides.DefaultSettings)
        {
            switch (key)
            {
                case "stopOnCriticalFailure" when value is bool b: merged.DefaultSettings.StopOnCriticalFailure = b; break;
                case "allowProjectOverride" when value is bool b: merged.DefaultSettings.AllowProjectOverride = b; break;
                case "requiresUserConfirmationBeforeModelChanges" when value is bool b: merged.DefaultSettings.RequiresUserConfirmationBeforeModelChanges = b; break;
            }
        }

        // Override task settings
        foreach (var (taskId, taskOverride) in projectOverride.Overrides.Tasks)
        {
            var taskDef = merged.Tasks.FirstOrDefault(t => t.Id == taskId);
            if (taskDef is null)
            {
                warnings.Add($"Override references unknown task '{taskId}' — skipped.");
                continue;
            }

            if (taskOverride.Enabled.HasValue)
                taskDef.Enabled = taskOverride.Enabled.Value;

            foreach (var (key, value) in taskOverride.Settings)
                taskDef.Settings[key] = value;
        }

        return (merged, warnings);
    }

    private static SkillDefinition DeepCopy(SkillDefinition src) => new()
    {
        SchemaVersion = src.SchemaVersion,
        Id = src.Id,
        Name = src.Name,
        Description = src.Description,
        Version = src.Version,
        Author = src.Author,
        IsCompanyMaster = src.IsCompanyMaster,
        SourcePath = src.SourcePath,
        DefaultSettings = new()
        {
            StopOnCriticalFailure = src.DefaultSettings.StopOnCriticalFailure,
            AllowProjectOverride = src.DefaultSettings.AllowProjectOverride,
            RequiresUserConfirmationBeforeModelChanges = src.DefaultSettings.RequiresUserConfirmationBeforeModelChanges
        },
        Tasks = src.Tasks.Select(t => new SkillTaskDefinition
        {
            Id = t.Id,
            Enabled = t.Enabled,
            Settings = new Dictionary<string, object?>(t.Settings)
        }).ToList()
    };
}
