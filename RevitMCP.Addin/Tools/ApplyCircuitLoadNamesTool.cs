using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class ApplyCircuitLoadNamesTool : IRevitMcpTool
{
    public string Name => "revit_apply_circuit_load_names";
    public string Description => "Applies load name changes to circuits. Requires approval. Accepts: changes (JSON array of {circuitId: long, newLoadName: string}). Runs inside a transaction. Returns modified and failed counts with old/new values.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

        var changesRaw = ToolArguments.GetString(request.Arguments, "changes");
        if (string.IsNullOrWhiteSpace(changesRaw))
            return Task.FromResult(Fail(request, "'changes' array is required (JSON: [{circuitId, newLoadName}])."));

        List<(long CircuitId, string NewLoadName)> changes;
        try
        {
            var arr = JArray.Parse(changesRaw);
            changes = arr.Select(item => (
                item["circuitId"]!.Value<long>(),
                item["newLoadName"]!.Value<string>()!
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

        cancellationToken.ThrowIfCancellationRequested();
        using (var tx = new Transaction(doc, "Revit MCP - Apply Circuit Load Names"))
        {
            tx.Start();
            foreach (var (circuitId, newName) in changes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var circuit = doc.GetElement(new ElementId(circuitId)) as ElectricalSystem;
                if (circuit == null) { failures.Add(new { circuitId, reason = "Circuit not found." }); continue; }
                try
                {
                    var param = circuit.LookupParameter("Load Name");
                    if (param == null || param.IsReadOnly)
                    {
                        failures.Add(new { circuitId, reason = "Load Name parameter not found or read-only." });
                        continue;
                    }
                    var old = param.AsString() ?? "";
                    param.Set(newName);
                    modified.Add(new { circuitId, oldLoadName = old, newLoadName = newName });
                }
                catch (Exception ex) { failures.Add(new { circuitId, reason = ex.Message }); }
            }
            RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(tx);
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Applied {modified.Count} load name change(s). {failures.Count} failed.",
            Data = new { modifiedCount = modified.Count, failedCount = failures.Count, modified, failures },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
