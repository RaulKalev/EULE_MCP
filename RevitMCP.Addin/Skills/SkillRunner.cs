using System.IO;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Skills.Models;
using System.Diagnostics;

namespace RevitMCP.Addin.Skills;

/// <summary>
/// Orchestrates a full skill run:
///  1. Loads the skill from the registry
///  2. Loads the project override (if requested)
///  3. Merges skill + override
///  4. Builds a <see cref="SkillContext"/>
///  5. Runs each enabled task in order
///  6. Stores the accumulated result in SharedData for report tasks
///  7. Writes the final JSON run report
///  8. Returns the <see cref="SkillRunResult"/>
/// </summary>
public class SkillRunner
{
    private static readonly SkillLoader _loader = new();
    private static readonly SkillRegistry _registry;
    private static readonly SkillOverrideService _overrideService = new();
    private static readonly SkillMerger _merger = new();
    private static readonly SkillTaskRegistry _taskRegistry = new();
    private static readonly SkillReportWriter _reportWriter = new();

    static SkillRunner()
    {
        _registry = new SkillRegistry(_loader);
        _registry.Load();
    }

    /// <summary>Reload the skill registry (e.g. after new skills have been deployed).</summary>
    public static void ReloadRegistry() => _registry.Reload();

    public static SkillRegistry Registry => _registry;
    public static SkillOverrideService OverrideService => _overrideService;
    public static SkillTaskRegistry TaskRegistry => _taskRegistry;

    /// <summary>
    /// Runs a full skill or a single named task within a skill.
    /// </summary>
    /// <param name="uiapp">Active Revit UIApplication (called on Revit API thread).</param>
    /// <param name="skillId">ID of the skill to run.</param>
    /// <param name="projectId">Project identifier used for override lookup.</param>
    /// <param name="useProjectOverride">Whether to apply a project override if one exists.</param>
    /// <param name="singleTaskId">Optional: run only this task ID within the skill.</param>
    public static SkillRunResult Run(
        UIApplication uiapp,
        string skillId,
        string? projectId,
        bool useProjectOverride,
        string? singleTaskId = null)
    {
        var sw = Stopwatch.StartNew();
        var startedAt = DateTime.Now;

        // --- 1. Resolve skill ---
        var masterSkill = _registry.Get(skillId);
        if (masterSkill is null)
            return ErrorResult(skillId, projectId ?? "", startedAt, $"Skill '{skillId}' not found. Call revit_list_skills to see available skills.");

        // --- 2. Load override ---
        SkillOverride? projectOverride = null;
        bool usedOverride = false;
        if (useProjectOverride && !string.IsNullOrWhiteSpace(projectId))
        {
            projectOverride = _overrideService.Load(skillId, projectId);
            usedOverride = projectOverride is not null;
        }

        // --- 3. Merge ---
        var (mergedSkill, mergeWarnings) = _merger.Merge(masterSkill, projectOverride);

        // --- 4. Build context ---
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc is null)
            return ErrorResult(skillId, projectId ?? "", startedAt, "No active Revit document.");

        var runId = $"{DateTime.Now:yyyyMMdd_HHmmss}_{skillId.Replace('.', '-')}";
        var outputFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RKTools", "RevitMCP", "SkillRuns");

        var ctx = new SkillContext
        {
            UiApplication = uiapp,
            Document = doc,
            ProjectId = projectId ?? string.Empty,
            ProjectName = doc.Title,
            SkillId = skillId,
            RunId = runId,
            OutputFolder = outputFolder
        };

        var result = new SkillRunResult
        {
            RunId = runId,
            SkillId = skillId,
            SkillName = masterSkill.Name,
            ProjectId = projectId ?? string.Empty,
            UsedProjectOverride = usedOverride,
            StartedAt = startedAt
        };

        result.Warnings.AddRange(mergeWarnings);

        // --- 5. Run tasks ---
        foreach (var taskDef in mergedSkill.Tasks)
        {
            // Skip if caller requested a specific task and this isn't it
            if (singleTaskId is not null && !taskDef.Id.Equals(singleTaskId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!taskDef.Enabled)
            {
                result.TaskResults.Add(new SkillTaskResult
                {
                    TaskId = taskDef.Id,
                    TaskName = taskDef.Id,
                    WasSkipped = true,
                    Success = true,
                    Message = "Task is disabled in the skill definition."
                });
                continue;
            }

            var taskImpl = _taskRegistry.Get(taskDef.Id);
            if (taskImpl is null)
            {
                result.TaskResults.Add(new SkillTaskResult
                {
                    TaskId = taskDef.Id,
                    TaskName = taskDef.Id,
                    WasSkipped = true,
                    Success = false,
                    Message = $"No implementation found for task '{taskDef.Id}' — task skipped."
                });
                result.Warnings.Add($"Unknown task id '{taskDef.Id}'.");
                continue;
            }

            var taskResult = taskImpl.Run(ctx, taskDef);
            taskResult.TaskName = string.IsNullOrEmpty(taskResult.TaskName) ? taskImpl.Name : taskResult.TaskName;
            result.TaskResults.Add(taskResult);

            if (taskResult.CriticalFailure && mergedSkill.DefaultSettings.StopOnCriticalFailure)
            {
                result.Warnings.Add($"Stopped after critical failure in task '{taskDef.Id}'.");
                break;
            }
        }

        // --- 6. Store result in SharedData for report tasks ---
        result.FinishedAt = DateTime.Now;
        result.Success = result.TaskResults.All(t => t.Success || t.WasSkipped);
        result.Summary = BuildSummary(result.TaskResults);
        ctx.SharedData["runResult"] = result;

        // Run any report tasks that are present but haven't run yet (e.g. when singleTaskId is set)
        // In a full run they already executed in the loop above.
        if (singleTaskId is null)
        {
            // report tasks already ran as part of the loop — nothing extra to do
        }

        // --- 7. Write JSON report ---
        try
        {
            _reportWriter.Write(result);
        }
        catch { /* non-fatal */ }

        return result;
    }

    private static SkillRunSummary BuildSummary(List<SkillTaskResult> taskResults) => new()
    {
        TotalIssues    = taskResults.Sum(t => t.IssueCount),
        CriticalIssues = taskResults.SelectMany(t => t.Issues)
                           .Count(i => i.Severity is "Critical" or "Kriitiline"),
        Warnings       = taskResults.Sum(t => t.Warnings.Count),
        TasksRun       = taskResults.Count(t => !t.WasSkipped),
        TasksFailed    = taskResults.Count(t => !t.Success && !t.WasSkipped),
        TasksSkipped   = taskResults.Count(t => t.WasSkipped)
    };

    private static SkillRunResult ErrorResult(string skillId, string projectId, DateTime startedAt, string error) => new()
    {
        RunId = $"error_{DateTime.Now:yyyyMMdd_HHmmss}",
        SkillId = skillId,
        ProjectId = projectId,
        StartedAt = startedAt,
        FinishedAt = DateTime.Now,
        Success = false,
        ErrorMessage = error
    };
}
