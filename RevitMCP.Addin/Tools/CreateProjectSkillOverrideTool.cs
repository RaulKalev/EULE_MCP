using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Skills;
using RevitMCP.Addin.Skills.Models;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Creates a new project-level override for a skill.
/// The changes argument is a JSON string representing SkillOverrideData
/// (e.g. {"tasks":{"check.cabletray.vs.ducts":{"settings":{"clearanceMm":100}}}}).
/// Does not change the Revit model — only writes a JSON config file.
/// </summary>
public class CreateProjectSkillOverrideTool : IRevitMcpTool
{
    public string Name => "revit_create_project_skill_override";
    public string Description => "Creates a project-specific override for a company skill. Args: skillId, projectId, projectName, changesJson (JSON string of override data), note (optional).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Skills;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var skillId = ToolArguments.GetString(request.Arguments, "skillId");
        var projectId = ToolArguments.GetString(request.Arguments, "projectId");
        var projectName = ToolArguments.GetString(request.Arguments, "projectName");
        var changesJson = ToolArguments.GetString(request.Arguments, "changesJson");
        var note = ToolArguments.GetString(request.Arguments, "note");

        if (string.IsNullOrWhiteSpace(skillId)) return Task.FromResult(Fail(request, "skillId is required."));
        if (string.IsNullOrWhiteSpace(projectId)) return Task.FromResult(Fail(request, "projectId is required."));

        var skill = SkillRunner.Registry.Get(skillId);
        if (skill is null) return Task.FromResult(Fail(request, $"Skill '{skillId}' not found."));

        if (SkillRunner.OverrideService.Exists(skillId, projectId))
            return Task.FromResult(Fail(request, $"An override for skill '{skillId}' / project '{projectId}' already exists. Use revit_update_project_skill_override to modify it."));

        SkillOverrideData overrideData;
        try { overrideData = string.IsNullOrWhiteSpace(changesJson)
                ? new SkillOverrideData()
                : JsonConvert.DeserializeObject<SkillOverrideData>(changesJson) ?? new SkillOverrideData(); }
        catch (Exception ex) { return Task.FromResult(Fail(request, $"Invalid changesJson: {ex.Message}")); }

        var now = DateTime.Now;
        var skillOverride = new SkillOverride
        {
            BaseSkillId = skillId,
            BaseSkillVersion = skill.Version,
            ProjectId = projectId,
            ProjectName = string.IsNullOrWhiteSpace(projectName) ? projectId : projectName,
            CreatedBy = Environment.UserName,
            CreatedAt = now,
            UpdatedAt = now,
            Overrides = overrideData,
            Notes = string.IsNullOrWhiteSpace(note) ? new() : new List<string> { $"{now:yyyy-MM-dd HH:mm}: {note}" }
        };

        SkillRunner.OverrideService.Save(skillOverride);

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Project override created for skill '{skillId}' / project '{projectId}'.",
            Data = new { skillId, projectId, baseSkillVersion = skill.Version }
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
