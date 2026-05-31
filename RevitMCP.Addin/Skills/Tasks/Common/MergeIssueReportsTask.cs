using System.Diagnostics;
using RevitMCP.Addin.Skills;
using RevitMCP.Addin.Skills.Models;
using RevitMCP.Core.Models.Issues;

namespace RevitMCP.Addin.Skills.Tasks.Common;

/// <summary>
/// Task ID: common.merge.issues
/// Reads issue lists from multiple SharedData keys and merges them into SharedData["issues"].
/// Configure source keys via the "sourceKeys" setting (comma-separated).
/// If no sourceKeys are configured, picks up all List&lt;SkillIssue&gt; values in SharedData.
/// </summary>
internal sealed class MergeIssueReportsTask : ISkillTask
{
    public string Id           => "common.merge.issues";
    public string Name         => "Merge Issue Lists";
    public bool   ChangesModel => false;

    public SkillTaskResult Run(SkillContext ctx, SkillTaskDefinition taskDef)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var sourceKeysRaw = SkillSettings.GetString(taskDef.Settings, "sourceKeys");
            var mergedSkillIssues = new List<SkillIssue>();
            var mergedIssueDtos   = new List<RevitMCP.Core.Models.Issues.IssueDto>();

            var candidates = string.IsNullOrWhiteSpace(sourceKeysRaw)
                ? ctx.SharedData.ToList()
                : sourceKeysRaw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                               .Select(k => k.Trim()).Where(k => k.Length > 0)
                               .Where(k => ctx.SharedData.ContainsKey(k))
                               .Select(k => new KeyValuePair<string, object?>(k, ctx.SharedData[k]))
                               .ToList();

            foreach (var kv in candidates)
            {
                if (kv.Value is List<SkillIssue> si)
                    mergedSkillIssues.AddRange(si);
                else if (kv.Value is List<RevitMCP.Core.Models.Issues.IssueDto> di)
                    mergedIssueDtos.AddRange(di);
            }

            // If there are native IssueDto items, store in issueDtos; otherwise store SkillIssue list
            if (mergedIssueDtos.Count > 0)
                ctx.SharedData["issueDtos"] = mergedIssueDtos;
            if (mergedSkillIssues.Count > 0)
                ctx.SharedData["issues"] = mergedSkillIssues;

            var total = mergedSkillIssues.Count + mergedIssueDtos.Count;

            return new SkillTaskResult
            {
                TaskId    = Id, TaskName = Name, Success = true,
                Message   = $"Merged {total} issue(s).",
                IssueCount = total, DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            return new SkillTaskResult
            {
                TaskId    = Id, TaskName = Name, Success = false,
                Message   = $"Merge failed: {ex.Message}", DurationMs = sw.ElapsedMilliseconds
            };
        }
    }
}
