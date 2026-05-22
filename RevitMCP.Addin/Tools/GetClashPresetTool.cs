using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Coordination.Clash.Services;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class GetClashPresetTool : IRevitMcpTool
{
    public string Name => "revit_get_clash_preset";
    public string Description => "Returns the full definition of a named clash detection preset including all rules and their parameters.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Coordination;

    private readonly ClashPresetService _service = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var presetName = ToolArguments.GetString(request.Arguments, "presetName");
        if (string.IsNullOrWhiteSpace(presetName))
            return Task.FromResult(Fail(request, "presetName is required."));

        var preset = _service.FindByName(presetName);
        if (preset == null)
            return Task.FromResult(Fail(request, $"Preset '{presetName}' not found."));

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Preset '{preset.Name}' has {preset.Rules.Count} rule(s).",
            Data = preset,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
