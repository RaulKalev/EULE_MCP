using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class GetCircuitsForSelectedElementsTool : IRevitMcpTool
{
    public string Name => "revit_get_circuits_for_selected_elements";
    public string Description => "Returns all electrical circuits for the currently selected Revit elements. De-duplicates circuits that contain multiple selected elements. Accepts: includeElements (bool, default true).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null) return Task.FromResult(Fail(request, "No active document."));
        var doc = uidoc.Document;

        var includeElements = ToolArguments.GetBool(request.Arguments, "includeElements", true);
        var selIds = uidoc.Selection.GetElementIds().ToList();

        if (selIds.Count == 0) return Task.FromResult(Fail(request, "No elements selected."));

        var circuits = new List<object>();
        var seenCircuits = new HashSet<long>();
        var uncircuited = new List<long>();

        foreach (var eid in selIds)
        {
            if (cancellationToken.IsCancellationRequested) break;
            var element = doc.GetElement(eid);
            if (element is not FamilyInstance fi || fi.MEPModel == null) { uncircuited.Add(eid.Value); continue; }

            bool found = false;
            try
            {
                foreach (ElectricalSystem sys in fi.MEPModel.ElectricalSystems)
                {
                    if (seenCircuits.Contains(sys.Id.Value)) continue;
                    seenCircuits.Add(sys.Id.Value);
                    var panel = CircuitDtoBuilder.TryGetPanel(sys);
                    List<object>? elements = null;
                    if (includeElements)
                    {
                        elements = new List<object>();
                        try
                        {
                            if (sys.Elements != null)
                                foreach (Element e in sys.Elements)
                                    elements.Add(new { elementId = e.Id.Value, category = e.Category?.Name ?? "", type = e.Name });
                        }
                        catch { }
                    }
                    circuits.Add(new
                    {
                        circuitId = sys.Id.Value,
                        circuitNumber = sys.CircuitNumber ?? "",
                        panelName = panel?.Name ?? "",
                        panelElementId = panel?.Id?.Value ?? 0L,
                        systemType = sys.SystemType.ToString(),
                        wireType = CircuitDtoBuilder.GetWireTypeName(doc, sys),
                        connectedElements = (object?)elements
                    });
                    found = true;
                }
            }
            catch { }
            if (!found) uncircuited.Add(eid.Value);
        }

        var warnings = uncircuited.Count > 0
            ? new List<string> { $"{uncircuited.Count} selected element(s) have no circuit: [{string.Join(", ", uncircuited)}]" }
            : new List<string>();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Found {circuits.Count} unique circuit(s) for {selIds.Count} selected element(s).",
            Data = new { selectedCount = selIds.Count, circuitCount = circuits.Count, uncircuitedCount = uncircuited.Count, circuits },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
