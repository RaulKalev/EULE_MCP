using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Skills;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Lists the catalog of skill task building blocks a skill can be composed from.
/// Example settings are harvested from the loaded skills that reference each task,
/// so the catalog stays current without a hand-maintained duplicate.
/// </summary>
public class ListSkillTasksTool : IRevitMcpTool
{
    public string Name => "revit_list_skill_tasks";
    public string Description =>
        "Lists all available skill task building blocks for composing skills. " +
        "Returns per task: id, name, changesModel, exampleSettings (from an existing skill that uses it), usedBySkills. " +
        "Use together with revit_create_skill / revit_update_skill. New task types require C# code and cannot be created at runtime.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Skills;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var skills = SkillRunner.Registry.GetAll();

        var tasks = SkillRunner.TaskRegistry.GetAll()
            .OrderBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
            .Select(t =>
            {
                var usedBy = skills
                    .Where(s => s.Tasks.Any(td => string.Equals(td.Id, t.Id, StringComparison.OrdinalIgnoreCase)))
                    .Select(s => s.Id)
                    .ToList();
                var exampleSettings = skills
                    .SelectMany(s => s.Tasks)
                    .FirstOrDefault(td => string.Equals(td.Id, t.Id, StringComparison.OrdinalIgnoreCase)
                                          && td.Settings.Count > 0)
                    ?.Settings;
                return new
                {
                    id = t.Id,
                    name = t.Name,
                    changesModel = t.ChangesModel,
                    exampleSettings,
                    usedBySkills = usedBy
                };
            })
            .ToList();

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{tasks.Count} skill task building block(s) available.",
            Data = new { tasks }
        });
    }
}
