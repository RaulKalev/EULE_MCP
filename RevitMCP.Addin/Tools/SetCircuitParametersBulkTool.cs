using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class SetCircuitParametersBulkTool : IRevitMcpTool
{
    public string Name => "revit_set_circuit_parameters_bulk";
    public string Description => "Sets multiple parameters on multiple circuits in a single transaction. Requires approval. Accepts: circuitIds (long[], optional), panelName (optional, used when circuitIds is empty), parameters (JSON array of {parameterName: string, value: string}), limit (default 1000). Supports String, Integer, Double, and ElementId storage types.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

        var circuitIds = ToolArguments.GetLongArray(request.Arguments, "circuitIds");
        var panelName = ToolArguments.GetString(request.Arguments, "panelName");
        var parametersRaw = ToolArguments.GetString(request.Arguments, "parameters");
        var limit = ToolArguments.GetInt(request.Arguments, "limit", 1000);

        if (string.IsNullOrWhiteSpace(parametersRaw))
            return Task.FromResult(Fail(request, "'parameters' array is required (JSON: [{parameterName, value}])."));

        List<(string Name, string Value)> paramChanges;
        try
        {
            var arr = JArray.Parse(parametersRaw);
            paramChanges = arr.Select(item => (
                item["parameterName"]!.Value<string>()!,
                item["value"]!.Value<string>()!
            )).ToList();
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail(request, $"Invalid parameters format: {ex.Message}"));
        }

        if (paramChanges.Count == 0)
            return Task.FromResult(Fail(request, "No parameters provided."));

        List<ElectricalSystem> circuits;
        if (circuitIds.Length > 0)
        {
            circuits = circuitIds
                .Select(id => doc.GetElement(new ElementId(id)) as ElectricalSystem)
                .Where(c => c != null)
                .ToList()!;
        }
        else if (!string.IsNullOrWhiteSpace(panelName))
        {
            circuits = CircuitQueryService.Filter(CircuitQueryService.GetAll(doc), panelName, null, null).Take(limit).ToList();
        }
        else
        {
            return Task.FromResult(Fail(request, "Provide circuitIds or panelName."));
        }

        if (circuits.Count == 0)
            return Task.FromResult(Fail(request, "No target circuits found."));

        var results = new List<object>();
        using (var tx = new Transaction(doc, "Revit MCP - Set Circuit Parameters Bulk"))
        {
            tx.Start();
            foreach (var circuit in circuits)
            {
                if (cancellationToken.IsCancellationRequested) break;
                var paramResults = new List<object>();
                foreach (var (paramName, value) in paramChanges)
                {
                    try
                    {
                        var param = circuit.LookupParameter(paramName);
                        if (param == null || param.IsReadOnly)
                        {
                            paramResults.Add(new { parameterName = paramName, success = false, reason = param == null ? "Not found." : "Read-only." });
                            continue;
                        }
                        bool set = param.StorageType == StorageType.ElementId
                            ? SetElementId(doc, param, value)
                            : param.StorageType switch
                            {
                                StorageType.String => SetString(param, value),
                                StorageType.Integer => SetInt(param, value),
                                StorageType.Double => SetDouble(param, value),
                                _ => false
                            };
                        paramResults.Add(new { parameterName = paramName, success = set, reason = set ? null : (object)"Unsupported storage type or invalid value." });
                    }
                    catch (Exception ex) { paramResults.Add(new { parameterName = paramName, success = false, reason = (object)ex.Message }); }
                }
                results.Add(new { circuitId = circuit.Id.Value, circuitNumber = circuit.CircuitNumber ?? "", parameters = paramResults });
            }
            tx.Commit();
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Applied bulk parameter changes to {results.Count} circuit(s).",
            Data = new { circuitCount = results.Count, results },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static bool SetString(Parameter p, string v) { p.Set(v); return true; }
    private static bool SetInt(Parameter p, string v) { if (!int.TryParse(v, out var i)) return false; p.Set(i); return true; }
    private static bool SetDouble(Parameter p, string v) { if (!double.TryParse(v, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d)) return false; p.Set(d); return true; }

    private static bool SetElementId(Document doc, Parameter p, string value)
    {
        if (long.TryParse(value, out var numId))
        {
            if (doc.GetElement(new ElementId(numId)) == null) return false;
            p.Set(new ElementId(numId)); return true;
        }
        var typeMatch = new FilteredElementCollector(doc).WhereElementIsElementType()
            .FirstOrDefault(e => string.Equals(e.Name, value, StringComparison.OrdinalIgnoreCase));
        if (typeMatch != null) { p.Set(typeMatch.Id); return true; }
        var instMatch = new FilteredElementCollector(doc).WhereElementIsNotElementType()
            .FirstOrDefault(e => string.Equals(e.Name, value, StringComparison.OrdinalIgnoreCase));
        if (instMatch != null) { p.Set(instMatch.Id); return true; }
        return false;
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
