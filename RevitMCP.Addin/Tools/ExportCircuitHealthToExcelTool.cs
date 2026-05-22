using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using ClosedXML.Excel;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Export;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class ExportCircuitHealthToExcelTool : IRevitMcpTool
{
    public string Name => "revit_export_circuit_health_to_excel";
    public string Description => "Exports circuit QA health issues to a formatted .xlsx file with Summary and Health Issues sheets. Runs the same checks as revit_check_circuit_health. Accepts: panelName, systemType, fileName (default Circuit_Health_Report.xlsx), limit. Returns the file path.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    private readonly ExportPathService _pathService = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

        var panelName = ToolArguments.GetString(request.Arguments, "panelName");
        var systemType = ToolArguments.GetString(request.Arguments, "systemType");
        var fileName = ToolArguments.GetString(request.Arguments, "fileName", "Circuit_Health_Report.xlsx");
        var limit = ToolArguments.GetInt(request.Arguments, "limit", 5000);

        var circuits = CircuitQueryService.GetAll(doc);
        if (!string.IsNullOrWhiteSpace(panelName) || !string.IsNullOrWhiteSpace(systemType))
            circuits = CircuitQueryService.Filter(circuits, panelName, null, systemType);
        var filtered = circuits.Take(limit).ToList();
        var filePath = _pathService.ResolveFilePath(fileName);

        var circNumMap = new Dictionary<string, List<long>>();
        foreach (var c in filtered)
        {
            var num = c.CircuitNumber ?? "";
            if (string.IsNullOrWhiteSpace(num)) continue;
            var p = CircuitDtoBuilder.TryGetPanel(c);
            var key = $"{p?.Id?.Value ?? 0}|{num}";
            if (!circNumMap.ContainsKey(key)) circNumMap[key] = [];
            circNumMap[key].Add(c.Id.Value);
        }

        var issues = new List<(string Panel, long CircuitId, string CircNum, string Issue)>();
        foreach (var c in filtered)
        {
            var panel = CircuitDtoBuilder.TryGetPanel(c);
            var pName = panel?.Name ?? "";
            var cn = c.CircuitNumber ?? "";
            var wt = CircuitDtoBuilder.GetWireTypeName(doc, c);
            string ln = ""; try { ln = c.LookupParameter("Load Name")?.AsString() ?? ""; } catch { }

            if (panel == null) issues.Add((pName, c.Id.Value, cn, "MissingPanel"));
            if (string.IsNullOrWhiteSpace(cn)) issues.Add((pName, c.Id.Value, cn, "EmptyCircuitNumber"));
            else
            {
                var key = $"{panel?.Id?.Value ?? 0}|{cn}";
                if (circNumMap.TryGetValue(key, out var dups) && dups.Count > 1)
                    issues.Add((pName, c.Id.Value, cn, "DuplicateCircuitNumbers"));
            }
            if (string.IsNullOrEmpty(wt)) issues.Add((pName, c.Id.Value, cn, "MissingCableType"));
            if (string.IsNullOrWhiteSpace(ln)) issues.Add((pName, c.Id.Value, cn, "MissingLoadName"));
        }

        using (var wb = new XLWorkbook())
        {
            var ws0 = wb.Worksheets.Add("Summary");
            int r0 = 1;
            void AddS(string l, string v) { ws0.Cell(r0, 1).Value = l; ws0.Cell(r0, 1).Style.Font.Bold = true; ws0.Cell(r0, 2).Value = v; r0++; }
            AddS("Export Time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            AddS("Total Circuits Checked", filtered.Count.ToString());
            AddS("Circuits With Issues", issues.Select(i => i.CircuitId).Distinct().Count().ToString());
            AddS("Total Issues", issues.Count.ToString());
            ws0.Column(1).Width = 26; ws0.Column(2).AdjustToContents();

            var ws1 = wb.Worksheets.Add("Health Issues");
            var hdrs = new[] { "Panel", "Circuit Id", "Circuit Number", "Issue" };
            for (var c = 0; c < hdrs.Length; c++) { ws1.Cell(1, c + 1).Value = hdrs[c]; ws1.Cell(1, c + 1).Style.Font.Bold = true; }
            for (var row = 0; row < issues.Count; row++)
            {
                ws1.Cell(row + 2, 1).Value = issues[row].Panel;
                ws1.Cell(row + 2, 2).Value = issues[row].CircuitId;
                ws1.Cell(row + 2, 3).Value = issues[row].CircNum;
                ws1.Cell(row + 2, 4).Value = issues[row].Issue;
            }
            if (issues.Count > 0)
            {
                ws1.Range(1, 1, issues.Count + 1, 4).CreateTable("HealthIssuesTable").ShowAutoFilter = true;
                ws1.SheetView.FreezeRows(1);
                for (var c = 1; c <= 4; c++) ws1.Column(c).AdjustToContents(1, Math.Min(issues.Count + 1, 200));
            }
            wb.SaveAs(filePath);
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Exported {issues.Count} issue(s) for {filtered.Count} circuit(s) to {System.IO.Path.GetFileName(filePath)}",
            Data = new { filePath, checkedCount = filtered.Count, issueCount = issues.Count },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
