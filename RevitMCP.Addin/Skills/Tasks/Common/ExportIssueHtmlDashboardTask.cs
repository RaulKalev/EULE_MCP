using System.Diagnostics;
using System.IO;
using RevitMCP.Addin.Reports;
using RevitMCP.Addin.Skills;
using RevitMCP.Addin.Skills.Models;

namespace RevitMCP.Addin.Skills.Tasks.Common;

/// <summary>
/// Task ID: common.export.html-dashboard
/// Reads the aggregated <see cref="SkillIssue"/> list from SharedData["issues"]
/// and exports it as a standalone offline HTML dashboard.
/// Stores the output path in SharedData["htmlReportPath"].
/// </summary>
internal sealed class ExportIssueHtmlDashboardTask : ISkillTask
{
    public string Id           => "common.export.html-dashboard";
    public string Name         => "Export HTML Issue Dashboard";
    public bool   ChangesModel => false;

    private static readonly IssueHtmlDashboardExporter _exporter = new();

    public SkillTaskResult Run(SkillContext ctx, SkillTaskDefinition taskDef)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var issues  = ExportIssueExcelReportTask.GetIssues(ctx);
            var report  = ExportIssueExcelReportTask.BuildReport(ctx, issues, taskDef);

            var outputFolder = SkillSettings.GetString(taskDef.Settings, "outputFolder");
            if (string.IsNullOrWhiteSpace(outputFolder)) outputFolder = ctx.OutputFolder;
            Directory.CreateDirectory(outputFolder);

            var includeJson = SkillSettings.GetBool(taskDef.Settings, "includeEmbeddedJson", true);
            var filePath    = _exporter.Export(report, outputFolder, includeEmbeddedJson: includeJson);
            ctx.SharedData["htmlReportPath"] = filePath;

            return new SkillTaskResult
            {
                TaskId    = Id, TaskName = Name, Success = true,
                Message   = $"HTML dashboard written to {filePath}",
                IssueCount = issues.Count, DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            return new SkillTaskResult
            {
                TaskId    = Id, TaskName = Name, Success = false,
                Message   = $"HTML export failed: {ex.Message}", DurationMs = sw.ElapsedMilliseconds
            };
        }
    }
}
