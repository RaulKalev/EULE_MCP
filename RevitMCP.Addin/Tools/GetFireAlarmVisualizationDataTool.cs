using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class GetFireAlarmVisualizationDataTool : IRevitMcpTool
{
    public string Name => "revit_get_fire_alarm_visualization_data";
    public string Description => "Returns structured fire alarm circuit data suitable for diagram rendering and spatial visualization, grouped by Ahela nr. Each loop node contains devices with element IDs, levels, and model coordinates (meters). Accepts: panelName, loopParameterName (default 'Ahela nr.'), deviceTypeParameterName, limit.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

        var panelName    = ToolArguments.GetString(request.Arguments, "panelName");
        var loopParam    = ToolArguments.GetString(request.Arguments, "loopParameterName",       "Ahela nr.");
        var devTypeParam = ToolArguments.GetString(request.Arguments, "deviceTypeParameterName", "ELENEA_Nimetus");
        var limit        = ToolArguments.GetInt(request.Arguments,    "limit",                   5000);

        var presetResult = FireAlarmCircuitPresetService.Run(
            doc, panelName, loopParam, "Seadme Nr.", devTypeParam, "Description",
            includeDeviceList: true, includeCircuitInfo: true,
            includeVoltageDrop: false, allowDeviceNumberXxx: true,
            sortBy: [], limit: limit, cancellationToken: cancellationToken);

        var nodes = presetResult.Loops.Select(l => (object)new
        {
            id        = l.LoopName,
            label     = l.LoopName,
            kind      = l.LoopKind,
            deviceCount = l.DeviceCount,
            circuitId = l.CircuitInfo?.RevitCircuitId,
            circuitNumber = l.CircuitInfo?.RevitCircuitNumber,
            panelN    = l.CircuitInfo?.PanelName,
            cableType = l.CircuitInfo?.CableType,
            lengthMeters = l.CircuitInfo?.CircuitLengthMeters,
            levels    = l.Levels.Select(lv => lv.LevelName).ToList(),
            devices   = l.Devices.Select(d => (object)new
            {
                id      = d.ElementId,
                number  = d.DeviceNumber,
                type    = d.DeviceType,
                desc    = d.Description,
                level   = d.Level
            }).ToList<object>()
        }).ToList<object>();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = $"Visualization data for {nodes.Count} loops/lines.",
            Data = new { panelName, loopCount = nodes.Count, nodes },
            Warnings   = presetResult.Warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
