using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class GetElectricalCircuitsTool : IRevitMcpTool
{
    public string Name => "revit_get_electrical_circuits";
    public string Description => "Lists electrical circuits (systems) in the active document with optional filters by panel name, circuit number, and system type.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

        var panelName = ToolArguments.GetString(request.Arguments, "panelName");
        var circuitNumber = ToolArguments.GetString(request.Arguments, "circuitNumber");
        var systemType = ToolArguments.GetString(request.Arguments, "systemType");
        var includeElements = ToolArguments.GetBool(request.Arguments, "includeElements", true);
        var includeParameters = ToolArguments.GetBool(request.Arguments, "includeParameters");
        var limit = ToolArguments.GetInt(request.Arguments, "limit", 500);

        var all = CircuitQueryService.GetAll(doc);
        var filtered = CircuitQueryService.Filter(all, panelName, circuitNumber, systemType);
        int total = filtered.Count;

        var circuits = filtered
            .Take(limit)
            .Select(c => CircuitDtoBuilder.Build(doc, c, includeElements, includeParameters))
            .ToList();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Returned {circuits.Count} of {total} electrical circuit(s).",
            Data = new { totalCircuits = total, returned = circuits.Count, circuits },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
