using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class SelectCircuitElementsTool : IRevitMcpTool
{
    public string Name => "revit_select_circuit_elements";
    public string Description => "Selects all elements connected to a circuit in the Revit UI. Requires approval. Accepts: circuitId (long, required), replaceSelection (bool, default true), zoomToSelection (bool, default false).";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null) return Task.FromResult(Fail(request, "No active document."));
        var doc = uidoc.Document;

        var circuitId = ToolArguments.GetLong(request.Arguments, "circuitId");
        var replaceSelection = ToolArguments.GetBool(request.Arguments, "replaceSelection", true);
        var zoomToSelection = ToolArguments.GetBool(request.Arguments, "zoomToSelection");

        if (circuitId <= 0) return Task.FromResult(Fail(request, "circuitId is required."));

        var circuit = doc.GetElement(new ElementId(circuitId)) as ElectricalSystem;
        if (circuit == null) return Task.FromResult(Fail(request, $"No electrical circuit found with ID {circuitId}."));

        var elementIds = new List<ElementId>();
        try
        {
            var es = circuit.Elements;
            if (es != null)
                foreach (Element e in es)
                    elementIds.Add(e.Id);
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail(request, $"Could not read circuit elements: {ex.Message}"));
        }

        if (elementIds.Count == 0)
            return Task.FromResult(Fail(request, "Circuit has no connected elements to select."));

        var existing = replaceSelection ? new List<ElementId>() : uidoc.Selection.GetElementIds().ToList();
        var merged = existing.Union(elementIds).ToList();
        uidoc.Selection.SetElementIds(merged);

        if (zoomToSelection)
            try { uidoc.ShowElements(merged); } catch { }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Selected {elementIds.Count} element(s) from circuit {circuit.CircuitNumber ?? circuitId.ToString()}.",
            Data = new
            {
                circuitId,
                circuitNumber = circuit.CircuitNumber ?? "",
                selectedCount = elementIds.Count,
                selectedElementIds = elementIds.Select(e => e.Value).ToList()
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
