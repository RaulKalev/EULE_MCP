using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Coordination.Clash.Services;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class GetPreviousClashTool : IRevitMcpTool
{
    public string Name => "revit_get_previous_clash";
    public string Description => "Navigates to the previous clash in the last run result. Returns the clash details and its position. Wraps around from the first clash to the last.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Coordination;

    private readonly ClashRunCacheService _cache = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var run = _cache.Load();
        if (run == null)
            return Task.FromResult(Fail(request, "No clash run in cache. Run a detection first."));
        if (run.Clashes.Count == 0)
            return Task.FromResult(new McpToolResult
            {
                RequestId  = request.RequestId,
                Success    = true,
                Message    = $"Last run for preset '{run.PresetName}' completed with 0 clashes — no results to navigate.",
                Data       = new { runId = run.RunId, presetName = run.PresetName, totalClashes = 0 },
                DurationMs = sw.ElapsedMilliseconds
            });

        var (clash, index, total) = _cache.GetPrevious(run);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Clash {index}/{total}: {clash!.ClashId} — {clash.Source.Category} vs {clash.Target.Category} ({clash.Severity}).",
            Data = new { clash, index, total, runId = run.RunId },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
