using System.Diagnostics;
using RevitMCP.Addin.ModelState;
using RevitMCP.Addin.Skills.Models;
using RevitMCP.Addin.Tools.ModelState;
using RevitMCP.Core.Models.Issues;

namespace RevitMCP.Addin.Skills.Tasks.Audit;

/// <summary>
/// Task ID: audit.model (#109)
/// Runs the model health audit (revit_audit_model) and adds one issue per warn/bad figure (and per failed section) to
/// SharedData["issueDtos"], after any issues an earlier task put there, so the export tasks of a skill such as
/// company.project.pre-delivery include them. Settings: sections (string array, default all), publish (bool, default
/// false: publish the audit page too). Does not change the model.
/// </summary>
internal sealed class AuditModelTask : ISkillTask
{
    public string Id => "audit.model";
    public string Name => "Model Health Audit";
    public bool ChangesModel => false;

    public SkillTaskResult Run(SkillContext ctx, SkillTaskDefinition taskDef)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var requested = SkillSettings.GetStringArray(taskDef.Settings, "sections", Array.Empty<string>());
            var sections = AuditSections.Resolve(requested, out var unknown);
            var report = AuditModelRunner.Run(ctx.UiApplication, ctx.Document, sections, CancellationToken.None);
            var warnings = new List<string>(report.Warnings);
            if (unknown.Count > 0) warnings.Add($"Unknown audit section(s) ignored: {string.Join(", ", unknown)}.");

            string? url = null;
            if (SkillSettings.GetBool(taskDef.Settings, "publish", false))
                (_, url) = AuditModelRunner.Publish(ctx.Document, report, null, warnings);

            var issues = AuditIssues.Build(report, ctx.RunId, Id, DateTimeOffset.UtcNow);
            if (ctx.SharedData.TryGetValue("issueDtos", out var existing) && existing is List<IssueDto> list)
                list.AddRange(issues);
            else
                ctx.SharedData["issueDtos"] = issues;
            ctx.SharedData["auditReport"] = report;

            return new SkillTaskResult
            {
                TaskId = Id,
                TaskName = Name,
                Success = true,
                Message = $"Audit of '{ctx.Document.Title}': {AuditReportHtml.StatusText(report.Status)}, {issues.Count} finding(s)" +
                          (url != null ? $"; page {url}." : "."),
                IssueCount = issues.Count,
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            return new SkillTaskResult
            {
                TaskId = Id,
                TaskName = Name,
                Success = false,
                Message = $"Model audit failed: {ex.Message}",
                DurationMs = sw.ElapsedMilliseconds
            };
        }
    }
}
