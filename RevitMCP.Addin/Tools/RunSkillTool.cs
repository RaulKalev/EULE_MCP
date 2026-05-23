using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Skills;
using RevitMCP.Core.Models;
using System.Diagnostics;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Runs a full skill (all enabled tasks in order). Read-only: does not modify the Revit model.
/// Args: skillId (required), projectId, useProjectOverride (bool, default false).
/// </summary>
public class RunSkillTool : IRevitMcpTool
{
    public string Name => "revit_run_skill";
    public string Description => "Runs all enabled tasks in a company skill and returns a full results report. Read-only — does not modify the Revit model. Args: skillId (required), projectId, useProjectOverride (bool, default false). Call revit_preview_skill_run first to understand impact.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Skills;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        var skillId = ToolArguments.GetString(request.Arguments, "skillId");
        if (string.IsNullOrWhiteSpace(skillId))
            return Task.FromResult(Fail(request, "skillId is required."));

        var projectId = ToolArguments.GetString(request.Arguments, "projectId");
        var useOverride = ToolArguments.GetBool(request.Arguments, "useProjectOverride", false);

        var runResult = SkillRunner.Run(uiapp, skillId, projectId, useOverride);

        var result = new McpToolResult
        {
            RequestId = request.RequestId,
            Success = runResult.Success,
            Message = runResult.ErrorMessage ?? $"Skill '{runResult.SkillName}' completed. {runResult.Summary.TotalIssues} issue(s) found across {runResult.Summary.TasksRun} task(s).",
            Data = new
            {
                runId = runResult.RunId,
                success = runResult.Success,
                summary = runResult.Summary,
                usedProjectOverride = runResult.UsedProjectOverride,
                taskResults = runResult.TaskResults.Select(t => new
                {
                    taskId = t.TaskId,
                    taskName = t.TaskName,
                    success = t.Success,
                    wasSkipped = t.WasSkipped,
                    issueCount = t.IssueCount,
                    message = t.Message,
                    durationMs = t.DurationMs
                }).ToList(),
                excelReportPath = runResult.TaskResults
                    .FirstOrDefault(t => t.TaskId == "export.excel-report" && t.Success)?.Message,
                jsonReportPath = runResult.TaskResults
                    .FirstOrDefault(t => t.TaskId == "export.json-report" && t.Success)?.Message
            },
            Warnings = runResult.Warnings,
            DurationMs = sw.ElapsedMilliseconds
        };

        return Task.FromResult(result);
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
