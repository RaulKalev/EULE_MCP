using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Skills;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Returns a preview of what tasks will run for a given skill (and optional project override),
/// including which tasks change the model. Use before revit_run_skill to understand impact.
/// </summary>
public class PreviewSkillRunTool : IRevitMcpTool
{
    public string Name => "revit_preview_skill_run";
    public string Description => "Previews what a skill run will do: task list, which tasks change the model, and whether user confirmation is required. Args: skillId (required), projectId, useProjectOverride (bool).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Skills;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var skillId = ToolArguments.GetString(request.Arguments, "skillId");
        if (string.IsNullOrWhiteSpace(skillId))
            return Task.FromResult(Fail(request, "skillId is required."));

        var skill = SkillRunner.Registry.Get(skillId);
        if (skill is null)
            return Task.FromResult(Fail(request, $"Skill '{skillId}' not found. Call revit_list_skills first."));

        var projectId = ToolArguments.GetString(request.Arguments, "projectId");
        var useOverride = ToolArguments.GetBool(request.Arguments, "useProjectOverride", false);

        RevitMCP.Addin.Skills.Models.SkillDefinition mergedSkill = skill;
        var mergeWarnings = new List<string>();
        bool hasOverride = false;

        if (useOverride && !string.IsNullOrEmpty(projectId))
        {
            var overrideData = SkillRunner.OverrideService.Load(skillId, projectId);
            hasOverride = overrideData is not null;
            if (overrideData is not null)
                (mergedSkill, mergeWarnings) = new SkillMerger().Merge(skill, overrideData);
        }

        var taskRegistry = SkillRunner.TaskRegistry;
        var tasksToRun = mergedSkill.Tasks
            .Where(t => t.Enabled)
            .Select(t =>
            {
                var impl = taskRegistry.Get(t.Id);
                return new
                {
                    id = t.Id,
                    name = impl?.Name ?? t.Id,
                    enabled = true,
                    changesModel = impl?.ChangesModel ?? false,
                    hasImplementation = impl is not null,
                    settings = t.Settings
                };
            }).ToList();

        var disabledTasks = mergedSkill.Tasks.Where(t => !t.Enabled).Select(t => t.Id).ToList();
        var modelChangingTasks = tasksToRun.Where(t => t.changesModel).Select(t => t.id).ToList();
        var requiresConfirmation = modelChangingTasks.Count > 0
                                   && mergedSkill.DefaultSettings.RequiresUserConfirmationBeforeModelChanges;

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Preview: {tasksToRun.Count} task(s) will run, {modelChangingTasks.Count} change(s) the model.",
            Data = new
            {
                skillId,
                skillName = mergedSkill.Name,
                usingProjectOverride = hasOverride,
                requiresUserConfirmation = requiresConfirmation,
                modelChangingTasks,
                tasksToRun,
                disabledTasks,
                mergeWarnings
            }
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
