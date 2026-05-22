using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class GetCircuitRouteAssumptionsTool : IRevitMcpTool
{
    public string Name => "revit_get_circuit_route_assumptions";
    public string Description => "Returns panel and connected element locations for a single circuit. Diagnostic step before estimating length. Locations are resolved via LocationPoint → LocationCurve midpoint → BoundingBox. Accepts: circuitId (required), includeConnectedElements (bool), includeLocations (bool).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    private static readonly string Disclaimer =
        "Estimated route data is preliminary and does not represent actual installed cable routing.";

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

        var circuitId = ToolArguments.GetLong(request.Arguments, "circuitId");
        if (circuitId == 0) return Task.FromResult(Fail(request, "circuitId is required."));

        var includeConnected = ToolArguments.GetBool(request.Arguments, "includeConnectedElements", true);
        var includeLocations = ToolArguments.GetBool(request.Arguments, "includeLocations", true);

        var circuit = doc.GetElement(new ElementId(circuitId)) as ElectricalSystem;
        if (circuit == null) return Task.FromResult(Fail(request, $"Circuit {circuitId} not found."));

        // Panel location
        var panel = CircuitDtoBuilder.TryGetPanel(circuit);
        object? panelDto = null;
        if (panel != null)
        {
            var loc = includeLocations ? CircuitLocationResolver.BuildLocationDto(panel) : (object?)null;
            panelDto = new
            {
                panelName = panel.Name,
                panelElementId = panel.Id.Value,
                location = loc
            };
        }

        // Circuit info
        string wireType = CircuitDtoBuilder.GetWireTypeName(doc, circuit);
        double voltage = 0, apparentLoad = 0;
        try { voltage = circuit.Voltage; } catch { }
        try { apparentLoad = circuit.ApparentLoad; } catch { }
        var circuitDto = new
        {
            circuitNumber = circuit.CircuitNumber ?? "",
            loadName = circuit.LookupParameter("Load Name")?.AsString() ?? "",
            systemType = circuit.SystemType.ToString(),
            voltageDisplay = $"{voltage:F0} V",
            apparentLoadDisplay = $"{apparentLoad:F0} VA",
            wireType,
            cableType = wireType
        };

        // Connected elements
        var elements = new List<object>();
        int missingLocation = 0;
        if (includeConnected)
        {
            try
            {
                if (circuit.Elements != null)
                {
                    foreach (Element e in circuit.Elements)
                    {
                        var typeId = e.GetTypeId();
                        var typeElem = typeId != null && typeId != ElementId.InvalidElementId ? doc.GetElement(typeId) : null;
                        object? locDto = null;
                        if (includeLocations)
                        {
                            var resolved = CircuitLocationResolver.Resolve(e);
                            if (resolved == null) missingLocation++;
                            locDto = CircuitLocationResolver.BuildLocationDto(e);
                        }
                        elements.Add(new
                        {
                            elementId = e.Id.Value,
                            category = e.Category?.Name ?? "",
                            family = e is FamilyInstance fi ? fi.Symbol?.Family?.Name ?? "" : "",
                            type = typeElem?.Name ?? e.Name,
                            location = locDto
                        });
                    }
                }
            }
            catch { }
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Route assumptions collected for circuit {circuitId}.",
            Data = new
            {
                circuitId,
                panel = panelDto,
                circuit = circuitDto,
                connectedElements = elements,
                connectedElementCount = elements.Count,
                elementsMissingLocation = missingLocation,
                availableLocationSources = CircuitLocationResolver.AvailableLocationSources,
                assumptions = new[]
                {
                    "Panel and element locations are model coordinates in meters.",
                    "No actual cable tray/conduit route was detected.",
                    "Length estimates will use geometric approximation."
                }
            },
            Warnings = [Disclaimer],
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
