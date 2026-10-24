using System.Diagnostics;
using System.IO;
using System.Text;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.ModelPages.Hosting;
using RevitMCP.Addin.Usage;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Usage;

/// <summary>Runs the monthly report and serves it as a page; shared by the tool and the connector window.</summary>
public static class UsageReportService
{
    private const string StylesResourceName = "RevitMCP.Addin.ModelPages.Viewer.pages.css";

    public static (UsageReportResult Result, string? Url) Run(string? month, string? sharedFolder, string? format, string? client)
    {
        var settings = UsageSettings.Load();
        var result = UsageReportRunner.Run(settings, month, sharedFolder, format, DateTimeOffset.Now, Stylesheet());
        if (!result.Success || result.Data == null || result.HtmlPath == null) return (result, null);

        // Also served as a model page (bound to no model), so the tables get search, filters, sorting and grouping.
        var pages = ModelPageService.Current;
        if (pages == null) return (result, null);
        try
        {
            var info = pages.Store.Save(UsageReportWriter.PageId(result.Data.Month), UsageReportWriter.Title(result.Data.Month),
                UsageReportWriter.Html(result.Data), string.Empty, "Usage", client, DateTimeOffset.Now);
            var url = pages.UrlFor(info.Id, out var problem);
            if (url == null && problem != null) result.Warnings.Add(problem);
            return (result, url);
        }
        catch (ArgumentException ex)
        {
            result.Warnings.Add("The report could not be served as a page: " + ex.Message);
            return (result, null);
        }
    }

    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch { /* the paths are in the result */ }
    }

    private static string? Stylesheet()
    {
        try
        {
            using var stream = typeof(UsageReportService).Assembly.GetManifestResourceStream(StylesResourceName);
            if (stream == null) return null;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// The monthly usage report (docs/usage-reporting.md): a deterministic aggregation over the shared usage files — per
/// user, project, client, tool, read/write and outcome — written as HTML and Excel to the report folder and served as
/// a page. No Revit API; runs off the Revit thread.
/// Tool name: revit_usage_report
/// </summary>
public class UsageReportTool : IRevitMcpTool, IBackgroundMcpTool
{
    public string Name => "revit_usage_report";
    public string Description => "Monthly connector usage report from the shared usage folder (users, projects, clients, tools, outcomes, credits, estimated time saved). Args: month (yyyy-MM, default previous), sharedFolder, format (html|xlsx|both), open.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Reports;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var args = request.Arguments;
        var client = string.IsNullOrWhiteSpace(request.ClientName) ? null : request.ClientName;
        var (result, url) = UsageReportService.Run(ToolArguments.GetString(args, "month"), ToolArguments.GetString(args, "sharedFolder"),
            ToolArguments.GetString(args, "format", UsageReportRunner.FormatBoth), client);
        sw.Stop();

        if (!result.Success)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Status = "not_available", Message = result.Message, Warnings = result.Warnings, DurationMs = sw.ElapsedMilliseconds });

        var opened = false;
        if (ToolArguments.GetBool(args, "open", true) && (url ?? result.HtmlPath) is { } target)
        {
            UsageReportService.Open(target);
            opened = true;
        }

        var d = result.Data!;
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = result.Message,
            Data = new
            {
                month = d.Month,
                url,
                html = result.HtmlPath,
                xlsx = result.ExcelPath,
                opened,
                totals = new
                {
                    calls = d.Total.Calls,
                    users = d.ByUser.Count,
                    projects = d.ByProject.Count,
                    successRate = d.Total.SuccessRate,
                    writes = d.Total.Writes,
                    rejected = d.Total.Rejected,
                    affected = d.Total.Affected,
                    timeSavedHoursEstimate = Math.Round(d.Total.SavedSeconds / 3600, 1),
                    providerCost = d.Costs.Present ? d.Costs.Rows.Sum(r => r.Cost) : (decimal?)null
                },
                topTools = d.ByTool.Take(10).Select(t => new { tool = t.Key, calls = t.Calls, affected = t.Affected }).ToList(),
                note = "Aggregates only; time saved is an estimate from the baseline table in the company config."
            },
            Warnings = result.Warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}
