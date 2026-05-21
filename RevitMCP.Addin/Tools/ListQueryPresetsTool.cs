using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Presets;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class ListQueryPresetsTool : IRevitMcpTool
{
    public string Name => "revit_list_query_presets";
    public string Description => "Lists available reusable query presets from the presets configuration file.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Reports;

    private static readonly QueryPresetStore _store = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        var file = _store.Load();
        var presets = file.Presets.Select(p => new
        {
            name = p.Name,
            description = p.Description,
            category = p.Category,
            parameters = p.Parameters,
            groupByCount = p.GroupBy.Count,
            filterCount = p.Filters.Count,
            defaultOutputMode = p.DefaultOutputMode
        }).ToList();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Found {presets.Count} query preset(s).",
            Data = new
            {
                presetCount = presets.Count,
                presets
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}
