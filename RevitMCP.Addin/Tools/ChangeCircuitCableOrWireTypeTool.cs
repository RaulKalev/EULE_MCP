using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class ChangeCircuitCableOrWireTypeTool : IRevitMcpTool
{
    public string Name => "revit_change_circuit_cable_or_wire_type";
    public string Description => "Changes the cable type and/or wire type of an electrical circuit. Prefers cable type; falls back to wire type. Requires approval. Transaction-wrapped and reversible via Revit Undo.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

        var circuitId = ToolArguments.GetInt(request.Arguments, "circuitId", 0);
        if (circuitId == 0)
            return Task.FromResult(Fail(request, "circuitId is required."));

        var circuitElem = doc.GetElement(new ElementId((long)circuitId));
        if (circuitElem is not ElectricalSystem circuit)
            return Task.FromResult(Fail(request, $"No electrical circuit found with ID {circuitId}."));

        var cableTypeName = ToolArguments.GetString(request.Arguments, "cableTypeName");
        var wireTypeName = ToolArguments.GetString(request.Arguments, "wireTypeName");
        var preferCableType = ToolArguments.GetBool(request.Arguments, "preferCableType", true);
        var fallbackToWireType = ToolArguments.GetBool(request.Arguments, "fallbackToWireType", true);

        if (string.IsNullOrWhiteSpace(cableTypeName) && string.IsNullOrWhiteSpace(wireTypeName))
            return Task.FromResult(Fail(request, "Provide cableTypeName and/or wireTypeName."));

        string currentWireType = CircuitDtoBuilder.GetWireTypeName(doc, circuit);
        var warnings = new List<string>();

        // Determine which name to resolve: prefer cableTypeName, then wireTypeName
        string targetTypeName = preferCableType && !string.IsNullOrWhiteSpace(cableTypeName)
            ? cableTypeName
            : wireTypeName;

        // If preferCableType is set but no cable types exist, try wire type name
        if (preferCableType && !string.IsNullOrWhiteSpace(cableTypeName))
        {
            var (wt, wtError) = WireTypeResolver.Resolve(doc, cableTypeName);
            if (wt == null)
            {
                if (fallbackToWireType && !string.IsNullOrWhiteSpace(wireTypeName))
                {
                    warnings.Add($"Cable type '{cableTypeName}' not found as a Revit WireType. Falling back to wireTypeName.");
                    targetTypeName = wireTypeName;
                }
                else
                {
                    warnings.Add($"'{cableTypeName}' not found. Available wire types: use revit_get_available_wire_types.");
                    return Task.FromResult(Fail(request, wtError));
                }
            }
        }

        var (resolvedType, resolveError) = WireTypeResolver.Resolve(doc, targetTypeName);
        if (resolvedType == null)
            return Task.FromResult(Fail(request, resolveError));

        var result = CircuitMutationService.ChangeWireType(doc, circuit, resolvedType);
        warnings.AddRange(result.Warnings);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = result.Success,
            Message = result.Message,
            Data = result.Success ? new
            {
                circuitId,
                oldWireType = result.OldWireType,
                newWireType = result.NewWireType
            } : (object?)null,
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
