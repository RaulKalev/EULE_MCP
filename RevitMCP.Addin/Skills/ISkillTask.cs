using RevitMCP.Addin.Skills.Models;

namespace RevitMCP.Addin.Skills;

/// <summary>
/// Interface all skill task implementations must satisfy.
/// The <see cref="Id"/> must match the task id used in the skill JSON file.
/// </summary>
public interface ISkillTask
{
    string Id { get; }
    string Name { get; }
    bool ChangesModel { get; }

    SkillTaskResult Run(SkillContext context, SkillTaskDefinition taskDef);
}
