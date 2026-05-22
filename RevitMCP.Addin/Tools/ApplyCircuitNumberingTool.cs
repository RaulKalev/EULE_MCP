using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class ApplyCircuitNumberingTool : IRevitMcpTool
{
    public string Name => "revit_apply_circuit_numbering";
    public string Description => "Applies circuit number changes. Requires approval. Accepts: changes (JSON array of {circuitId: long, newCircuitNumber: string}). Runs inside a transaction. Returns modified and failed counts with old/new values.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

        var changesRaw = ToolArguments.GetString(request.Arguments, "changes");
        if (string.IsNullOrWhiteSpace(changesRaw))
            return Task.FromResult(Fail(request, "'changes' array is required (JSON: [{circuitId, newCircuitNumber}])."));

        List<(long CircuitId, string NewNumber)> changes;
        try
        {
            var arr = JArray.Parse(changesRaw);
            changes = arr.Select(item => (
                item["circuitId"]!.Value<long>(),
                item["newCircuitNumber"]!.Value<string>()!
            )).ToList();
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail(request, $"Invalid changes format: {ex.Message}"));
        }

        if (changes.Count == 0)
            return Task.FromResult(Fail(request, "No changes provided."));

        var modified = new List<object>();
        var failures = new List<object>();

        using (var tx = new Transaction(doc, "Revit MCP - Apply Circuit Numbering"))
        {
            tx.Start();
            foreach (var (circuitId, newNum) in changes)
            {
                if (cancellationToken.IsCancellationRequested) break;
                var circuit = doc.GetElement(new ElementId(circuitId)) as ElectricalSystem;
                if (circuit == null) { failures.Add(new { circuitId, reason = "Circuit not found." }); continue; }
                try
                {
                    var param = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_NUMBER)
                             ?? circuit.LookupParameter("Circuit Number");
                    if (param == null || param.IsReadOnly)
                    {
                        failures.Add(new { circuitId, reason = "Circuit Number parameter not writable." });
                        continue;
                    }
                    var old = param.AsString() ?? "";
                    param.Set(newNum);
                    modified.Add(new { circuitId, oldCircuitNumber = old, newCircuitNumber = newNum });
                }
                catch (Exception ex) { failures.Add(new { circuitId, reason = ex.Message }); }
            }
            tx.Commit();
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Applied {modified.Count} circuit number change(s). {failures.Count} failed.",
            Data = new { modifiedCount = modified.Count, failedCount = failures.Count, modified, failures },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
