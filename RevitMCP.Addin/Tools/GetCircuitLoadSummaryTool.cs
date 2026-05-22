using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class GetCircuitLoadSummaryTool : IRevitMcpTool
{
    public string Name => "revit_get_circuit_load_summary";
    public string Description => "Summarizes circuit apparent loads grouped by Panel, SystemType, CableType, or WireType. Accepts: groupBy (string[], default [Panel, SystemType]), panelName, systemType, includeCircuitDetails (bool, default false), limit (default 5000).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

        var groupBy = ToolArguments.GetStringArray(request.Arguments, "groupBy");
        if (groupBy.Length == 0) groupBy = ["Panel", "SystemType"];
        var panelName = ToolArguments.GetString(request.Arguments, "panelName");
        var systemType = ToolArguments.GetString(request.Arguments, "systemType");
        var includeDetails = ToolArguments.GetBool(request.Arguments, "includeCircuitDetails");
        var limit = ToolArguments.GetInt(request.Arguments, "limit", 5000);

        var circuits = CircuitQueryService.GetAll(doc);
        if (!string.IsNullOrWhiteSpace(panelName) || !string.IsNullOrWhiteSpace(systemType))
            circuits = CircuitQueryService.Filter(circuits, panelName, null, systemType);
        var filtered = circuits.Take(limit).ToList();

        var rows = new List<(string Panel, string SysType, string WireType, double Load, long Id, string CircNum)>();
        foreach (var c in filtered)
        {
            if (cancellationToken.IsCancellationRequested) break;
            var panel = CircuitDtoBuilder.TryGetPanel(c);
            var wt = CircuitDtoBuilder.GetWireTypeName(doc, c);
            double load = 0; try { load = c.ApparentLoad; } catch { }
            rows.Add((panel?.Name ?? "(No Panel)", c.SystemType.ToString(), string.IsNullOrEmpty(wt) ? "(No Cable Type)" : wt, load, c.Id.Value, c.CircuitNumber ?? ""));
        }

        // Build composite group key
        string KeyFor((string Panel, string SysType, string WireType, double Load, long Id, string CircNum) row) =>
            string.Join(" | ", groupBy.Select(g => g.ToLowerInvariant() switch
            {
                "panel" => row.Panel,
                "systemtype" => row.SysType,
                "cabletype" or "wiretype" => row.WireType,
                _ => ""
            }));

        var groups = rows.GroupBy(KeyFor).OrderBy(g => g.Key).Select(g =>
        {
            var details = includeDetails
                ? (object)g.Select(r => new { circuitId = r.Id, circuitNumber = r.CircNum, apparentLoad = $"{r.Load:F0} VA" }).ToList()
                : null;
            return (object)new
            {
                key = g.Key,
                circuitCount = g.Count(),
                totalApparentLoad = $"{g.Sum(r => r.Load):F0} VA",
                circuits = details
            };
        }).ToList();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Load summary for {filtered.Count} circuit(s) across {groups.Count} group(s).",
            Data = new { totalCircuits = filtered.Count, groupCount = groups.Count, groupBy, groups },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
