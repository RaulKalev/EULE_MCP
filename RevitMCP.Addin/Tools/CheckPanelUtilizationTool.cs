using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class CheckPanelUtilizationTool : IRevitMcpTool
{
    public string Name => "revit_check_panel_utilization";
    public string Description => "Checks circuit count, total apparent load, and data quality issues per panel. Accepts: panelName (optional — if empty checks all panels), systemType (optional), includeCircuitDetails (bool, default false), limit (default 5000).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

        var panelNameFilter = ToolArguments.GetString(request.Arguments, "panelName");
        var systemType = ToolArguments.GetString(request.Arguments, "systemType");
        var includeDetails = ToolArguments.GetBool(request.Arguments, "includeCircuitDetails");
        var limit = ToolArguments.GetInt(request.Arguments, "limit", 5000);

        var circuits = CircuitQueryService.GetAll(doc);
        if (!string.IsNullOrWhiteSpace(panelNameFilter) || !string.IsNullOrWhiteSpace(systemType))
            circuits = CircuitQueryService.Filter(circuits, panelNameFilter, null, systemType);

        var byPanel = new Dictionary<string, (long PanelId, List<ElectricalSystem> Circuits)>();
        foreach (var c in circuits.Take(limit))
        {
            if (cancellationToken.IsCancellationRequested) break;
            var panel = CircuitDtoBuilder.TryGetPanel(c);
            var key = panel?.Name ?? "(No Panel)";
            var panelId = panel?.Id?.Value ?? 0L;
            if (!byPanel.ContainsKey(key)) byPanel[key] = (panelId, new List<ElectricalSystem>());
            byPanel[key].Circuits.Add(c);
        }

        var panels = new List<object>();
        foreach (var (pName, (panelId, panelCircuits)) in byPanel.OrderBy(kv => kv.Key))
        {
            double totalLoad = 0;
            int missingCableType = 0, missingLoadName = 0, emptyCircNum = 0;
            var circuitDetails = includeDetails ? new List<object>() : null;

            foreach (var c in panelCircuits)
            {
                double load = 0; try { load = c.ApparentLoad; } catch { }
                totalLoad += load;
                var wt = CircuitDtoBuilder.GetWireTypeName(doc, c);
                if (string.IsNullOrEmpty(wt)) missingCableType++;
                string ln = ""; try { ln = c.LookupParameter("Load Name")?.AsString() ?? ""; } catch { }
                if (string.IsNullOrWhiteSpace(ln)) missingLoadName++;
                if (string.IsNullOrWhiteSpace(c.CircuitNumber)) emptyCircNum++;
                circuitDetails?.Add(new { circuitId = c.Id.Value, circuitNumber = c.CircuitNumber ?? "", apparentLoad = $"{load:F0} VA", wireType = wt });
            }

            panels.Add(new
            {
                panelName = pName,
                panelElementId = panelId,
                circuitCount = panelCircuits.Count,
                totalApparentLoad = $"{totalLoad:F0} VA",
                missingCableType,
                missingLoadName,
                emptyCircuitNumbers = emptyCircNum,
                circuits = (object?)circuitDetails
            });
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Checked {byPanel.Count} panel(s) with {circuits.Count} total circuit(s).",
            Data = new { panelCount = byPanel.Count, totalCircuits = circuits.Count, panels },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
