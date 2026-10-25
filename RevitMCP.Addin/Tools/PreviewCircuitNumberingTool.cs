using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class PreviewCircuitNumberingTool : IRevitMcpTool
{
    public string Name => "revit_preview_circuit_numbering";
    public string Description => "Previews renumbering proposals for panel circuits without modifying the model. Returns old/new circuit number pairs and a willChange flag per circuit. Accepts: panelName (required), startNumber (int, default 1), increment (int, default 1), systemType (filter), sortBy (LoadName|CurrentCircuitNumber, default CurrentCircuitNumber).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var documentTarget = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (documentTarget == null) return Task.FromResult(documentFailure!);
        var doc = documentTarget.Document;

        var panelName = ToolArguments.GetString(request.Arguments, "panelName");
        var startNumber = ToolArguments.GetInt(request.Arguments, "startNumber", 1);
        var increment = ToolArguments.GetInt(request.Arguments, "increment", 1);
        var systemType = ToolArguments.GetString(request.Arguments, "systemType");
        var sortBy = ToolArguments.GetString(request.Arguments, "sortBy", "CurrentCircuitNumber");

        if (string.IsNullOrWhiteSpace(panelName))
            return Task.FromResult(Fail(request, "panelName is required."));
        if (increment <= 0) increment = 1;

        var circuits = CircuitQueryService.Filter(CircuitQueryService.GetAll(doc), panelName, null, systemType);
        if (circuits.Count == 0)
            return Task.FromResult(Fail(request, $"No circuits found for panel '{panelName}'."));

        // Circuit numbers sort naturally (1, 2, 10 — not 1, 10, 2), or a renumbering would scramble the panel.
        // The apply step writes exactly the pairs this preview returns, so this is the only ordering.
        var sorted = sortBy.Equals("LoadName", StringComparison.OrdinalIgnoreCase)
            ? circuits.OrderBy(c =>
            {
                string ln = ""; try { ln = c.LookupParameter("Load Name")?.AsString() ?? ""; } catch { } return ln;
            }).ThenBy(c => c.CircuitNumber, CircuitNumberComparer.Instance).ThenBy(c => c.Id.Value).ToList()
            : circuits.OrderBy(c => c.CircuitNumber, CircuitNumberComparer.Instance).ThenBy(c => c.Id.Value).ToList();

        int num = startNumber;
        int changedCount = 0;
        int notWritable = 0;
        var changes = new List<object>();
        foreach (var c in sorted)
        {
            var newNum = num.ToString();
            // The apply step writes RBS_ELEC_CIRCUIT_NUMBER; Revit keeps it read-only for some circuits
            // (e.g. by system type or panel naming), so say so here instead of promising a change.
            bool writable;
            try
            {
                var p = c.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_NUMBER);
                writable = p != null && !p.IsReadOnly;
            }
            catch { writable = false; }
            bool willChange = writable && !string.Equals(c.CircuitNumber, newNum);
            if (willChange) changedCount++;
            if (!writable) notWritable++;
            changes.Add(new
            {
                circuitId = c.Id.Value, oldCircuitNumber = c.CircuitNumber ?? "", newCircuitNumber = newNum, willChange,
                reason = writable ? null : "Circuit Number is read-only for this circuit in Revit."
            });
            num += increment;
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Previewed renumbering of {circuits.Count} circuit(s) on panel '{panelName}'. {changedCount} will change." +
                      (notWritable > 0 ? $" {notWritable} have a read-only Circuit Number and cannot be renumbered." : ""),
            Data = new { panelName, circuitCount = circuits.Count, changedCount, notWritableCount = notWritable, changes },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
