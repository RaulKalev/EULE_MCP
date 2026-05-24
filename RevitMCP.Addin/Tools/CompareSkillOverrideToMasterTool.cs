using System.Diagnostics;
using System.IO;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Skills;
using RevitMCP.Addin.Skills.Models;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Compares a project-level skill override against the current company master skill definition.
/// Returns a diff summary: changed task settings, enabled/disabled tasks, new tasks in master,
/// tasks present in override but missing from master, and version mismatch warnings.
/// Read-only — does not modify any files.
/// Tool name: revit_compare_skill_override_to_master
/// </summary>
public class CompareSkillOverrideToMasterTool : IRevitMcpTool
{
    public string Name        => "revit_compare_skill_override_to_master";
    public string Description => "Compares a project skill override against the current company master. Returns a diff of changed settings, enabled/disabled tasks, new tasks, and version mismatches. Read-only.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory   Category   => ToolCategory.Skills;

    private static readonly SkillOverrideService _overrideService = new();
    private static readonly SkillLoader          _loader          = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        var skillId   = ToolArguments.GetString(request.Arguments, "skillId");
        var projectId = ToolArguments.GetString(request.Arguments, "projectId");

        if (string.IsNullOrWhiteSpace(skillId) || string.IsNullOrWhiteSpace(projectId))
        {
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success   = false,
                Message   = "skillId and projectId are required."
            });
        }

        // Load master skill
        var allSkills = _loader.LoadAll();
        var master = allSkills.FirstOrDefault(s => s.Id.Equals(skillId, StringComparison.OrdinalIgnoreCase));
        if (master == null)
        {
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success   = false,
                Message   = $"Master skill '{skillId}' not found. Available: {string.Join(", ", allSkills.Select(s => s.Id))}"
            });
        }

        // Load override
        var overrideData = _overrideService.Load(skillId, projectId);
        if (overrideData == null)
        {
            return Task.FromResult(new McpToolResult
            {
                RequestId  = request.RequestId,
                Success    = true,
                Message    = $"No project override exists for skill '{skillId}' in project '{projectId}'.",
                Data       = new { hasOverride = false, skillId, projectId },
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        var diff = BuildDiff(master, overrideData);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = $"Diff for '{skillId}' / project '{projectId}': {diff.TotalChanges} change(s).",
            Data       = diff,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static SkillOverrideDiff BuildDiff(SkillDefinition master, SkillOverride overrideData)
    {
        var diff = new SkillOverrideDiff
        {
            SkillId          = master.Id,
            ProjectId        = overrideData.ProjectId,
            MasterVersion    = master.Version,
            OverrideBaseVersion = overrideData.BaseSkillVersion,
            VersionMismatch  = master.Version != overrideData.BaseSkillVersion
        };

        var masterTaskIds   = master.Tasks.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var overrideTaskIds = overrideData.Overrides.Tasks.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Tasks in override that no longer exist in master
        foreach (var id in overrideTaskIds.Except(masterTaskIds, StringComparer.OrdinalIgnoreCase))
            diff.ObsoleteTasks.Add(id);

        // Tasks in master that are not in override
        foreach (var id in masterTaskIds.Except(overrideTaskIds, StringComparer.OrdinalIgnoreCase))
            diff.NewTasksInMaster.Add(id);

        // Changed tasks
        foreach (var masterTask in master.Tasks)
        {
            if (!overrideData.Overrides.Tasks.TryGetValue(masterTask.Id, out var taskOverride)) continue;

            var taskDiff = new TaskDiff { TaskId = masterTask.Id };

            if (taskOverride.Enabled.HasValue && taskOverride.Enabled.Value != masterTask.Enabled)
            {
                taskDiff.EnabledChanged  = true;
                taskDiff.MasterEnabled   = masterTask.Enabled;
                taskDiff.OverrideEnabled = taskOverride.Enabled.Value;
            }

            foreach (var (key, overrideVal) in taskOverride.Settings)
            {
                masterTask.Settings.TryGetValue(key, out var masterVal);
                var masterStr   = masterVal?.ToString();
                var overrideStr = overrideVal?.ToString();

                if (masterStr != overrideStr)
                {
                    taskDiff.ChangedSettings.Add(new SettingDiff
                    {
                        Key           = key,
                        MasterValue   = masterStr,
                        OverrideValue = overrideStr
                    });
                }
            }

            if (taskDiff.HasChanges)
                diff.ChangedTasks.Add(taskDiff);
        }

        // Default setting overrides
        foreach (var (key, overrideVal) in overrideData.Overrides.DefaultSettings)
            diff.OverriddenDefaultSettings.Add(key, overrideVal?.ToString());

        return diff;
    }
}

// ─── Diff models ─────────────────────────────────────────────────────────────

public class SkillOverrideDiff
{
    public string SkillId               { get; set; } = string.Empty;
    public string ProjectId             { get; set; } = string.Empty;
    public string MasterVersion         { get; set; } = string.Empty;
    public string OverrideBaseVersion   { get; set; } = string.Empty;
    public bool   VersionMismatch       { get; set; }
    public List<string>   ObsoleteTasks             { get; set; } = new();
    public List<string>   NewTasksInMaster          { get; set; } = new();
    public List<TaskDiff> ChangedTasks              { get; set; } = new();
    public Dictionary<string, string?> OverriddenDefaultSettings { get; set; } = new();
    public int TotalChanges =>
        ObsoleteTasks.Count + NewTasksInMaster.Count + ChangedTasks.Count +
        OverriddenDefaultSettings.Count + (VersionMismatch ? 1 : 0);
}

public class TaskDiff
{
    public string     TaskId         { get; set; } = string.Empty;
    public bool       EnabledChanged { get; set; }
    public bool       MasterEnabled  { get; set; }
    public bool       OverrideEnabled { get; set; }
    public List<SettingDiff> ChangedSettings { get; set; } = new();
    public bool HasChanges => EnabledChanged || ChangedSettings.Count > 0;
}

public class SettingDiff
{
    public string  Key           { get; set; } = string.Empty;
    public string? MasterValue   { get; set; }
    public string? OverrideValue { get; set; }
}
