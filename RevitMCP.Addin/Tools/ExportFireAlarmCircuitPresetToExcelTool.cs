using System.Diagnostics;
using Autodesk.Revit.UI;
using ClosedXML.Excel;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Export;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class ExportFireAlarmCircuitPresetToExcelTool : IRevitMcpTool
{
    public string Name => "revit_export_fire_alarm_circuit_preset_to_excel";
    public string Description => "Exports fire alarm circuit preset data to .xlsx: sheets Summary, Loop Summary, Device List, Circuit Info, Voltage Drop Input, Warnings. Accepts: panelName, loopParameterName (default 'Ahela nr.'), deviceTypeParameterName (default 'ELENEA_Nimetus'), includeVoltageDropInput (bool), sounderCurrentMilliAmps (default 50.0), fallbackResistanceOhmPerMeter (default 0.035), fileName, limit.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    private readonly ExportPathService _pathService = new();

    private static readonly string Disclaimer =
        "Loop lengths are read from Revit circuit Length parameter (if present) or remain 0. Voltage drop values are preliminary — verify against manufacturer data and actual routing.";

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

        var panelName        = ToolArguments.GetString(request.Arguments, "panelName");
        var loopParam        = ToolArguments.GetString(request.Arguments, "loopParameterName",         "Ahela nr.");
        var devTypeParam     = ToolArguments.GetString(request.Arguments, "deviceTypeParameterName",   "ELENEA_Nimetus");
        var includeVd        = ToolArguments.GetBool(request.Arguments,   "includeVoltageDropInput",   true);
        var sounderCurrentmA = GetDouble(request.Arguments, "sounderCurrentMilliAmps",            50.0);
        var fallbackRes      = GetDouble(request.Arguments, "fallbackResistanceOhmPerMeter",       0.035);
        var fileName         = ToolArguments.GetString(request.Arguments, "fileName",              "FireAlarm_Circuit_Preset.xlsx");
        var limit            = ToolArguments.GetInt(request.Arguments,    "limit",                 5000);

        var presetResult = FireAlarmCircuitPresetService.Run(
            doc, panelName, loopParam, "Seadme Nr.", devTypeParam, "Description",
            includeDeviceList: true, includeCircuitInfo: true,
            includeVoltageDrop: includeVd, allowDeviceNumberXxx: true,
            sortBy: [], limit: limit, cancellationToken: cancellationToken);

        CableResistanceProfileService.EnsureDefaultFileExists();
        var profileCollection = CableResistanceProfileService.Load();

        var filePath = _pathService.ResolveFilePath(fileName);
        string modelTitle = doc.Title ?? "";

        using (var wb = new XLWorkbook())
        {
            // ── Summary sheet ──────────────────────────────────────────────
            var ws0 = wb.Worksheets.Add("Summary");
            int r0 = 1;
            void S(string l, string v) { ws0.Cell(r0, 1).Value = l; ws0.Cell(r0, 1).Style.Font.Bold = true; ws0.Cell(r0, 2).Value = v; r0++; }
            S("Export Time",    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            S("Model",          modelTitle);
            S("Panel Filter",   panelName ?? "(all)");
            S("Loop Parameter", loopParam);
            S("Device Count",   presetResult.DeviceCount.ToString());
            S("Loop Count",     presetResult.LoopCount.ToString());
            ws0.Column(1).Width = 22; ws0.Column(2).AdjustToContents();
            if (presetResult.Warnings.Count > 0)
            {
                r0++;
                ws0.Cell(r0, 1).Value = "DISCLAIMER"; ws0.Cell(r0, 1).Style.Font.Bold = true; ws0.Cell(r0, 1).Style.Font.FontColor = XLColor.Red; r0++;
                ws0.Cell(r0, 1).Value = Disclaimer; ws0.Cell(r0, 1).Style.Alignment.WrapText = true; ws0.Row(r0).Height = 50;
            }

            // ── Loop Summary ───────────────────────────────────────────────
            var ws1 = wb.Worksheets.Add("Loop Summary");
            var lh = new[] { "Loop Name", "Kind", "Device Count", "Level Count", "Device Types", "Circuit Id", "Circuit Number", "Panel Name", "Cable Type", "Length m" };
            for (int i = 0; i < lh.Length; i++) { ws1.Cell(1, i + 1).Value = lh[i]; ws1.Cell(1, i + 1).Style.Font.Bold = true; }
            for (int i = 0; i < presetResult.Loops.Count; i++)
            {
                var l = presetResult.Loops[i]; int r = i + 2;
                ws1.Cell(r, 1).Value = l.LoopName;
                ws1.Cell(r, 2).Value = l.LoopKind;
                ws1.Cell(r, 3).Value = l.DeviceCount;
                ws1.Cell(r, 4).Value = l.Levels.Count;
                ws1.Cell(r, 5).Value = string.Join(", ", l.DeviceTypeSummary.Select(ts => $"{ts.DeviceType} ({ts.Count})"));
                ws1.Cell(r, 6).Value = l.CircuitInfo?.RevitCircuitId ?? 0;
                ws1.Cell(r, 7).Value = l.CircuitInfo?.RevitCircuitNumber ?? "";
                ws1.Cell(r, 8).Value = l.CircuitInfo?.PanelName ?? "";
                ws1.Cell(r, 9).Value = l.CircuitInfo?.CableType ?? "";
                ws1.Cell(r, 10).Value = l.CircuitInfo?.CircuitLengthMeters ?? 0;
            }
            if (presetResult.Loops.Count > 0)
            {
                ws1.Range(1, 1, presetResult.Loops.Count + 1, lh.Length).CreateTable("LoopSummaryTable").ShowAutoFilter = true;
                ws1.SheetView.FreezeRows(1);
                for (int i = 1; i <= lh.Length; i++) ws1.Column(i).AdjustToContents(1, Math.Min(presetResult.Loops.Count + 1, 200));
            }

            // ── Device List ────────────────────────────────────────────────
            var allDevices = presetResult.Loops.SelectMany(l => l.Devices).ToList();
            var ws2 = wb.Worksheets.Add("Device List");
            var dh = new[] { "Element Id", "Level", "Loop Name", "Device Number", "Device Type", "Description", "Circuit Id" };
            for (int i = 0; i < dh.Length; i++) { ws2.Cell(1, i + 1).Value = dh[i]; ws2.Cell(1, i + 1).Style.Font.Bold = true; }
            for (int i = 0; i < allDevices.Count; i++)
            {
                var d = allDevices[i]; int r = i + 2;
                ws2.Cell(r, 1).Value = d.ElementId;
                ws2.Cell(r, 2).Value = d.Level;
                ws2.Cell(r, 3).Value = d.LoopNumber;
                ws2.Cell(r, 4).Value = d.DeviceNumber;
                ws2.Cell(r, 5).Value = d.DeviceType;
                ws2.Cell(r, 6).Value = d.Description;
                ws2.Cell(r, 7).Value = d.RevitCircuitId;
            }
            if (allDevices.Count > 0)
            {
                ws2.Range(1, 1, allDevices.Count + 1, dh.Length).CreateTable("DeviceListTable").ShowAutoFilter = true;
                ws2.SheetView.FreezeRows(1);
                for (int i = 1; i <= dh.Length; i++) ws2.Column(i).AdjustToContents(1, Math.Min(allDevices.Count + 1, 200));
            }

            // ── Voltage Drop Input ─────────────────────────────────────────
            if (includeVd)
            {
                var ws3 = wb.Worksheets.Add("Voltage Drop Input");
                var vh = new[] { "Loop Name", "Kind", "Device Count", "Cable Type", "Length m", "Resistance Ohm/m", "Total Current mA", "Voltage Drop V", "End Voltage V", "Status" };
                for (int i = 0; i < vh.Length; i++) { ws3.Cell(1, i + 1).Value = vh[i]; ws3.Cell(1, i + 1).Style.Font.Bold = true; }
                int vRow = 2;
                foreach (var l in presetResult.Loops)
                {
                    string cableType = l.CircuitInfo?.CableType ?? "";
                    double lengthM = l.CircuitInfo?.CircuitLengthMeters ?? 0;
                    var profile = CableResistanceProfileService.FindMatch(cableType, profileCollection);
                    double res = profile?.RoundTripOhmPerMeter ?? fallbackRes;
                    double totalCurrentA = l.DeviceCount * sounderCurrentmA / 1000.0;
                    double voltDrop = l.LoopKind == "ConventionalSounderLine" ? Math.Round(totalCurrentA * lengthM * res, 2) : 0;
                    double loopRes = (l.LoopKind == "AddressableLoop" || l.LoopKind == "ModuleLoop") ? Math.Round(lengthM * res, 2) : 0;
                    ws3.Cell(vRow, 1).Value = l.LoopName;
                    ws3.Cell(vRow, 2).Value = l.LoopKind;
                    ws3.Cell(vRow, 3).Value = l.DeviceCount;
                    ws3.Cell(vRow, 4).Value = cableType;
                    ws3.Cell(vRow, 5).Value = lengthM;
                    ws3.Cell(vRow, 6).Value = res;
                    ws3.Cell(vRow, 7).Value = l.LoopKind == "ConventionalSounderLine" ? Math.Round(totalCurrentA * 1000, 1) : 0;
                    ws3.Cell(vRow, 8).Value = voltDrop;
                    ws3.Cell(vRow, 9).Value = l.LoopKind == "ConventionalSounderLine" ? Math.Round(24.0 - voltDrop, 2) : 0;
                    ws3.Cell(vRow, 10).Value = l.LoopKind == "AddressableLoop" ? $"Loop Ω: {loopRes}" : "";
                    vRow++;
                }
                if (vRow > 2)
                {
                    ws3.Range(1, 1, vRow - 1, vh.Length).CreateTable("VdInputTable").ShowAutoFilter = true;
                    ws3.SheetView.FreezeRows(1);
                    for (int i = 1; i <= vh.Length; i++) ws3.Column(i).AdjustToContents(1, Math.Min(vRow, 200));
                }
            }

            // ── Warnings ───────────────────────────────────────────────────
            var wsW = wb.Worksheets.Add("Warnings");
            wsW.Cell(1, 1).Value = "Warning"; wsW.Cell(1, 1).Style.Font.Bold = true;
            wsW.Cell(2, 1).Value = Disclaimer;
            for (int i = 0; i < presetResult.Warnings.Count; i++)
                wsW.Cell(i + 3, 1).Value = presetResult.Warnings[i];
            wsW.Column(1).Width = 80;

            wb.SaveAs(filePath);
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = $"Fire alarm circuit preset exported to {filePath}",
            Data       = new { filePath, loopCount = presetResult.LoopCount, deviceCount = presetResult.DeviceCount },
            Warnings   = [Disclaimer, .. presetResult.Warnings],
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
}
