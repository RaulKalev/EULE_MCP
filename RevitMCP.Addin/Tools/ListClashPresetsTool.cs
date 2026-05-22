using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Coordination.Clash.Services;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class ListClashPresetsTool : IRevitMcpTool
{
    public string Name => "revit_list_clash_presets";
    public string Description => "Lists all available clash detection presets including their names, descriptions, and rule counts.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Coordination;

    private readonly ClashPresetService _service = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var file = _service.Load();
        var presets = file.Presets.Select(p => new
        {
            name = p.Name,
            description = p.Description,
            ruleCount = p.Rules.Count,
            rules = p.Rules.Select(r => new { r.Name, r.ClashType, r.Severity }).ToList()
        }).ToList();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Found {presets.Count} clash preset(s).",
            Data = new { presets, count = presets.Count },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}
