using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
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
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

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

        var sorted = sortBy.Equals("LoadName", StringComparison.OrdinalIgnoreCase)
            ? circuits.OrderBy(c =>
            {
                string ln = ""; try { ln = c.LookupParameter("Load Name")?.AsString() ?? ""; } catch { } return ln;
            }).ThenBy(c => c.CircuitNumber).ToList()
            : circuits.OrderBy(c => c.CircuitNumber).ToList();

        int num = startNumber;
        int changedCount = 0;
        var changes = new List<object>();
        foreach (var c in sorted)
        {
            var newNum = num.ToString();
            bool willChange = !string.Equals(c.CircuitNumber, newNum);
            if (willChange) changedCount++;
            changes.Add(new { circuitId = c.Id.Value, oldCircuitNumber = c.CircuitNumber ?? "", newCircuitNumber = newNum, willChange });
            num += increment;
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Previewed renumbering of {circuits.Count} circuit(s) on panel '{panelName}'. {changedCount} will change.",
            Data = new { panelName, circuitCount = circuits.Count, changedCount, changes },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
