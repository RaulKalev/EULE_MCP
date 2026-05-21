using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class GetCircuitInfoTool : IRevitMcpTool
{
    public string Name => "revit_get_circuit_info";
    public string Description => "Returns detailed information for one electrical circuit by element ID.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

        var circuitId = ToolArguments.GetLong(request.Arguments, "circuitId");
        if (circuitId == 0)
            return Task.FromResult(Fail(request, "circuitId is required."));

        var element = doc.GetElement(new ElementId(circuitId));
        if (element is not ElectricalSystem circuit)
            return Task.FromResult(Fail(request, $"No electrical circuit found with ID {circuitId}."));

        var includeElements = ToolArguments.GetBool(request.Arguments, "includeElements", true);
        var includeCircuitParameters = ToolArguments.GetBool(request.Arguments, "includeCircuitParameters", true);

        var dto = CircuitDtoBuilder.Build(doc, circuit, includeElements, includeCircuitParameters);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Circuit info for ID {circuitId}.",
            Data = new { circuit = dto },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
