using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClosedXML.Excel;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Export;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class ExportElectricalDashboardToExcelTool : IRevitMcpTool
{
    public string Name => "revit_export_electrical_dashboard_to_excel";
    public string Description => "Exports electrical dashboard summary to an .xlsx file with sheets: Summary, Issue Breakdown, Panel Summary, System Type Summary, Uncircuited Summary. Accepts: fileName (default Electrical_Dashboard_Summary.xlsx), includePanelSummary (bool), includeIssueDetails (bool), includeSystemTypeSummary (bool), limit (int).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    private readonly ExportPathService _pathService = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

        var fileName               = ToolArguments.GetString(request.Arguments, "fileName", "Electrical_Dashboard_Summary.xlsx");
        var includePanelSummary    = ToolArguments.GetBool(request.Arguments, "includePanelSummary",       true);
        var includeIssueDetails    = ToolArguments.GetBool(request.Arguments, "includeIssueDetails",       true);
        var includeSystemTypeSummary = ToolArguments.GetBool(request.Arguments, "includeSystemTypeSummary", true);
        var limit                  = ToolArguments.GetInt(request.Arguments,  "limit",                    5000);

        var data = ElectricalDashboardService.Build(
            doc,
            includePanels: includePanelSummary,
            includeSystemTypes: includeSystemTypeSummary,
            includeTopIssues: includeIssueDetails,
            includeUncircuitedSummary: true,
            includeLoadSummary: true,
            limit: limit,
            cancellationToken: cancellationToken);

        var filePath = _pathService.ResolveFilePath(fileName);

        // Reflect summary values from anonymous object via Newtonsoft serialization trick
        var summaryDict = Newtonsoft.Json.Linq.JObject.FromObject(data.Summary).ToObject<Dictionary<string, object?>>() ?? new();
        var modelDict   = Newtonsoft.Json.Linq.JObject.FromObject(data.Model).ToObject<Dictionary<string, object?>>() ?? new();

        using (var wb = new XLWorkbook())
        {
            // ── Summary sheet ──────────────────────────────────────────────
            var ws0 = wb.Worksheets.Add("Summary");
            int r0 = 1;
            void AddS(string l, string v) { ws0.Cell(r0, 1).Value = l; ws0.Cell(r0, 1).Style.Font.Bold = true; ws0.Cell(r0, 2).Value = v; r0++; }
            AddS("Export Time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            AddS("Model", modelDict.TryGetValue("title", out var t) ? t?.ToString() ?? "" : "");
            AddS("Central Path", modelDict.TryGetValue("centralPath", out var cp) ? cp?.ToString() ?? "" : "");
            AddS("Revit User", modelDict.TryGetValue("revitUser", out var ru) ? ru?.ToString() ?? "" : "");
            r0++;
            foreach (var kv in summaryDict)
                AddS(kv.Key, kv.Value?.ToString() ?? "");
            if (data.Warnings.Count > 0)
            {
                r0++;
                ws0.Cell(r0, 1).Value = "Warnings"; ws0.Cell(r0, 1).Style.Font.Bold = true; r0++;
                foreach (var w in data.Warnings) { ws0.Cell(r0, 1).Value = w; r0++; }
            }
            ws0.Column(1).Width = 34; ws0.Column(2).AdjustToContents();

            // ── Issue Breakdown ────────────────────────────────────────────
            if (includeIssueDetails && data.IssueBreakdown.Count > 0)
            {
                var ws1 = wb.Worksheets.Add("Issue Breakdown");
                ws1.Cell(1, 1).Value = "Issue Type"; ws1.Cell(1, 1).Style.Font.Bold = true;
                ws1.Cell(1, 2).Value = "Count";      ws1.Cell(1, 2).Style.Font.Bold = true;
                for (int i = 0; i < data.IssueBreakdown.Count; i++)
                {
                    var jobj = Newtonsoft.Json.Linq.JObject.FromObject(data.IssueBreakdown[i]);
                    ws1.Cell(i + 2, 1).Value = jobj["issueType"]?.ToString() ?? "";
                    ws1.Cell(i + 2, 2).Value = jobj["count"]?.ToString() ?? "";
                }
                ws1.Column(1).AdjustToContents(); ws1.Column(2).AdjustToContents();
            }

            // ── Panel Summary ──────────────────────────────────────────────
            if (includePanelSummary && data.PanelsWithMostIssues.Count > 0)
            {
                var ws2 = wb.Worksheets.Add("Panel Summary");
                var ph = new[] { "Panel Name", "Panel Element Id", "Issue Count", "Circuit Count", "Missing Cable Type", "Missing Load Name", "Duplicate Circuit Numbers", "Total Load" };
                for (int i = 0; i < ph.Length; i++) { ws2.Cell(1, i + 1).Value = ph[i]; ws2.Cell(1, i + 1).Style.Font.Bold = true; }
                for (int i = 0; i < data.PanelsWithMostIssues.Count; i++)
                {
                    var jobj = Newtonsoft.Json.Linq.JObject.FromObject(data.PanelsWithMostIssues[i]);
                    ws2.Cell(i + 2, 1).Value = jobj["panelName"]?.ToString() ?? "";
                    ws2.Cell(i + 2, 2).Value = jobj["panelElementId"]?.ToString() ?? "";
                    ws2.Cell(i + 2, 3).Value = jobj["issueCount"]?.ToString() ?? "";
                    ws2.Cell(i + 2, 4).Value = jobj["circuitCount"]?.ToString() ?? "";
                    ws2.Cell(i + 2, 5).Value = jobj["missingCableTypeCount"]?.ToString() ?? "";
                    ws2.Cell(i + 2, 6).Value = jobj["missingLoadNameCount"]?.ToString() ?? "";
                    ws2.Cell(i + 2, 7).Value = jobj["duplicateCircuitNumberCount"]?.ToString() ?? "";
                    ws2.Cell(i + 2, 8).Value = jobj["totalApparentLoadDisplay"]?.ToString() ?? "";
                }
                if (data.PanelsWithMostIssues.Count > 0)
                    ws2.Range(1, 1, data.PanelsWithMostIssues.Count + 1, ph.Length).CreateTable("PanelSummaryTable").ShowAutoFilter = true;
                for (int i = 1; i <= ph.Length; i++) ws2.Column(i).AdjustToContents(1, Math.Min(data.PanelsWithMostIssues.Count + 1, 200));
            }

            // ── System Type Summary ────────────────────────────────────────
            if (includeSystemTypeSummary && data.SystemTypeSummary.Count > 0)
            {
                var ws3 = wb.Worksheets.Add("System Type Summary");
                ws3.Cell(1, 1).Value = "System Type";   ws3.Cell(1, 1).Style.Font.Bold = true;
                ws3.Cell(1, 2).Value = "Circuit Count"; ws3.Cell(1, 2).Style.Font.Bold = true;
                ws3.Cell(1, 3).Value = "Issue Count";   ws3.Cell(1, 3).Style.Font.Bold = true;
                for (int i = 0; i < data.SystemTypeSummary.Count; i++)
                {
                    var jobj = Newtonsoft.Json.Linq.JObject.FromObject(data.SystemTypeSummary[i]);
                    ws3.Cell(i + 2, 1).Value = jobj["systemType"]?.ToString() ?? "";
                    ws3.Cell(i + 2, 2).Value = jobj["circuitCount"]?.ToString() ?? "";
                    ws3.Cell(i + 2, 3).Value = jobj["issueCount"]?.ToString() ?? "";
                }
                for (int i = 1; i <= 3; i++) ws3.Column(i).AdjustToContents();
            }

            wb.SaveAs(filePath);
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Electrical dashboard exported to {filePath}",
            Data = new { filePath },
            Warnings = data.Warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
