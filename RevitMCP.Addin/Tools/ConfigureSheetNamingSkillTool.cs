using Autodesk.Revit.UI;
using Newtonsoft.Json;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Skills;
using RevitMCP.Addin.Skills.Models;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Convenience tool to configure the sheet naming skill override for a project.
/// Creates or updates the project skill override for "company.lehed.nimetamise-kontroll",
/// enabling or disabling Excel comparison and export tasks based on the provided flags.
/// </summary>
public class ConfigureSheetNamingSkillTool : IRevitMcpTool
{
    private const string SkillId = "company.lehed.nimetamise-kontroll";

    public string Name => "revit_configure_sheet_naming_skill";
    public string Description =>
        "Convenience tool to configure the sheet naming skill for a project. " +
        "Creates or updates the project skill override, enabling Excel comparison and export tasks. " +
        "Args: projectId (required), projectName, excelFilePath, enableExcelComparison (bool), " +
        "enableExcelReport (bool), enableJsonReport (bool), allowedDisciplines (string[]), allowedStages (string[]).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Skills;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var projectId = ToolArguments.GetString(request.Arguments, "projectId");
        if (string.IsNullOrWhiteSpace(projectId))
            return Task.FromResult(Fail(request, "projectId is required."));

        var projectName = ToolArguments.GetString(request.Arguments, "projectName", projectId);
        var excelFilePath = ToolArguments.GetString(request.Arguments, "excelFilePath");
        var enableExcelComparison = ToolArguments.GetBool(request.Arguments, "enableExcelComparison", false);
        var enableExcelReport = ToolArguments.GetBool(request.Arguments, "enableExcelReport", false);
        var enableJsonReport = ToolArguments.GetBool(request.Arguments, "enableJsonReport", false);
        var allowedDisciplines = ToolArguments.GetStringArray(request.Arguments, "allowedDisciplines");
        var allowedStages = ToolArguments.GetStringArray(request.Arguments, "allowedStages");

        // Validate Excel path if Excel comparison is enabled
        if (enableExcelComparison && string.IsNullOrWhiteSpace(excelFilePath))
            return Task.FromResult(Fail(request, "excelFilePath is required when enableExcelComparison=true."));

        var skill = SkillRunner.Registry.Get(SkillId);
        if (skill is null)
            return Task.FromResult(Fail(request, $"Skill '{SkillId}' not found. Ensure the addin is loaded correctly."));

        // Build override data
        var overrideData = new SkillOverrideData();

        // Excel reading task
        if (enableExcelComparison)
        {
            var readSettings = new Dictionary<string, object?> { ["excelFilePath"] = excelFilePath };
            overrideData.Tasks["read.excel.document-register"] = new SkillTaskOverride
            {
                Enabled = true,
                Settings = readSettings
            };

            // Enable comparison task
            overrideData.Tasks["compare.excel-register"] = new SkillTaskOverride { Enabled = true };
        }

        // Export tasks
        overrideData.Tasks["export.excel-report"] = new SkillTaskOverride { Enabled = enableExcelReport };
        overrideData.Tasks["export.json-report"] = new SkillTaskOverride { Enabled = enableJsonReport };

        // Allowed disciplines / stages as default settings
        if (allowedDisciplines.Length > 0)
            overrideData.DefaultSettings["allowedDisciplines"] = allowedDisciplines;
        if (allowedStages.Length > 0)
            overrideData.DefaultSettings["allowedStages"] = allowedStages;

        var now = DateTime.Now;
        var isUpdate = SkillRunner.OverrideService.Exists(SkillId, projectId);

        if (isUpdate)
        {
            // Load existing and merge
            var existing = SkillRunner.OverrideService.Load(SkillId, projectId)!;
            foreach (var (taskId, taskOverride) in overrideData.Tasks)
                existing.Overrides.Tasks[taskId] = taskOverride;
            foreach (var (key, val) in overrideData.DefaultSettings)
                existing.Overrides.DefaultSettings[key] = val;
            existing.UpdatedAt = now;
            SkillRunner.OverrideService.Save(existing);
        }
        else
        {
            var skillOverride = new SkillOverride
            {
                BaseSkillId = SkillId,
                BaseSkillVersion = skill.Version,
                ProjectId = projectId,
                ProjectName = projectName,
                CreatedAt = now,
                UpdatedAt = now,
                Overrides = overrideData,
                Notes = new List<string>
                {
                    $"Configured by revit_configure_sheet_naming_skill on {now:yyyy-MM-dd HH:mm:ss}"
                }
            };
            SkillRunner.OverrideService.Save(skillOverride);
        }

        // Build preview of what will happen
        var preview = new
        {
            skillId = SkillId,
            projectId,
            projectName,
            isNewOverride = !isUpdate,
            excelComparisonEnabled = enableExcelComparison,
            excelFilePath = string.IsNullOrWhiteSpace(excelFilePath) ? null : excelFilePath,
            excelReportEnabled = enableExcelReport,
            jsonReportEnabled = enableJsonReport,
            allowedDisciplines,
            allowedStages,
            hint = "Call revit_run_skill with skillId and projectId, useProjectOverride=true to apply."
        };

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = isUpdate
                ? $"Updated sheet naming skill override for project '{projectId}'."
                : $"Created sheet naming skill override for project '{projectId}'.",
            Data = preview
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
