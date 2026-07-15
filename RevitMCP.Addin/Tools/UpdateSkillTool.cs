using System.IO;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Skills;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Edits a user-created skill in place. Company master skills are protected —
/// those are changed via project overrides or a proposed master update.
/// </summary>
public class UpdateSkillTool : IRevitMcpTool
{
    public string Name => "revit_update_skill";
    public string Description =>
        "Updates an existing user-created skill (.skill.json). Requires approval. " +
        "Author ALL content in ENGLISH regardless of conversation language. " +
        "Required: skillId. Optional (only provided fields change): name, description, version, " +
        "tasks (FULL replacement array of { id, enabled, settings }), stopOnCriticalFailure, " +
        "requiresUserConfirmationBeforeModelChanges, author. " +
        "Company master skills cannot be edited — use revit_manage_project_skill_override or revit_propose_master_skill_update. " +
        "Fetch the current definition with revit_get_skill_details first.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Skills;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var skillId = ToolArguments.GetString(request.Arguments, "skillId").Trim();
        if (string.IsNullOrWhiteSpace(skillId))
            return Task.FromResult(Fail(request, "skillId is required."));

        var loaded = SkillRunner.Registry.Get(skillId);
        if (loaded is null)
            return Task.FromResult(Fail(request, $"Skill '{skillId}' not found. Call revit_list_skills to see available skills."));
        if (loaded.IsCompanyMaster)
            return Task.FromResult(Fail(request, $"Skill '{skillId}' is a company master skill and cannot be edited directly. " +
                                                 "Use revit_manage_project_skill_override for project-specific changes or revit_propose_master_skill_update to propose a master change."));
        if (string.IsNullOrWhiteSpace(loaded.SourcePath))
            return Task.FromResult(Fail(request, $"Skill '{skillId}' has no source file path — it cannot be written back."));

        // Work on a clone so a failed write never leaves half-applied changes in the registry.
        var skill = JsonConvert.DeserializeObject<Skills.Models.SkillDefinition>(JsonConvert.SerializeObject(loaded))!;
        skill.SourcePath = loaded.SourcePath;

        var changed = new List<string>();

        var name = ToolArguments.GetString(request.Arguments, "name");
        if (!string.IsNullOrWhiteSpace(name)) { skill.Name = name.Trim(); changed.Add("name"); }

        var description = ToolArguments.GetString(request.Arguments, "description");
        if (!string.IsNullOrWhiteSpace(description)) { skill.Description = description.Trim(); changed.Add("description"); }

        var version = ToolArguments.GetString(request.Arguments, "version");
        if (!string.IsNullOrWhiteSpace(version)) { skill.Version = version.Trim(); changed.Add("version"); }

        var author = ToolArguments.GetString(request.Arguments, "author");
        if (!string.IsNullOrWhiteSpace(author)) { skill.Author = author.Trim(); changed.Add("author"); }

        var (tasks, taskError) = SkillTaskArguments.Parse(request.Arguments, SkillRunner.TaskRegistry);
        if (taskError is not null)
            return Task.FromResult(Fail(request, taskError));
        if (tasks is not null) { skill.Tasks = tasks; changed.Add("tasks"); }

        if (request.Arguments.ContainsKey("stopOnCriticalFailure"))
        {
            skill.DefaultSettings.StopOnCriticalFailure = ToolArguments.GetBool(request.Arguments, "stopOnCriticalFailure");
            changed.Add("stopOnCriticalFailure");
        }
        if (request.Arguments.ContainsKey("requiresUserConfirmationBeforeModelChanges"))
        {
            skill.DefaultSettings.RequiresUserConfirmationBeforeModelChanges = ToolArguments.GetBool(request.Arguments, "requiresUserConfirmationBeforeModelChanges");
            changed.Add("requiresUserConfirmationBeforeModelChanges");
        }

        if (changed.Count == 0)
            return Task.FromResult(Fail(request, "Nothing to update — provide at least one of: name, description, version, author, tasks, stopOnCriticalFailure, requiresUserConfirmationBeforeModelChanges."));

        try
        {
            File.WriteAllText(skill.SourcePath!, JsonConvert.SerializeObject(skill, Formatting.Indented));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail(request, $"Could not write skill file '{skill.SourcePath}': {ex.Message}"));
        }

        SkillRunner.ReloadRegistry();

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Skill '{skill.Name}' ({skillId}) updated ({string.Join(", ", changed)}). " +
                      $"Run it with revit_run_skill (skillId='{skillId}'); dry-run first with revit_preview_skill_run.",
            Data = new { skillId, updatedFields = changed, filePath = skill.SourcePath }
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
