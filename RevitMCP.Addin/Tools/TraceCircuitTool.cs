using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class TraceCircuitTool : IRevitMcpTool
{
    public string Name => "revit_trace_circuit";
    public string Description => "Traces element(s) or a circuit back to their panel. Input modes: circuitId (direct circuit lookup), elementId (single element), or useSelection (current Revit selection — traces all selected elements). Returns circuit details: number, load name, system type, wire/cable type, apparent load, panel name, panel element ID, and optionally connected elements.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null)
            return Task.FromResult(Fail(request, "No active document."));
        var doc = uidoc.Document;

        var circuitId = ToolArguments.GetLong(request.Arguments, "circuitId");
        var elementId = ToolArguments.GetLong(request.Arguments, "elementId");
        var useSelection = ToolArguments.GetBool(request.Arguments, "useSelection");
        var includeElements = ToolArguments.GetBool(request.Arguments, "includeElements", true);

        // Mode 1: direct circuit ID
        if (circuitId > 0)
        {
            var circuit = doc.GetElement(new ElementId(circuitId)) as ElectricalSystem;
            if (circuit == null)
                return Task.FromResult(Fail(request, $"No electrical circuit found with ID {circuitId}."));
            sw.Stop();
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"Circuit {circuit.CircuitNumber ?? circuitId.ToString()} found.",
                Data = new { circuitCount = 1, circuits = new[] { BuildCircuitDto(doc, circuit, includeElements) } },
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        // Mode 2: single elementId or Mode 3: selection
        IEnumerable<ElementId> sourceIds;
        if (elementId > 0)
        {
            sourceIds = new[] { new ElementId(elementId) };
        }
        else if (useSelection)
        {
            sourceIds = uidoc.Selection.GetElementIds();
            if (!sourceIds.Any())
                return Task.FromResult(Fail(request, "No elements selected."));
        }
        else
        {
            return Task.FromResult(Fail(request, "Provide circuitId, elementId, or useSelection=true."));
        }

        var circuits = new List<object>();
        var seenCircuits = new HashSet<long>();
        var uncircuitedIds = new List<long>();

        foreach (var eid in sourceIds)
        {
            if (cancellationToken.IsCancellationRequested) break;
            var element = doc.GetElement(eid);
            if (element == null) continue;

            if (element is not FamilyInstance fi || fi.MEPModel == null)
            {
                uncircuitedIds.Add(eid.Value);
                continue;
            }

            bool foundCircuit = false;
            try
            {
                var elemCircuits = new FilteredElementCollector(doc)
                    .OfClass(typeof(ElectricalSystem)).Cast<ElectricalSystem>()
                    .Where(sys => sys.Elements != null && sys.Elements.Cast<Element>().Any(e => e.Id == fi.Id))
                    .ToList();
                foreach (var sys in elemCircuits)
                {
                    if (seenCircuits.Contains(sys.Id.Value)) continue;
                    seenCircuits.Add(sys.Id.Value);
                    circuits.Add(BuildCircuitDto(doc, sys, includeElements));
                    foundCircuit = true;
                }
            }
            catch { }

            if (!foundCircuit)
                uncircuitedIds.Add(eid.Value);
        }

        var warnings = new List<string>();
        if (uncircuitedIds.Count > 0)
            warnings.Add($"{uncircuitedIds.Count} element(s) not assigned to any circuit: [{string.Join(", ", uncircuitedIds)}]");

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = circuits.Count > 0
                ? $"Found {circuits.Count} circuit(s) for the specified element(s)."
                : "No circuits found — element(s) are not assigned to any electrical circuit.",
            Data = new { circuitCount = circuits.Count, uncircuitedElementCount = uncircuitedIds.Count, circuits },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static object BuildCircuitDto(Document doc, ElectricalSystem circuit, bool includeElements)
    {
        var panel = CircuitDtoBuilder.TryGetPanel(circuit);
        string loadName = ""; try { loadName = circuit.LookupParameter("Load Name")?.AsString() ?? ""; } catch { }
        double apparentLoad = 0; try { apparentLoad = circuit.ApparentLoad; } catch { }
        double voltage = 0; try { voltage = circuit.Voltage; } catch { }
        int poles = 0; try { poles = circuit.PolesNumber; } catch { }
        string wireType = CircuitDtoBuilder.GetWireTypeName(doc, circuit);

        List<object>? elements = null;
        if (includeElements)
        {
            elements = new List<object>();
            try
            {
                var es = circuit.Elements;
                if (es != null)
                    foreach (Element e in es)
                        elements.Add(new { elementId = e.Id.Value, category = e.Category?.Name ?? "", type = e.Name });
            }
            catch { }
        }

        return new
        {
            circuitId = circuit.Id.Value,
            circuitNumber = circuit.CircuitNumber ?? "",
            loadName,
            systemType = circuit.SystemType.ToString(),
            wireType,
            apparentLoad = $"{apparentLoad:F0} VA",
            voltage = $"{voltage:F0} V",
            poles,
            panelName = panel?.Name ?? "",
            panelElementId = panel?.Id?.Value ?? 0L,
            connectedElements = elements
        };
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
