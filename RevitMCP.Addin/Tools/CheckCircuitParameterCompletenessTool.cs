using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class CheckCircuitParameterCompletenessTool : IRevitMcpTool
{
    public string Name => "revit_check_circuit_parameter_completeness";
    public string Description => "Checks required parameters on electrical circuit elements. Returns per-parameter fill rates and circuit IDs with empty values. Accepts: panelName, systemType, requiredParameters (string[], default [Circuit Number, Load Name, Cable Type]), treatWhitespaceAsEmpty (bool, default true), includeCircuitIds (bool, default true), limit (default 1000).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

        var panelName = ToolArguments.GetString(request.Arguments, "panelName");
        var systemType = ToolArguments.GetString(request.Arguments, "systemType");
        var requiredParams = ToolArguments.GetStringArray(request.Arguments, "requiredParameters");
        var treatWsEmpty = ToolArguments.GetBool(request.Arguments, "treatWhitespaceAsEmpty", true);
        var includeCircuitIds = ToolArguments.GetBool(request.Arguments, "includeCircuitIds", true);
        var limit = ToolArguments.GetInt(request.Arguments, "limit", 1000);

        if (requiredParams.Length == 0)
            requiredParams = ["Circuit Number", "Load Name", "Cable Type"];

        var circuits = CircuitQueryService.GetAll(doc);
        if (!string.IsNullOrWhiteSpace(panelName) || !string.IsNullOrWhiteSpace(systemType))
            circuits = CircuitQueryService.Filter(circuits, panelName, null, systemType);
        var checked_ = circuits.Take(limit).ToList();

        var filledIds = requiredParams.ToDictionary(p => p, _ => new List<long>());
        var emptyIds = requiredParams.ToDictionary(p => p, _ => new List<long>());

        foreach (var circuit in checked_)
        {
            if (cancellationToken.IsCancellationRequested) break;
            foreach (var paramName in requiredParams)
            {
                string val = "";
                try
                {
                    var p = circuit.LookupParameter(paramName);
                    if (p != null)
                        val = p.StorageType == StorageType.ElementId
                            ? (p.AsElementId() != ElementId.InvalidElementId ? "set" : "")
                            : p.AsString() ?? p.AsValueString() ?? "";
                }
                catch { }

                bool isEmpty = treatWsEmpty ? string.IsNullOrWhiteSpace(val) : string.IsNullOrEmpty(val);
                (isEmpty ? emptyIds : filledIds)[paramName].Add(circuit.Id.Value);
            }
        }

        var results = requiredParams.Select(p => new
        {
            parameterName = p,
            total = checked_.Count,
            filledCount = filledIds[p].Count,
            emptyCount = emptyIds[p].Count,
            fillRate = checked_.Count > 0 ? $"{filledIds[p].Count * 100.0 / checked_.Count:F0}%" : "N/A",
            emptyCircuitIds = includeCircuitIds ? (object)emptyIds[p] : null
        }).ToList();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = results.Any(r => r.emptyCount > 0)
                ? $"Checked {checked_.Count} circuit(s). Some required parameters are missing."
                : $"Checked {checked_.Count} circuit(s). All required parameters are filled.",
            Data = new { checkedCount = checked_.Count, parameters = results },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
