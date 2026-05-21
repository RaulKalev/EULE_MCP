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

public class ExportPanelCircuitListToExcelTool : IRevitMcpTool
{
    public string Name => "revit_export_panel_circuit_list_to_excel";
    public string Description => "Exports a panel-organized circuit report to a formatted .xlsx file. Sheets: Summary, Panel Circuits, Circuit Elements (optional), Health Issues (optional). Returns the file path.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    private readonly ExportPathService _pathService = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null) return Task.FromResult(Fail(request, "No active document."));
        var doc = uidoc.Document;

        var panelNameFilter = ToolArguments.GetString(request.Arguments, "panelName");
        var systemTypeFilter = ToolArguments.GetString(request.Arguments, "systemType");
        var includeElements = ToolArguments.GetBool(request.Arguments, "includeElements", true);
        var includeHealthCheck = ToolArguments.GetBool(request.Arguments, "includeHealthCheck", true);
        var fileName = ToolArguments.GetString(request.Arguments, "fileName", "Panel_Circuit_List.xlsx");
        var limit = ToolArguments.GetInt(request.Arguments, "limit", 5000);

        var circuits = CircuitQueryService.GetAll(doc);
        if (!string.IsNullOrWhiteSpace(panelNameFilter) || !string.IsNullOrWhiteSpace(systemTypeFilter))
            circuits = CircuitQueryService.Filter(circuits, panelNameFilter, null, systemTypeFilter);

        var filtered = circuits.Take(limit).ToList();
        var filePath = _pathService.ResolveFilePath(fileName);
        var warnings = new List<string>();

        string modelName = doc.Title ?? "";
        string centralPath = "";
        try { centralPath = doc.IsWorkshared ? doc.GetWorksharingCentralModelPath()?.CentralServerPath ?? "" : doc.PathName; } catch { }
        string revitUser = ""; try { revitUser = uiapp.Application.Username; } catch { }

        using (var wb = new XLWorkbook())
        {
            AddSummarySheet(wb, modelName, centralPath, revitUser, filtered.Count, panelNameFilter, systemTypeFilter);
            AddPanelCircuitsSheet(wb, doc, filtered);
            if (includeElements) AddCircuitElementsSheet(wb, doc, filtered, warnings);
            if (includeHealthCheck) AddHealthIssuesSheet(wb, doc, filtered);
            wb.SaveAs(filePath);
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Exported {filtered.Count} circuit(s) to {System.IO.Path.GetFileName(filePath)}",
            Data = new { filePath, circuitCount = filtered.Count, includesElements = includeElements, includesHealthIssues = includeHealthCheck },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static void AddSummarySheet(XLWorkbook wb, string modelName, string centralPath, string user, int count, string panelFilter, string sysFilter)
    {
        var ws = wb.Worksheets.Add("Summary");
        int row = 1;
        void Add(string label, string value)
        {
            ws.Cell(row, 1).Value = label; ws.Cell(row, 2).Value = value;
            ws.Cell(row, 1).Style.Font.Bold = true; row++;
        }
        Add("Export Time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        Add("Model", modelName);
        Add("Central Path", centralPath);
        Add("Revit User", user);
        Add("Total Circuits", count.ToString());
        if (!string.IsNullOrWhiteSpace(panelFilter)) Add("Panel Filter", panelFilter);
        if (!string.IsNullOrWhiteSpace(sysFilter)) Add("System Type Filter", sysFilter);
        ws.Column(1).Width = 22;
        ws.Column(2).AdjustToContents();
    }

    private static void AddPanelCircuitsSheet(XLWorkbook wb, Document doc, List<ElectricalSystem> circuits)
    {
        var ws = wb.Worksheets.Add("Panel Circuits");
        var headers = new[] { "Panel", "Circuit Number", "Load Name", "Circuit Id", "System Type", "Elements Count", "Apparent Load", "Voltage", "Poles", "Cable/Wire Type", "Comments" };
        for (var c = 0; c < headers.Length; c++) { ws.Cell(1, c + 1).Value = headers[c]; ws.Cell(1, c + 1).Style.Font.Bold = true; }

        for (var r = 0; r < circuits.Count; r++)
        {
            var circuit = circuits[r];
            var panel = CircuitDtoBuilder.TryGetPanel(circuit);
            var wireType = CircuitDtoBuilder.GetWireTypeName(doc, circuit);
            string loadName = ""; try { loadName = circuit.LookupParameter("Load Name")?.AsString() ?? ""; } catch { }
            double apparentLoad = 0; try { apparentLoad = circuit.ApparentLoad; } catch { }
            double voltage = 0; try { voltage = circuit.Voltage; } catch { }
            int poles = 0; try { poles = circuit.PolesNumber; } catch { }
            int elemCount = 0; try { elemCount = circuit.Elements?.Size ?? 0; } catch { }
            string comments = ""; try { comments = circuit.LookupParameter("Comments")?.AsString() ?? ""; } catch { }

            ws.Cell(r + 2, 1).Value = panel?.Name ?? "";
            ws.Cell(r + 2, 2).Value = circuit.CircuitNumber ?? "";
            ws.Cell(r + 2, 3).Value = loadName;
            ws.Cell(r + 2, 4).Value = circuit.Id.Value;
            ws.Cell(r + 2, 5).Value = circuit.SystemType.ToString();
            ws.Cell(r + 2, 6).Value = elemCount;
            ws.Cell(r + 2, 7).Value = $"{apparentLoad:F0} VA";
            ws.Cell(r + 2, 8).Value = $"{voltage:F0} V";
            ws.Cell(r + 2, 9).Value = poles;
            ws.Cell(r + 2, 10).Value = wireType;
            ws.Cell(r + 2, 11).Value = comments;
        }
        if (circuits.Count > 0) FormatTable(ws, 1, 1, circuits.Count + 1, headers.Length, "PanelCircuitsTable");
    }

    private static void AddCircuitElementsSheet(XLWorkbook wb, Document doc, List<ElectricalSystem> circuits, List<string> warnings)
    {
        var ws = wb.Worksheets.Add("Circuit Elements");
        var headers = new[] { "Panel", "Circuit Number", "Circuit Id", "Element Id", "Category", "Family", "Type", "Level" };
        for (var c = 0; c < headers.Length; c++) { ws.Cell(1, c + 1).Value = headers[c]; ws.Cell(1, c + 1).Style.Font.Bold = true; }

        int row = 2;
        foreach (var circuit in circuits)
        {
            var panel = CircuitDtoBuilder.TryGetPanel(circuit);
            var panelName = panel?.Name ?? "";
            var circuitNum = circuit.CircuitNumber ?? "";
            try
            {
                var es = circuit.Elements;
                if (es == null) continue;
                foreach (Element e in es)
                {
                    var typeId = e.GetTypeId();
                    var typeElem = typeId != null && typeId != ElementId.InvalidElementId ? doc.GetElement(typeId) : null;
                    string level = "";
                    try
                    {
                        var lvl = e.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM)
                               ?? e.get_Parameter(BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM);
                        if (lvl != null) level = doc.GetElement(lvl.AsElementId())?.Name ?? "";
                    }
                    catch { }

                    ws.Cell(row, 1).Value = panelName;
                    ws.Cell(row, 2).Value = circuitNum;
                    ws.Cell(row, 3).Value = circuit.Id.Value;
                    ws.Cell(row, 4).Value = e.Id.Value;
                    ws.Cell(row, 5).Value = e.Category?.Name ?? "";
                    ws.Cell(row, 6).Value = e is FamilyInstance fi ? fi.Symbol?.Family?.Name ?? "" : "";
                    ws.Cell(row, 7).Value = typeElem?.Name ?? e.Name;
                    ws.Cell(row, 8).Value = level;
                    row++;
                }
            }
            catch { warnings.Add($"Could not read elements for circuit {circuit.Id.Value} — skipped."); }
        }
        if (row > 2) FormatTable(ws, 1, 1, row - 1, headers.Length, "CircuitElementsTable");
    }

    private static void AddHealthIssuesSheet(XLWorkbook wb, Document doc, List<ElectricalSystem> circuits)
    {
        var issues = new List<(string Panel, long CircuitId, string CircuitNum, string Issue)>();
        var circNumMap = new Dictionary<string, List<long>>();
        foreach (var c in circuits)
        {
            var num = c.CircuitNumber ?? "";
            if (string.IsNullOrWhiteSpace(num)) continue;
            var p = CircuitDtoBuilder.TryGetPanel(c);
            var key = $"{p?.Id?.Value ?? 0}|{num}";
            if (!circNumMap.ContainsKey(key)) circNumMap[key] = new List<long>();
            circNumMap[key].Add(c.Id.Value);
        }

        foreach (var circuit in circuits)
        {
            var panel = CircuitDtoBuilder.TryGetPanel(circuit);
            var pName = panel?.Name ?? "";
            var cn = circuit.CircuitNumber ?? "";
            var wireType = CircuitDtoBuilder.GetWireTypeName(doc, circuit);

            if (panel == null) issues.Add((pName, circuit.Id.Value, cn, "MissingPanel"));
            if (string.IsNullOrWhiteSpace(cn)) issues.Add((pName, circuit.Id.Value, cn, "EmptyCircuitNumber"));
            if (!string.IsNullOrWhiteSpace(cn))
            {
                var key = $"{panel?.Id?.Value ?? 0}|{cn}";
                if (circNumMap.TryGetValue(key, out var dups) && dups.Count > 1)
                    issues.Add((pName, circuit.Id.Value, cn, "DuplicateCircuitNumbers"));
            }
            if (string.IsNullOrEmpty(wireType)) issues.Add((pName, circuit.Id.Value, cn, "MissingCableType"));
            try
            {
                if (circuit.Elements == null || circuit.Elements.IsEmpty)
                    issues.Add((pName, circuit.Id.Value, cn, "NoConnectedElements"));
            }
            catch { }
        }

        var ws = wb.Worksheets.Add("Health Issues");
        var headers = new[] { "Panel", "Circuit Id", "Circuit Number", "Issue" };
        for (var c = 0; c < headers.Length; c++) { ws.Cell(1, c + 1).Value = headers[c]; ws.Cell(1, c + 1).Style.Font.Bold = true; }
        for (var r = 0; r < issues.Count; r++)
        {
            ws.Cell(r + 2, 1).Value = issues[r].Panel;
            ws.Cell(r + 2, 2).Value = issues[r].CircuitId;
            ws.Cell(r + 2, 3).Value = issues[r].CircuitNum;
            ws.Cell(r + 2, 4).Value = issues[r].Issue;
        }
        if (issues.Count > 0) FormatTable(ws, 1, 1, issues.Count + 1, headers.Length, "HealthIssuesTable");
    }

    private static void FormatTable(IXLWorksheet ws, int fr, int fc, int lr, int lc, string name)
    {
        if (lr <= fr) return;
        ws.Range(fr, fc, lr, lc).CreateTable(name).ShowAutoFilter = true;
        ws.Row(fr).Style.Font.Bold = true;
        ws.SheetView.FreezeRows(1);
        for (var c = fc; c <= lc; c++) ws.Column(c).AdjustToContents(1, Math.Min(lr, 200));
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
