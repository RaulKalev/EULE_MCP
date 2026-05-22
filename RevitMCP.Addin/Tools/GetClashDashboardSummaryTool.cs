using System.Diagnostics;
using System.Text.Json;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Coordination.Clash.DTOs;
using RevitMCP.Addin.Coordination.Clash.Services;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class GetClashDashboardSummaryTool : IRevitMcpTool
{
    public string Name => "revit_get_clash_dashboard_summary";
    public string Description => "Returns a rich dashboard summary of clash results grouped by multiple dimensions simultaneously for a quick project health overview.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Coordination;

    private readonly ClashRunCacheService _cache = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var args = request.Arguments;
        var useLastRun = ToolArguments.GetBool(args, "useLastRun", true);
        var clashesJson = ToolArguments.GetString(args, "clashesJson", "");
        var groupByArr = ToolArguments.GetStringArray(args, "groupBy");
        var groupBy = groupByArr.Length > 0 ? groupByArr : null;

        ClashRunResultDto? run = null;
        if (useLastRun)
            run = _cache.Load();
        else if (!string.IsNullOrWhiteSpace(clashesJson))
            run = JsonSerializer.Deserialize<ClashRunResultDto>(clashesJson);

        if (run == null)
            return Task.FromResult(Fail(request, "No clash data found. Run a detection first or provide clashesJson."));

        var summary = ClashSummaryService.BuildSummary(run.Clashes, groupBy);
        summary["runId"] = run.RunId;
        summary["runAt"] = run.RunAt;
        summary["presetName"] = run.PresetName ?? "(none)";
        summary["totalClashesInRun"] = run.TotalClashes;
        summary["topClashes"] = run.Clashes
            .OrderByDescending(c => c.Severity switch
            {
                "Critical" => 4, "High" => 3, "Medium" => 2, _ => 1
            })
            .Take(5)
            .Select(c => new { c.ClashId, c.RuleName, c.ClashType, c.Severity, c.Status,
                src = $"{c.Source.Category} ({c.Source.Model})",
                tgt = $"{c.Target.Category} ({c.Target.Model})" })
            .ToList<object>();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Dashboard: {run.TotalClashes} clash(es) from run '{run.RunId}'.",
            Data = summary,
            Warnings = run.Warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
