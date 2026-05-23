using Autodesk.Revit.UI;
using Newtonsoft.Json;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Skills;
using RevitMCP.Addin.Skills.Models;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Updates an existing project-level skill override by merging the provided changes
/// into the existing override data. Does not change the Revit model.
/// </summary>
public class UpdateProjectSkillOverrideTool : IRevitMcpTool
{
    public string Name => "revit_update_project_skill_override";
    public string Description => "Updates an existing project skill override. The changesJson is merged into the existing override (replaces top-level keys). Args: skillId, projectId, changesJson (JSON string), note (optional).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Skills;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var skillId = ToolArguments.GetString(request.Arguments, "skillId");
        var projectId = ToolArguments.GetString(request.Arguments, "projectId");
        var changesJson = ToolArguments.GetString(request.Arguments, "changesJson");
        var note = ToolArguments.GetString(request.Arguments, "note");

        if (string.IsNullOrWhiteSpace(skillId)) return Task.FromResult(Fail(request, "skillId is required."));
        if (string.IsNullOrWhiteSpace(projectId)) return Task.FromResult(Fail(request, "projectId is required."));

        var existing = SkillRunner.OverrideService.Load(skillId, projectId);
        if (existing is null)
            return Task.FromResult(Fail(request, $"No override found for skill '{skillId}' / project '{projectId}'. Use revit_create_project_skill_override first."));

        if (!string.IsNullOrWhiteSpace(changesJson))
        {
            SkillOverrideData incoming;
            try { incoming = JsonConvert.DeserializeObject<SkillOverrideData>(changesJson) ?? new(); }
            catch (Exception ex) { return Task.FromResult(Fail(request, $"Invalid changesJson: {ex.Message}")); }

            // Merge incoming task overrides into existing
            foreach (var (taskId, taskOverride) in incoming.Tasks)
            {
                if (!existing.Overrides.Tasks.TryGetValue(taskId, out var existingTaskOverride))
                {
                    existing.Overrides.Tasks[taskId] = taskOverride;
                }
                else
                {
                    if (taskOverride.Enabled.HasValue)
                        existingTaskOverride.Enabled = taskOverride.Enabled;
                    foreach (var (key, val) in taskOverride.Settings)
                        existingTaskOverride.Settings[key] = val;
                }
            }

            // Merge default settings overrides
            foreach (var (key, val) in incoming.DefaultSettings)
                existing.Overrides.DefaultSettings[key] = val;
        }

        existing.UpdatedAt = DateTime.Now;
        if (!string.IsNullOrWhiteSpace(note))
            existing.Notes.Add($"{existing.UpdatedAt:yyyy-MM-dd HH:mm}: {note}");

        SkillRunner.OverrideService.Save(existing);

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Override for skill '{skillId}' / project '{projectId}' updated.",
            Data = new { skillId, projectId, updatedAt = existing.UpdatedAt }
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
