using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class RunFireAlarmCircuitPresetTool : IRevitMcpTool
{
    public string Name => "revit_run_fire_alarm_circuit_preset";
    public string Description => "Predefined Fire Alarm Devices workflow: collects all fire alarm devices, groups by Ahela nr. (or custom loop parameter), reads Seadme Nr. and device type name, resolves connected circuits, and classifies each loop (AddressableLoop, ConventionalSounderLine, ModuleLoop, Unknown). Accepts: panelName, loopParameterName (default 'Ahela nr.'), deviceNumberParameterName (default 'Seadme Nr.'), deviceTypeParameterName (default 'ELENEA_Nimetus'), descriptionParameterName (default 'Description'), includeDeviceList (bool), includeCircuitInfo (bool), allowDeviceNumberXXX (bool, default true), limit (int).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

        var panelName        = ToolArguments.GetString(request.Arguments, "panelName");
        var loopParam        = ToolArguments.GetString(request.Arguments, "loopParameterName",         "Ahela nr.");
        var devNumParam      = ToolArguments.GetString(request.Arguments, "deviceNumberParameterName", "Seadme Nr.");
        var devTypeParam     = ToolArguments.GetString(request.Arguments, "deviceTypeParameterName",   "ELENEA_Nimetus");
        var descParam        = ToolArguments.GetString(request.Arguments, "descriptionParameterName",  "Description");
        var includeDevices   = ToolArguments.GetBool(request.Arguments,   "includeDeviceList",         true);
        var includeCircuits  = ToolArguments.GetBool(request.Arguments,   "includeCircuitInfo",        true);
        var allowXxx         = ToolArguments.GetBool(request.Arguments,   "allowDeviceNumberXXX",      true);
        var limit            = ToolArguments.GetInt(request.Arguments,    "limit",                     5000);

        var result = FireAlarmCircuitPresetService.Run(
            doc, panelName, loopParam, devNumParam, devTypeParam, descParam,
            includeDevices, includeCircuits,
            includeVoltageDrop: false, // voltage drop handled by separate tool
            allowDeviceNumberXxx: allowXxx,
            sortBy: [], limit: limit, cancellationToken: cancellationToken);

        // Serialize loops to anonymous objects for the response
        var loops = result.Loops.Select(l => (object)new
        {
            loopName          = l.LoopName,
            loopKind          = l.LoopKind,
            deviceCount       = l.DeviceCount,
            levels            = l.Levels.Select(lv => new { levelName = lv.LevelName, deviceCount = lv.DeviceCount }).ToList<object>(),
            deviceTypeSummary = l.DeviceTypeSummary.Select(ts => new { deviceType = ts.DeviceType, count = ts.Count }).ToList<object>(),
            circuitInfo = l.CircuitInfo == null ? null : (object)new
            {
                revitCircuitId     = l.CircuitInfo.RevitCircuitId,
                revitCircuitNumber = l.CircuitInfo.RevitCircuitNumber,
                panelName          = l.CircuitInfo.PanelName,
                cableType          = l.CircuitInfo.CableType,
                wireType           = l.CircuitInfo.WireType,
                circuitLengthMeters = l.CircuitInfo.CircuitLengthMeters
            },
            devices = includeDevices
                ? l.Devices.Select(d => (object)new
                {
                    elementId      = d.ElementId,
                    level          = d.Level,
                    deviceNumber   = d.DeviceNumber,
                    loopNumber     = d.LoopNumber,
                    deviceType     = d.DeviceType,
                    description    = d.Description,
                    revitCircuitId = d.RevitCircuitId
                }).ToList<object>()
                : null
        }).ToList<object>();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = $"Fire alarm circuit preset completed. Found {result.DeviceCount} devices across {result.LoopCount} loops/lines.",
            Data = new
            {
                panelName   = result.PanelName,
                deviceCount = result.DeviceCount,
                loopCount   = result.LoopCount,
                loops
            },
            Warnings   = result.Warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
