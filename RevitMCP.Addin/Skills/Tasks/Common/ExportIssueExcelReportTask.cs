using System.Diagnostics;
using System.IO;
using RevitMCP.Addin.Reports;
using RevitMCP.Addin.Skills;
using RevitMCP.Addin.Skills.Models;
using RevitMCP.Core.Models.Issues;

namespace RevitMCP.Addin.Skills.Tasks.Common;

/// <summary>
/// Task ID: common.export.excel-report
/// Reads the aggregated <see cref="SkillIssue"/> list from SharedData["issues"]
/// and exports it as a formatted Excel report using <see cref="IssueExcelExporter"/>.
/// Stores the output path in SharedData["excelReportPath"].
/// </summary>
internal sealed class ExportIssueExcelReportTask : ISkillTask
{
    public string Id           => "common.export.excel-report";
    public string Name         => "Export Excel Issue Report";
    public bool   ChangesModel => false;

    private static readonly IssueExcelExporter _exporter = new();

    public SkillTaskResult Run(SkillContext ctx, SkillTaskDefinition taskDef)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var issues  = GetIssues(ctx);
            var report  = BuildReport(ctx, issues, taskDef);

            var outputFolder = SkillSettings.GetString(taskDef.Settings, "outputFolder");
            if (string.IsNullOrWhiteSpace(outputFolder)) outputFolder = ctx.OutputFolder;
            Directory.CreateDirectory(outputFolder);

            var filePath = _exporter.Export(report, outputFolder);
            ctx.SharedData["excelReportPath"] = filePath;

            return new SkillTaskResult
            {
                TaskId    = Id, TaskName = Name, Success = true,
                Message   = $"Excel report written to {filePath}",
                IssueCount = issues.Count, DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            return new SkillTaskResult
            {
                TaskId    = Id, TaskName = Name, Success = false,
                Message   = $"Excel export failed: {ex.Message}", DurationMs = sw.ElapsedMilliseconds
            };
        }
    }

    internal static List<SkillIssue> GetIssues(SkillContext ctx) =>
        ctx.SharedData.TryGetValue("issues", out var v) && v is List<SkillIssue> list ? list : new List<SkillIssue>();

    internal static IssueReportDto BuildReport(SkillContext ctx, List<SkillIssue> issues, SkillTaskDefinition taskDef)
    {
        var title = SkillSettings.GetString(taskDef.Settings, "reportTitle");
        if (string.IsNullOrWhiteSpace(title)) title = $"{ctx.SkillId} — {ctx.ProjectName}";

        // If SharedData contains native IssueDto list (e.g. from delivery/coordination tasks), use that directly
        List<IssueDto> issueDtos;
        if (ctx.SharedData.TryGetValue("issueDtos", out var raw) && raw is List<IssueDto> directList)
        {
            issueDtos = directList;
        }
        else
        {
            issueDtos = issues.Select(si => SkillIssueToDto(si, ctx.SkillId)).ToList();
        }

        return new IssueReportDto
        {
            RunId       = ctx.RunId,
            Title       = title,
            ModelTitle  = ctx.ProjectName,
            SourceTools = new List<string> { ctx.SkillId },
            CreatedAt   = DateTime.UtcNow,
            Issues      = issueDtos
        };
    }

    internal static IssueDto SkillIssueToDto(SkillIssue si, string sourceTool) => new()
    {
        IssueId      = si.Id,
        SourceTool   = sourceTool,
        RunId        = Guid.NewGuid().ToString("N")[..8],
        Severity     = ParseSeverity(si.Severity),
        Status       = IssueStatus.Open,
        Category     = si.Category,
        Title        = si.Category,
        Description  = si.Description,
        SuggestedFix = si.SuggestedAction,
        ElementId    = si.HostElementId,
        LinkedElementId = si.LinkedElementId,
        CreatedAt    = DateTime.UtcNow
    };

    private static IssueSeverity ParseSeverity(string s) => s?.ToLowerInvariant() switch
    {
        "critical" or "kriitiline" => IssueSeverity.Critical,
        "error"    or "viga"       => IssueSeverity.Error,
        "warning"  or "hoiatus"    => IssueSeverity.Warning,
        _                          => IssueSeverity.Info
    };
}
