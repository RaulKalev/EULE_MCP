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

public class ExportVoltageDropInputToExcelTool : IRevitMcpTool
{
    public string Name => "revit_export_voltage_drop_input_to_excel";
    public string Description => "Exports circuit data and estimated lengths for manual voltage-drop calculations to an .xlsx file. Sheets: Summary, Voltage Drop Input, Circuit Elements, Assumptions, Failures. Accepts: circuitIds (long[], optional — overrides other filters), panelName, systemType, method (default ManhattanMax), routingMultiplier (default 1.25), fileName, limit. Results are PRELIMINARY engineering input only.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    private readonly ExportPathService _pathService = new();

    private static readonly string Disclaimer =
        "Circuit length is estimated from model element locations and does not represent actual installed cable routing unless routing/path data is explicitly available. Use results as preliminary engineering input only.";

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

        var panelName = ToolArguments.GetString(request.Arguments, "panelName");
        var systemType = ToolArguments.GetString(request.Arguments, "systemType");
        var method = ToolArguments.GetString(request.Arguments, "method", "ManhattanMax");
        var multiplier = GetDouble(request.Arguments, "routingMultiplier", 1.25);
        var fileName = ToolArguments.GetString(request.Arguments, "fileName", "Voltage_Drop_Input.xlsx");
        var limit = ToolArguments.GetInt(request.Arguments, "limit", 1000);
        var circuitIds = ToolArguments.GetLongArray(request.Arguments, "circuitIds");

        var circuits = CircuitQueryService.GetAll(doc);
        if (circuitIds.Length > 0)
        {
            var idSet = new HashSet<long>(circuitIds);
            circuits = circuits.Where(c => idSet.Contains(c.Id.Value)).ToList();
        }
        else if (!string.IsNullOrWhiteSpace(panelName) || !string.IsNullOrWhiteSpace(systemType))
        {
            circuits = CircuitQueryService.Filter(circuits, panelName, null, systemType);
        }
        circuits = circuits.Take(limit).ToList();

        var filePath = _pathService.ResolveFilePath(fileName);

        // Model info
        string title = doc.Title ?? "";
        bool isWorkshared = false; string centralPath = "", revitUser = "";
        try { isWorkshared = doc.IsWorkshared; if (isWorkshared) centralPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(doc.GetWorksharingCentralModelPath()); } catch { }
        try { revitUser = doc.Application?.Username ?? ""; } catch { }

        // Compute rows
        var vdRows = new List<VdRow>();
        var elementRows = new List<ElemRow>();
        var failures = new List<(long CircuitId, string PanelN, string CircNum, string Reason)>();

        foreach (var c in circuits)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var panel = CircuitDtoBuilder.TryGetPanel(c);
            var pName = panel?.Name ?? "";
            var circNum = c.CircuitNumber ?? "";
            var wireType = CircuitDtoBuilder.GetWireTypeName(doc, c);
            string loadName = ""; try { loadName = c.LookupParameter("Load Name")?.AsString() ?? ""; } catch { }
            double voltage = 0, load = 0;
            try { voltage = c.Voltage; } catch { }
            try { load = c.ApparentLoad; } catch { }
            double currentEst = voltage > 0 ? Math.Round(load / voltage, 2) : 0;
            var stName = c.SystemType.ToString();

            if (panel == null)
            {
                failures.Add((c.Id.Value, pName, circNum, "No panel assigned."));
                continue;
            }

            var panelR = CircuitLocationResolver.Resolve(panel);
            if (panelR == null)
            {
                failures.Add((c.Id.Value, pName, circNum, "Panel location not available."));
                continue;
            }
            var panelPt = panelR.Value.Pt;

            var elements = new List<(long Id, (double X, double Y, double Z)? Loc, Element? Elem)>();
            try
            {
                if (c.Elements != null)
                    foreach (Element e in c.Elements)
                    {
                        var r = CircuitLocationResolver.Resolve(e);
                        elements.Add((e.Id.Value, r?.Pt, e));
                    }
            }
            catch { }

            int missingLoc = elements.Count(e => !e.Loc.HasValue);
            var (rawFeet, _) = CircuitLengthEstimator.Estimate(panelPt, elements.Select(e => (e.Id, e.Loc)).ToList(), method);
            double rawMeters = LengthUnitHelper.RoundMeters(LengthUnitHelper.ToMeters(rawFeet));
            double estimatedMeters = LengthUnitHelper.RoundMeters(rawMeters * multiplier);

            var warn = new List<string>();
            if (missingLoc > 0) warn.Add($"{missingLoc} elements missing location.");
            if (string.IsNullOrEmpty(wireType)) warn.Add("Cable type not assigned.");

            vdRows.Add(new VdRow
            {
                Panel = pName, CircuitNumber = circNum, CircuitId = c.Id.Value, LoadName = loadName,
                SystemType = stName, Voltage = $"{voltage:F0} V", ApparentLoad = $"{load:F0} VA",
                CurrentEstimate = currentEst, CableType = wireType, WireType = wireType,
                EstimatedLengthM = estimatedMeters, LengthMethod = method,
                RoutingMultiplier = multiplier, ConnectedElementCount = elements.Count,
                ElementsMissingLocation = missingLoc, Warnings = string.Join("; ", warn)
            });

            // Element rows
            foreach (var (elemId, loc, elem) in elements)
            {
                if (elem == null) continue;
                var typeId = elem.GetTypeId();
                var typeElem = typeId != null && typeId != ElementId.InvalidElementId ? doc.GetElement(typeId) : null;
                string locSource = loc.HasValue ? (CircuitLocationResolver.Resolve(elem)?.Source ?? "") : "Unavailable";
                double distM = loc.HasValue
                    ? LengthUnitHelper.RoundMeters(LengthUnitHelper.ToMeters(
                        CircuitLengthEstimator.Manhattan(panelPt.X, panelPt.Y, panelPt.Z, loc.Value.X, loc.Value.Y, loc.Value.Z)))
                    : 0;

                string levelName = "";
                try { var lvlId = elem.LevelId; if (lvlId != null && lvlId != ElementId.InvalidElementId) levelName = (doc.GetElement(lvlId) as Level)?.Name ?? ""; } catch { }

                elementRows.Add(new ElemRow
                {
                    Panel = pName, CircuitNumber = circNum, CircuitId = c.Id.Value,
                    ElementId = elemId, Category = elem.Category?.Name ?? "",
                    Family = elem is FamilyInstance fi ? fi.Symbol?.Family?.Name ?? "" : "",
                    Type = typeElem?.Name ?? elem.Name, Level = levelName,
                    X = loc.HasValue ? Math.Round(LengthUnitHelper.ToMeters(loc.Value.X), 3) : 0,
                    Y = loc.HasValue ? Math.Round(LengthUnitHelper.ToMeters(loc.Value.Y), 3) : 0,
                    Z = loc.HasValue ? Math.Round(LengthUnitHelper.ToMeters(loc.Value.Z), 3) : 0,
                    DistanceFromPanelM = distM, LocationSource = locSource
                });
            }
        }

        // Write Excel
        using (var wb = new XLWorkbook())
        {
            // Sheet: Summary
            var ws0 = wb.Worksheets.Add("Summary");
            int r0 = 1;
            void S(string l, string v) { ws0.Cell(r0, 1).Value = l; ws0.Cell(r0, 1).Style.Font.Bold = true; ws0.Cell(r0, 2).Value = v; r0++; }
            S("Export Time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            S("Model", title);
            S("Central Path", centralPath);
            S("Revit User", revitUser);
            S("Circuits Processed", vdRows.Count.ToString());
            S("Failures", failures.Count.ToString());
            S("Length Method", method);
            S("Routing Multiplier", multiplier.ToString("F2"));
            ws0.Column(1).Width = 24; ws0.Column(2).AdjustToContents();

            // Sheet: Voltage Drop Input
            var ws1 = wb.Worksheets.Add("Voltage Drop Input");
            var h1 = new[] { "Panel", "Circuit Number", "Circuit Id", "Load Name", "System Type", "Voltage", "Apparent Load", "Current Estimate A", "Cable Type", "Wire Type", "Estimated Length m", "Length Method", "Routing Multiplier", "Connected Element Count", "Elements Missing Location", "Warnings" };
            for (int i = 0; i < h1.Length; i++) { ws1.Cell(1, i + 1).Value = h1[i]; ws1.Cell(1, i + 1).Style.Font.Bold = true; }
            for (int i = 0; i < vdRows.Count; i++)
            {
                var row = vdRows[i]; int r = i + 2;
                ws1.Cell(r, 1).Value = row.Panel; ws1.Cell(r, 2).Value = row.CircuitNumber; ws1.Cell(r, 3).Value = row.CircuitId;
                ws1.Cell(r, 4).Value = row.LoadName; ws1.Cell(r, 5).Value = row.SystemType; ws1.Cell(r, 6).Value = row.Voltage;
                ws1.Cell(r, 7).Value = row.ApparentLoad; ws1.Cell(r, 8).Value = row.CurrentEstimate; ws1.Cell(r, 9).Value = row.CableType;
                ws1.Cell(r, 10).Value = row.WireType; ws1.Cell(r, 11).Value = row.EstimatedLengthM; ws1.Cell(r, 12).Value = row.LengthMethod;
                ws1.Cell(r, 13).Value = row.RoutingMultiplier; ws1.Cell(r, 14).Value = row.ConnectedElementCount;
                ws1.Cell(r, 15).Value = row.ElementsMissingLocation; ws1.Cell(r, 16).Value = row.Warnings;
            }
            if (vdRows.Count > 0)
            {
                ws1.Range(1, 1, vdRows.Count + 1, h1.Length).CreateTable("VDInputTable").ShowAutoFilter = true;
                ws1.SheetView.FreezeRows(1);
                for (int i = 1; i <= h1.Length; i++) ws1.Column(i).AdjustToContents(1, Math.Min(vdRows.Count + 1, 200));
            }

            // Sheet: Circuit Elements
            var ws2 = wb.Worksheets.Add("Circuit Elements");
            var h2 = new[] { "Panel", "Circuit Number", "Circuit Id", "Element Id", "Category", "Family", "Type", "Level", "X m", "Y m", "Z m", "Distance From Panel m", "Location Source" };
            for (int i = 0; i < h2.Length; i++) { ws2.Cell(1, i + 1).Value = h2[i]; ws2.Cell(1, i + 1).Style.Font.Bold = true; }
            for (int i = 0; i < elementRows.Count; i++)
            {
                var row = elementRows[i]; int r = i + 2;
                ws2.Cell(r, 1).Value = row.Panel; ws2.Cell(r, 2).Value = row.CircuitNumber; ws2.Cell(r, 3).Value = row.CircuitId;
                ws2.Cell(r, 4).Value = row.ElementId; ws2.Cell(r, 5).Value = row.Category; ws2.Cell(r, 6).Value = row.Family;
                ws2.Cell(r, 7).Value = row.Type; ws2.Cell(r, 8).Value = row.Level;
                ws2.Cell(r, 9).Value = row.X; ws2.Cell(r, 10).Value = row.Y; ws2.Cell(r, 11).Value = row.Z;
                ws2.Cell(r, 12).Value = row.DistanceFromPanelM; ws2.Cell(r, 13).Value = row.LocationSource;
            }
            if (elementRows.Count > 0)
            {
                ws2.Range(1, 1, elementRows.Count + 1, h2.Length).CreateTable("ElementsTable").ShowAutoFilter = true;
                ws2.SheetView.FreezeRows(1);
                for (int i = 1; i <= h2.Length; i++) ws2.Column(i).AdjustToContents(1, Math.Min(elementRows.Count + 1, 200));
            }

            // Sheet: Assumptions
            var ws3 = wb.Worksheets.Add("Assumptions");
            int r3 = 1;
            void A(string l, string v) { ws3.Cell(r3, 1).Value = l; ws3.Cell(r3, 1).Style.Font.Bold = true; ws3.Cell(r3, 2).Value = v; r3++; }
            A("Export Time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            A("Model Name", title);
            A("Central Path", centralPath);
            A("Revit User", revitUser);
            A("Length Method", method);
            A("Routing Multiplier", multiplier.ToString("F2"));
            r3++;
            ws3.Cell(r3, 1).Value = "DISCLAIMER"; ws3.Cell(r3, 1).Style.Font.Bold = true; ws3.Cell(r3, 1).Style.Font.FontColor = XLColor.Red; r3++;
            ws3.Cell(r3, 1).Value = Disclaimer; ws3.Cell(r3, 1).Style.Alignment.WrapText = true; ws3.Row(r3).Height = 50; r3 += 2;
            ws3.Cell(r3, 1).Value = "Unit Conversion"; ws3.Cell(r3, 1).Style.Font.Bold = true; r3++;
            ws3.Cell(r3, 1).Value = "Revit internal units are feet. All length values in this export are converted to meters (1 ft = 0.3048 m)."; r3++;
            ws3.Column(1).Width = 20; ws3.Column(2).Width = 80;

            // Sheet: Failures
            var ws4 = wb.Worksheets.Add("Failures");
            ws4.Cell(1, 1).Value = "Circuit Id"; ws4.Cell(1, 1).Style.Font.Bold = true;
            ws4.Cell(1, 2).Value = "Panel";      ws4.Cell(1, 2).Style.Font.Bold = true;
            ws4.Cell(1, 3).Value = "Circuit Number"; ws4.Cell(1, 3).Style.Font.Bold = true;
            ws4.Cell(1, 4).Value = "Reason";     ws4.Cell(1, 4).Style.Font.Bold = true;
            for (int i = 0; i < failures.Count; i++)
            {
                ws4.Cell(i + 2, 1).Value = failures[i].CircuitId;
                ws4.Cell(i + 2, 2).Value = failures[i].PanelN;
                ws4.Cell(i + 2, 3).Value = failures[i].CircNum;
                ws4.Cell(i + 2, 4).Value = failures[i].Reason;
            }
            if (failures.Count > 0) for (int i = 1; i <= 4; i++) ws4.Column(i).AdjustToContents();

            wb.SaveAs(filePath);
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Voltage-drop input exported to {filePath}",
            Data = new { filePath, circuitsProcessed = vdRows.Count, failures = failures.Count },
            Warnings = [Disclaimer],
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static double GetDouble(Dictionary<string, object?> args, string key, double def)
    {
        if (!args.TryGetValue(key, out var val)) return def;
        return val switch { double d => d, float f => f, Newtonsoft.Json.Linq.JValue jv => (double)jv, _ => def };
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };

    // ── Internal row types ────────────────────────────────────────────────────
    private sealed class VdRow
    {
        public string Panel = "", CircuitNumber = "", LoadName = "", SystemType = "", Voltage = "", ApparentLoad = "", CableType = "", WireType = "", LengthMethod = "", Warnings = "";
        public long CircuitId;
        public double CurrentEstimate, EstimatedLengthM, RoutingMultiplier;
        public int ConnectedElementCount, ElementsMissingLocation;
    }
    private sealed class ElemRow
    {
        public string Panel = "", CircuitNumber = "", Category = "", Family = "", Type = "", Level = "", LocationSource = "";
        public long CircuitId, ElementId;
        public double X, Y, Z, DistanceFromPanelM;
    }
}
