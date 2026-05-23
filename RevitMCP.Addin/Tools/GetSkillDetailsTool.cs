using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Skills;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Returns the full definition for a specific skill, optionally merged with a project override.
/// </summary>
public class GetSkillDetailsTool : IRevitMcpTool
{
    public string Name => "revit_get_skill_details";
    public string Description => "Returns full definition for a skill. Optionally merges in the project override. Args: skillId (required), projectId, includeProjectOverride (bool).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Skills;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var skillId = ToolArguments.GetString(request.Arguments, "skillId");
        if (string.IsNullOrWhiteSpace(skillId))
            return Task.FromResult(Fail(request, "skillId is required."));

        var skill = SkillRunner.Registry.Get(skillId);
        if (skill is null)
            return Task.FromResult(Fail(request, $"Skill '{skillId}' not found. Call revit_list_skills to see available skills."));

        var projectId = ToolArguments.GetString(request.Arguments, "projectId");
        var includeOverride = ToolArguments.GetBool(request.Arguments, "includeProjectOverride", false);

        object? overrideInfo = null;
        object? mergedSkillInfo = null;

        if (includeOverride && !string.IsNullOrEmpty(projectId))
        {
            var overrideData = SkillRunner.OverrideService.Load(skillId, projectId);
            if (overrideData is not null)
            {
                overrideInfo = new
                {
                    baseSkillVersion = overrideData.BaseSkillVersion,
                    projectName = overrideData.ProjectName,
                    createdBy = overrideData.CreatedBy,
                    updatedAt = overrideData.UpdatedAt,
                    notes = overrideData.Notes
                };

                var (merged, warnings) = new SkillMerger().Merge(skill, overrideData);
                mergedSkillInfo = BuildSkillDto(merged, warnings);
            }
        }

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Skill '{skill.Name}' retrieved.",
            Data = new
            {
                master = BuildSkillDto(skill, new List<string>()),
                projectOverride = overrideInfo,
                mergedSkill = mergedSkillInfo
            }
        });
    }

    private static object BuildSkillDto(RevitMCP.Addin.Skills.Models.SkillDefinition s, List<string> warnings) => new
    {
        id = s.Id,
        name = s.Name,
        description = s.Description,
        version = s.Version,
        author = s.Author,
        defaultSettings = new
        {
            stopOnCriticalFailure = s.DefaultSettings.StopOnCriticalFailure,
            allowProjectOverride = s.DefaultSettings.AllowProjectOverride,
            requiresUserConfirmationBeforeModelChanges = s.DefaultSettings.RequiresUserConfirmationBeforeModelChanges
        },
        tasks = s.Tasks.Select(t => new
        {
            id = t.Id,
            enabled = t.Enabled,
            settings = t.Settings
        }).ToList(),
        mergeWarnings = warnings
    };

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
