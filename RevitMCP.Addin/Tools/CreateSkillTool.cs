using System.IO;
using System.Text.RegularExpressions;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Skills;
using RevitMCP.Addin.Skills.Models;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Creates a new skill (.skill.json) in the company skill library from task building blocks.
/// The skill becomes immediately runnable via revit_run_skill after the registry reload.
/// </summary>
public class CreateSkillTool : IRevitMcpTool
{
    private static readonly Regex SkillIdPattern = new("^[a-z0-9]+([._-][a-z0-9]+)*$", RegexOptions.Compiled);

    public string Name => "revit_create_skill";
    public string Description =>
        "Creates a new skill (.skill.json) in the company skill library. Requires approval. " +
        "Author ALL content (id, name, description, settings) in ENGLISH regardless of conversation language. " +
        "Required: skillId (lowercase dot/dash separated, e.g. 'user.delivery.pdf-check'), name, description, " +
        "tasks (array of { id, enabled, settings } — ids from revit_list_skill_tasks). " +
        "Optional: version (default 1.0.0), author, stopOnCriticalFailure (default false), " +
        "requiresUserConfirmationBeforeModelChanges (default true), overwrite (default false). " +
        "Follow the revit_skill_builder_guide workflow before calling this.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Skills;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var skillId     = ToolArguments.GetString(request.Arguments, "skillId").Trim();
        var name        = ToolArguments.GetString(request.Arguments, "name").Trim();
        var description = ToolArguments.GetString(request.Arguments, "description").Trim();
        var version     = ToolArguments.GetString(request.Arguments, "version", "1.0.0").Trim();
        var author      = ToolArguments.GetString(request.Arguments, "author", "RevitMCP Skill Builder").Trim();
        var overwrite   = ToolArguments.GetBool(request.Arguments, "overwrite", false);
        var stopOnCritical = ToolArguments.GetBool(request.Arguments, "stopOnCriticalFailure", false);
        var requiresConfirmation = ToolArguments.GetBool(request.Arguments, "requiresUserConfirmationBeforeModelChanges", true);

        if (string.IsNullOrWhiteSpace(skillId))
            return Task.FromResult(Fail(request, "skillId is required."));
        if (!SkillIdPattern.IsMatch(skillId))
            return Task.FromResult(Fail(request, "skillId must be lowercase letters/digits separated by '.', '-' or '_' (e.g. 'user.delivery.pdf-check')."));
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult(Fail(request, "name is required."));
        if (string.IsNullOrWhiteSpace(description))
            return Task.FromResult(Fail(request, "description is required."));

        var (tasks, taskError) = SkillTaskArguments.Parse(request.Arguments, SkillRunner.TaskRegistry);
        if (taskError is not null)
            return Task.FromResult(Fail(request, taskError));
        if (tasks is null)
            return Task.FromResult(Fail(request, "tasks is required — an array of { id, enabled, settings } objects. Call revit_list_skill_tasks for the catalog."));

        var existing = SkillRunner.Registry.Get(skillId);
        if (existing is not null)
        {
            if (existing.IsCompanyMaster)
                return Task.FromResult(Fail(request, $"Skill id '{skillId}' belongs to a company master skill and cannot be overwritten. Pick a different id (recommended prefix: 'user.')."));
            if (!overwrite)
                return Task.FromResult(Fail(request, $"Skill '{skillId}' already exists. Pass overwrite=true to replace it, or use revit_update_skill to edit it."));
        }

        var skill = new SkillDefinition
        {
            Id = skillId,
            Name = name,
            Description = description,
            Version = string.IsNullOrWhiteSpace(version) ? "1.0.0" : version,
            Author = author,
            IsCompanyMaster = false,
            Tasks = tasks,
            DefaultSettings = new SkillDefaultSettings
            {
                StopOnCriticalFailure = stopOnCritical,
                AllowProjectOverride = true,
                RequiresUserConfirmationBeforeModelChanges = requiresConfirmation
            }
        };

        // Reuse the existing file when overwriting so custom file names survive.
        var filePath = existing?.SourcePath
                       ?? Path.Combine(SkillLoader.GetWritableSkillsDirectory(), $"{skillId}.skill.json");
        try
        {
            File.WriteAllText(filePath, JsonConvert.SerializeObject(skill, Formatting.Indented));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail(request, $"Could not write skill file '{filePath}': {ex.Message}"));
        }

        SkillRunner.ReloadRegistry();
        if (SkillRunner.Registry.Get(skillId) is null)
            return Task.FromResult(Fail(request, $"Skill file was written to '{filePath}' but did not load back — check the file for JSON errors."));

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Skill '{name}' ({skillId}) created with {tasks.Count} task(s). " +
                      $"Activate it by asking to run skill '{name}' (revit_run_skill with skillId='{skillId}'); " +
                      $"dry-run first with revit_preview_skill_run. Edit later with revit_update_skill.",
            Data = new
            {
                skillId,
                name,
                filePath,
                taskCount = tasks.Count,
                activation = new
                {
                    runTool = "revit_run_skill",
                    previewTool = "revit_preview_skill_run",
                    editTool = "revit_update_skill",
                    examplePrompt = $"Run skill '{name}'"
                }
            }
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
