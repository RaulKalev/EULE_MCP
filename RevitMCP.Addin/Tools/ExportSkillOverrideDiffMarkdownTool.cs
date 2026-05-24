using System.Diagnostics;
using System.IO;
using System.Text;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Export;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Skills;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Exports a human-readable Markdown diff report comparing a project skill override
/// against the current company master skill.
/// Tool name: revit_export_skill_override_diff_markdown
/// </summary>
public class ExportSkillOverrideDiffMarkdownTool : IRevitMcpTool
{
    public string Name        => "revit_export_skill_override_diff_markdown";
    public string Description => "Exports a Markdown diff report comparing a project skill override to the company master. Saves to the standard export folder. Read-only: does not modify any skill files.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory   Category   => ToolCategory.Skills;

    private static readonly ExportPathService _pathService    = new();
    private static readonly SkillOverrideService _overrideSvc = new();
    private static readonly SkillLoader          _loader      = new();

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

        var allSkills = _loader.LoadAll();
        var master = allSkills.FirstOrDefault(s => s.Id.Equals(skillId, StringComparison.OrdinalIgnoreCase));
        if (master == null)
        {
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success   = false,
                Message   = $"Master skill '{skillId}' not found."
            });
        }

        var overrideData = _overrideSvc.Load(skillId, projectId);
        if (overrideData == null)
        {
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success   = true,
                Message   = $"No project override exists for '{skillId}' in project '{projectId}'. Nothing to diff.",
                Data      = new { hasOverride = false }
            });
        }

        // Build the diff using the shared compare tool logic
        var compareRequest = new McpToolRequest
        {
            RequestId = request.RequestId,
            Arguments = new() { ["skillId"] = skillId, ["projectId"] = projectId }
        };
        var compareTool = new CompareSkillOverrideToMasterTool();
        var compareResult = compareTool.ExecuteAsync(uiapp, compareRequest, cancellationToken).Result;

        // Extract diff from compare result Data property
        var diff = compareResult.Data as SkillOverrideDiff;

        // Build Markdown
        var md = BuildMarkdown(skillId, projectId, master.Name, overrideData.ProjectName, diff);

        // Write to export folder
        var outputDir = _pathService.ExportDirectory;
        Directory.CreateDirectory(outputDir);

        var safeProject = SanitiseName(projectId);
        var safeSkill   = SanitiseName(skillId);
        var fileName    = $"SkillDiff_{safeSkill}_{safeProject}_{DateTime.Now:yyyyMMdd_HHmmss}.md";
        var filePath    = Path.Combine(outputDir, fileName);
        File.WriteAllText(filePath, md, Encoding.UTF8);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = $"Diff report saved: {fileName}",
            Data       = new { filePath, skillId, projectId, changeCount = diff?.TotalChanges ?? 0 },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static string BuildMarkdown(
        string skillId, string projectId, string skillName, string projectName, SkillOverrideDiff? diff)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Skill Override Diff Report");
        sb.AppendLine();
        sb.AppendLine($"| Field | Value |");
        sb.AppendLine($"|-------|-------|");
        sb.AppendLine($"| Skill | `{skillId}` ({skillName}) |");
        sb.AppendLine($"| Project | `{projectId}` {(string.IsNullOrEmpty(projectName) ? "" : $"— {projectName}")} |");
        sb.AppendLine($"| Generated | {DateTime.Now:yyyy-MM-dd HH:mm:ss} |");

        if (diff == null)
        {
            sb.AppendLine();
            sb.AppendLine("> No diff data available.");
            return sb.ToString();
        }

        sb.AppendLine($"| Master Version | `{diff.MasterVersion}` |");
        sb.AppendLine($"| Override Base Version | `{diff.OverrideBaseVersion}` |");
        if (diff.VersionMismatch)
            sb.AppendLine($"| ⚠ Version Mismatch | Override was created from an older master version |");

        sb.AppendLine();

        if (diff.TotalChanges == 0)
        {
            sb.AppendLine("_No differences found. The project override matches the master._");
            return sb.ToString();
        }

        sb.AppendLine($"**Total changes: {diff.TotalChanges}**");
        sb.AppendLine();

        // Changed tasks
        if (diff.ChangedTasks.Count > 0)
        {
            sb.AppendLine("## Changed Tasks");
            sb.AppendLine();
            foreach (var task in diff.ChangedTasks)
            {
                sb.AppendLine($"### `{task.TaskId}`");
                if (task.EnabledChanged)
                    sb.AppendLine($"- **Enabled**: master=`{task.MasterEnabled}` → override=`{task.OverrideEnabled}`");
                foreach (var s in task.ChangedSettings)
                    sb.AppendLine($"- **{s.Key}**: master=`{s.MasterValue ?? "(null)"}` → override=`{s.OverrideValue ?? "(null)"}`");
                sb.AppendLine();
            }
        }

        // Obsolete tasks
        if (diff.ObsoleteTasks.Count > 0)
        {
            sb.AppendLine("## Override Tasks Not in Master");
            sb.AppendLine();
            sb.AppendLine("> These task IDs are in the project override but no longer exist in the master skill.");
            sb.AppendLine();
            foreach (var id in diff.ObsoleteTasks)
                sb.AppendLine($"- `{id}`");
            sb.AppendLine();
        }

        // New tasks in master
        if (diff.NewTasksInMaster.Count > 0)
        {
            sb.AppendLine("## New Tasks in Master (not in Override)");
            sb.AppendLine();
            sb.AppendLine("> These tasks exist in the current master but have no override entry.");
            sb.AppendLine();
            foreach (var id in diff.NewTasksInMaster)
                sb.AppendLine($"- `{id}`");
            sb.AppendLine();
        }

        // Default settings overrides
        if (diff.OverriddenDefaultSettings.Count > 0)
        {
            sb.AppendLine("## Default Settings Overrides");
            sb.AppendLine();
            sb.AppendLine("| Setting | Override Value |");
            sb.AppendLine("|---------|----------------|");
            foreach (var (k, v) in diff.OverriddenDefaultSettings)
                sb.AppendLine($"| `{k}` | `{v ?? "(null)"}` |");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string SanitiseName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
