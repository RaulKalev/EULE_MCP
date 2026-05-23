using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Skills;
using RevitMCP.Core.Models;
using System.Diagnostics;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Runs a single named task from within a skill. Useful for re-running or debugging
/// individual tasks without running the full skill. Read-only.
/// </summary>
public class RunSkillTaskTool : IRevitMcpTool
{
    public string Name => "revit_run_skill_task";
    public string Description => "Runs a single task within a skill by its task ID. Read-only. Useful for re-running a specific check or report without repeating all tasks. Args: skillId (required), taskId (required), projectId, useProjectOverride (bool).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Skills;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        var skillId = ToolArguments.GetString(request.Arguments, "skillId");
        var taskId = ToolArguments.GetString(request.Arguments, "taskId");
        if (string.IsNullOrWhiteSpace(skillId))
            return Task.FromResult(Fail(request, "skillId is required."));
        if (string.IsNullOrWhiteSpace(taskId))
            return Task.FromResult(Fail(request, "taskId is required."));

        var projectId = ToolArguments.GetString(request.Arguments, "projectId");
        var useOverride = ToolArguments.GetBool(request.Arguments, "useProjectOverride", false);

        var runResult = SkillRunner.Run(uiapp, skillId, projectId, useOverride, singleTaskId: taskId);

        var taskResult = runResult.TaskResults.FirstOrDefault(t => t.TaskId.Equals(taskId, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = runResult.Success && taskResult is not null,
            Message = taskResult?.Message ?? runResult.ErrorMessage ?? $"Task '{taskId}' not found in skill '{skillId}'.",
            Data = taskResult is null ? null : new
            {
                taskId = taskResult.TaskId,
                taskName = taskResult.TaskName,
                success = taskResult.Success,
                wasSkipped = taskResult.WasSkipped,
                issueCount = taskResult.IssueCount,
                issues = taskResult.Issues.Take(100).ToList(),
                totalIssues = taskResult.IssueCount,
                durationMs = taskResult.DurationMs,
                runId = runResult.RunId
            },
            Warnings = runResult.Warnings.Concat(taskResult?.Warnings ?? Enumerable.Empty<string>()).ToList(),
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
