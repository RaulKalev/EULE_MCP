using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class GetFireAlarmVoltageDropSummaryTool : IRevitMcpTool
{
    public string Name => "revit_get_fire_alarm_voltage_drop_summary";
    public string Description => "Returns preliminary loop resistance or voltage-drop estimates grouped by Ahela nr. AddressableLoops use loop resistance (Ω); ConventionalSounderLines use voltage-drop (V). Results are preliminary only. Accepts: panelName, loopParameterName (default 'Ahela nr.'), deviceTypeParameterName, sounderCurrentMilliAmps (default 50.0), sounderSupplyVoltage (default 24.0), minimumSounderVoltage (default 18.0), addressableLoopMaxResistanceOhm (default 120.0), fallbackResistanceOhmPerMeter (default 0.035), limit.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    private static readonly string Warning =
        "Values are preliminary and must be reviewed against actual manufacturer data and final cable routing.";

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

        var panelName        = ToolArguments.GetString(request.Arguments, "panelName");
        var loopParam        = ToolArguments.GetString(request.Arguments, "loopParameterName",       "Ahela nr.");
        var devTypeParam     = ToolArguments.GetString(request.Arguments, "deviceTypeParameterName", "ELENEA_Nimetus");
        var sounderCurrentmA = GetDouble(request.Arguments, "sounderCurrentMilliAmps",          50.0);
        var supplyVoltage    = GetDouble(request.Arguments, "sounderSupplyVoltage",             24.0);
        var minVoltage       = GetDouble(request.Arguments, "minimumSounderVoltage",            18.0);
        var maxLoopResOhm    = GetDouble(request.Arguments, "addressableLoopMaxResistanceOhm",  120.0);
        var fallbackResOhm   = GetDouble(request.Arguments, "fallbackResistanceOhmPerMeter",    0.035);
        var limit            = ToolArguments.GetInt(request.Arguments, "limit",                 5000);

        // Get fire alarm preset data
        var presetResult = FireAlarmCircuitPresetService.Run(
            doc, panelName, loopParam, "Seadme Nr.", devTypeParam, "Description",
            includeDeviceList: false, includeCircuitInfo: true,
            includeVoltageDrop: true, allowDeviceNumberXxx: true,
            sortBy: [], limit: limit, cancellationToken: cancellationToken);

        CableResistanceProfileService.EnsureDefaultFileExists();
        var profileCollection = CableResistanceProfileService.Load();

        var loopResults = new List<object>();

        foreach (var loop in presetResult.Loops)
        {
            if (cancellationToken.IsCancellationRequested) break;

            string cableType = loop.CircuitInfo?.CableType ?? "";
            double lengthMeters = loop.CircuitInfo?.CircuitLengthMeters ?? 0;
            var profile = CableResistanceProfileService.FindMatch(cableType, profileCollection);
            double resistanceOhmPerMeter = profile?.RoundTripOhmPerMeter ?? fallbackResOhm;

            if (loop.LoopKind == "AddressableLoop" || loop.LoopKind == "ModuleLoop")
            {
                double loopRes = Math.Round(lengthMeters * resistanceOhmPerMeter, 2);
                double pctOfLimit = maxLoopResOhm > 0 ? Math.Round(loopRes / maxLoopResOhm * 100, 1) : 0;
                string status = loopRes > maxLoopResOhm ? "Critical"
                    : (loopRes > maxLoopResOhm * 0.8 ? "Warning" : "Ok");

                loopResults.Add(new
                {
                    loopName = loop.LoopName,
                    loopKind = loop.LoopKind,
                    deviceCount = loop.DeviceCount,
                    cableType,
                    lengthMeters,
                    resistanceOhmPerMeter,
                    estimatedLoopResistanceOhm = loopRes,
                    limitOhm = maxLoopResOhm,
                    percentOfLimit = pctOfLimit,
                    status,
                    profileMatched = profile != null
                });
            }
            else if (loop.LoopKind == "ConventionalSounderLine")
            {
                double totalCurrentA = loop.DeviceCount * sounderCurrentmA / 1000.0;
                double voltageDrop = Math.Round(totalCurrentA * lengthMeters * resistanceOhmPerMeter, 2);
                double endVoltage = Math.Round(supplyVoltage - voltageDrop, 2);
                string status = endVoltage < minVoltage ? "Critical"
                    : (endVoltage < minVoltage + 2.0 ? "Warning" : "Ok");

                loopResults.Add(new
                {
                    loopName = loop.LoopName,
                    loopKind = loop.LoopKind,
                    deviceCount = loop.DeviceCount,
                    cableType,
                    lengthMeters,
                    deviceCurrentMilliAmps = sounderCurrentmA,
                    totalCurrentMilliAmps = Math.Round(totalCurrentA * 1000, 1),
                    resistanceOhmPerMeter,
                    estimatedVoltageDrop = voltageDrop,
                    estimatedEndVoltage = endVoltage,
                    minimumVoltage = minVoltage,
                    status,
                    profileMatched = profile != null
                });
            }
            else
            {
                loopResults.Add(new
                {
                    loopName = loop.LoopName,
                    loopKind = loop.LoopKind,
                    deviceCount = loop.DeviceCount,
                    cableType,
                    lengthMeters,
                    status = "Unknown",
                    note = "Loop kind is Unknown — cannot estimate resistance or voltage drop."
                });
            }
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = $"Fire alarm voltage-drop/resistance summary for {loopResults.Count} loop(s).",
            Data = new { panelName, loops = loopResults },
            Warnings   = [Warning, .. presetResult.Warnings],
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static double GetDouble(Dictionary<string, object?> args, string key, double def)
    {
        if (!args.TryGetValue(key, out var val)) return def;
        return val switch { double d => d, float f => f, Newtonsoft.Json.Linq.JValue jv => (double)jv, _ => def };
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
