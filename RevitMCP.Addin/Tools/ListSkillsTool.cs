using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Skills;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Lists all available company skills with their IDs, names, versions and whether a
/// project override exists for the current project.
/// </summary>
public class ListSkillsTool : IRevitMcpTool
{
    public string Name => "revit_list_skills";
    public string Description => "Lists all available company skills (from the company skill library). Returns id, name, description, version, and whether a project override exists.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Skills;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var projectId = ToolArguments.GetString(request.Arguments, "projectId");
        var overrideService = SkillRunner.OverrideService;

        var skills = SkillRunner.Registry.GetAll().Select(s => new
        {
            id = s.Id,
            name = s.Name,
            description = s.Description,
            version = s.Version,
            author = s.Author,
            taskCount = s.Tasks.Count,
            hasProjectOverride = !string.IsNullOrEmpty(projectId) && overrideService.Exists(s.Id, projectId)
        }).ToList();

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{skills.Count} skill(s) available.",
            Data = new { skills }
        });
    }
}
