using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class GetVoltageDropPrecheckTool : IRevitMcpTool
{
    public string Name => "revit_get_voltage_drop_precheck";
    public string Description => "Reports whether circuits have enough data for voltage-drop calculation. Checks voltage, load, cable type, wire type, and estimated length availability. Does not calculate voltage drop. Accepts: circuitIds (long[], preferred) or circuitId (single long), requireCableType (bool), requireVoltage (bool), requireLoad (bool), requireLength (bool).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    private static readonly string Warning =
        "This precheck only verifies data availability. It does not validate cable sizing or voltage drop compliance.";

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

        // Accept circuitIds (array) or single circuitId for backward compatibility
        var ids = ToolArguments.GetLongArray(request.Arguments, "circuitIds");
        if (ids.Length == 0)
        {
            var single = ToolArguments.GetLong(request.Arguments, "circuitId");
            if (single != 0) ids = [single];
        }
        if (ids.Length == 0) return Task.FromResult(Fail(request, "circuitIds (array) or circuitId (single) is required."));

        bool requireCableType = ToolArguments.GetBool(request.Arguments, "requireCableType", true);
        bool requireVoltage   = ToolArguments.GetBool(request.Arguments, "requireVoltage",   true);
        bool requireLoad      = ToolArguments.GetBool(request.Arguments, "requireLoad",      true);
        bool requireLength    = ToolArguments.GetBool(request.Arguments, "requireLength",     true);

        var results = new List<object>();
        int readyCount = 0, notReadyCount = 0;

        foreach (var circuitId in ids)
        {
            var circuit = doc.GetElement(new ElementId(circuitId)) as ElectricalSystem;
            if (circuit == null)
            {
                results.Add(new { circuitId, error = $"Circuit {circuitId} not found.", readyForVoltageDropCalculation = false });
                notReadyCount++;
                continue;
            }

            // Voltage
            double voltage = 0; bool hasVoltage = false;
            try { voltage = circuit.Voltage; hasVoltage = voltage > 0; } catch { }

            // Load
            double load = 0; bool hasLoad = false;
            try { load = circuit.ApparentLoad; hasLoad = load > 0; } catch { }

            // Cable/wire type
            var wireType = CircuitDtoBuilder.GetWireTypeName(doc, circuit);
            bool hasCableType = false;
#if !REVIT2024
            try { var id = circuit.CableType; hasCableType = id != null && id != ElementId.InvalidElementId; } catch { }
#endif
            bool hasWireType = !string.IsNullOrEmpty(wireType);

            // Panel location
            var panel = CircuitDtoBuilder.TryGetPanel(circuit);
            bool hasPanelLocation = panel != null && CircuitLocationResolver.Resolve(panel) != null;

            // Element locations
            int elemCount = 0, elemWithLoc = 0;
            try
            {
                if (circuit.Elements != null)
                    foreach (Element e in circuit.Elements)
                    {
                        elemCount++;
                        if (CircuitLocationResolver.Resolve(e) != null) elemWithLoc++;
                    }
            }
            catch { }
            bool hasElementLocations = elemWithLoc > 0;
            bool canEstimateLength = hasPanelLocation && hasElementLocations;

            var missing = new List<string>();
            var recommendations = new List<string>();
            bool isReady = true;

            if (requireVoltage && !hasVoltage)      { missing.Add("Voltage"); recommendations.Add("Assign a voltage to the circuit system type."); isReady = false; }
            if (requireLoad && !hasLoad)            { missing.Add("Load");    recommendations.Add("Assign an apparent load to the circuit."); isReady = false; }
            if (requireCableType && !hasCableType)  { missing.Add("Cable Type"); recommendations.Add("Assign Cable Type or confirm Wire Type can be used as calculation input."); isReady = false; }
            if (requireLength && !canEstimateLength)
            {
                missing.Add("Estimated Length");
                if (!hasPanelLocation) recommendations.Add("Panel location is unavailable — ensure panel has a valid location.");
                if (!hasElementLocations) recommendations.Add("No connected element locations found — ensure elements are placed in the model.");
                isReady = false;
            }

            if (isReady) readyCount++; else notReadyCount++;

            results.Add(new
            {
                circuitId,
                readyForVoltageDropCalculation = isReady,
                available = new
                {
                    voltage = hasVoltage,
                    load = hasLoad,
                    cableType = hasCableType,
                    wireType = hasWireType,
                    estimatedLength = canEstimateLength,
                    panelLocation = hasPanelLocation,
                    elementLocations = hasElementLocations,
                    connectedElementCount = elemCount,
                    elementsWithLocation = elemWithLoc
                },
                missing,
                recommendations
            });
        }

        sw.Stop();
        // Return single object when only one circuit was checked (backward compat), array otherwise
        object data = results.Count == 1 ? results[0] : (object)new { circuits = results, readyCount, notReadyCount, totalChecked = ids.Length };

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = results.Count == 1
                ? (notReadyCount == 0 ? $"Circuit {ids[0]} has sufficient data for voltage-drop calculation." : $"Circuit {ids[0]} is missing required data.")
                : $"Precheck complete: {readyCount} ready, {notReadyCount} not ready (of {ids.Length} checked).",
            Data = data,
            Warnings = [Warning],
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
