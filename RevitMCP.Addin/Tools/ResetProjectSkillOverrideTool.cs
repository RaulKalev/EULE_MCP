using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Skills;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Deletes the project-level override for a skill, reverting to the company master.
/// Does not change the Revit model.
/// </summary>
public class ResetProjectSkillOverrideTool : IRevitMcpTool
{
    public string Name => "revit_reset_project_skill_override";
    public string Description => "Deletes the project-specific skill override, reverting to the company master skill. Args: skillId (required), projectId (required).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Skills;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var skillId = ToolArguments.GetString(request.Arguments, "skillId");
        var projectId = ToolArguments.GetString(request.Arguments, "projectId");

        if (string.IsNullOrWhiteSpace(skillId)) return Task.FromResult(Fail(request, "skillId is required."));
        if (string.IsNullOrWhiteSpace(projectId)) return Task.FromResult(Fail(request, "projectId is required."));

        if (!SkillRunner.OverrideService.Exists(skillId, projectId))
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"No override found for skill '{skillId}' / project '{projectId}' — nothing to reset."
            });

        SkillRunner.OverrideService.Delete(skillId, projectId);

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Project override for skill '{skillId}' / project '{projectId}' deleted. Company master skill will be used."
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
