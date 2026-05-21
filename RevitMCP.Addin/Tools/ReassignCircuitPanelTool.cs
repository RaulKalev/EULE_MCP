using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class ReassignCircuitPanelTool : IRevitMcpTool
{
    public string Name => "revit_reassign_circuit_panel";
    public string Description => "Reassigns an existing electrical circuit to another panel. Requires approval. Transaction-wrapped and reversible via Revit Undo.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Electrical;

    private readonly PanelResolver _panelResolver = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

        var circuitId = ToolArguments.GetLong(request.Arguments, "circuitId");
        if (circuitId == 0)
            return Task.FromResult(Fail(request, "circuitId is required."));

        var circuitElem = doc.GetElement(new ElementId(circuitId));
        if (circuitElem is not ElectricalSystem circuit)
            return Task.FromResult(Fail(request, $"No electrical circuit found with ID {circuitId}."));

        var panelElementId = ToolArguments.GetLong(request.Arguments, "targetPanelElementId");
        var panelName = ToolArguments.GetString(request.Arguments, "targetPanelName");

        var (panel, panelError) = _panelResolver.Resolve(doc, panelElementId, panelName);
        if (panel == null)
            return Task.FromResult(Fail(request, panelError));

        var currentPanel = CircuitDtoBuilder.TryGetPanel(circuit);
        var result = CircuitMutationService.ReassignPanel(doc, circuit, panel);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = result.Success,
            Message = result.Message,
            Data = result.Success ? new
            {
                circuitId,
                circuitNumber = circuit.CircuitNumber ?? "",
                oldPanel = result.OldPanel,
                newPanel = result.NewPanel
            } : (object?)null,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
