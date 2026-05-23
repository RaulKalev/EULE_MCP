using System.IO;
using ClosedXML.Excel;
using RevitMCP.Addin.Coordination.Clash.DTOs;
using RevitMCP.Addin.Export;

namespace RevitMCP.Addin.Coordination.Clash.Reports;

public class ClashExcelExporter
{
    public string Export(
        ClashRunResultDto run,
        string fileName,
        bool includeSummary,
        bool includeByRule,
        bool includeByLevel,
        bool includeByLinkedModel,
        bool includeByCategoryPair)
    {
        var outPath = new ExportPathService().ResolveFilePath(fileName);

        using var wb = new XLWorkbook();

        AddClashesSheet(wb, run.Clashes);

        if (includeSummary) AddSummarySheet(wb, run);
        if (includeByRule) AddGroupSheet(wb, run.Clashes, "By Rule", c => c.RuleName);
        if (includeByLevel) AddGroupSheet(wb, run.Clashes, "By Level", c => c.Level ?? "(no level)");
        if (includeByLinkedModel) AddGroupSheet(wb, run.Clashes, "By Linked Model",
            c => c.Target.Model == "Host" ? "Host" : (c.Target.LinkName ?? c.Target.Model));
        if (includeByCategoryPair) AddGroupSheet(wb, run.Clashes, "By Category Pair",
            c => $"{c.Source.Category} ↔ {c.Target.Category}");

        if (run.Warnings.Count > 0) AddWarningsSheet(wb, run.Warnings);

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        wb.SaveAs(outPath);
        return outPath;
    }

    private static void AddSummarySheet(IXLWorkbook wb, ClashRunResultDto run)
    {
        var ws = wb.Worksheets.Add("Summary");
        ws.Cell(1, 1).Value = "Run ID"; ws.Cell(1, 2).Value = run.RunId;
        ws.Cell(2, 1).Value = "Run At"; ws.Cell(2, 2).Value = run.RunAt.ToString("yyyy-MM-dd HH:mm:ss");
        ws.Cell(3, 1).Value = "Preset"; ws.Cell(3, 2).Value = run.PresetName ?? "-";
        ws.Cell(4, 1).Value = "Total Clashes"; ws.Cell(4, 2).Value = run.TotalClashes;
        ws.Column(1).Width = 20;
        ws.Column(2).Width = 40;
    }

    private static void AddClashesSheet(IXLWorkbook wb, List<ClashResultDto> clashes)
    {
        var ws = wb.Worksheets.Add("Clashes");
        var headers = new[]
        {
            "Clash ID", "Rule", "Type", "Severity", "Status",
            "Src ID", "Src Category", "Src Model",
            "Tgt ID", "Tgt Category", "Tgt Model",
            "Level", "Dist (mm)", "Req. Clearance (mm)",
            "Detection Method", "Confidence", "Message"
        };
        for (var i = 0; i < headers.Length; i++)
            ws.Cell(1, i + 1).Value = headers[i];

        ws.Row(1).Style.Font.Bold = true;

        for (var r = 0; r < clashes.Count; r++)
        {
            var c = clashes[r];
            var row = r + 2;
            ws.Cell(row, 1).Value = c.ClashId;
            ws.Cell(row, 2).Value = c.RuleName;
            ws.Cell(row, 3).Value = c.ClashType;
            ws.Cell(row, 4).Value = c.Severity;
            ws.Cell(row, 5).Value = c.Status;
            ws.Cell(row, 6).Value = c.Source.ElementId;
            ws.Cell(row, 7).Value = c.Source.Category;
            ws.Cell(row, 8).Value = c.Source.Model;
            ws.Cell(row, 9).Value = c.Target.ElementId;
            ws.Cell(row, 10).Value = c.Target.Category;
            ws.Cell(row, 11).Value = c.Target.Model;
            ws.Cell(row, 12).Value = c.Level ?? "";
            ws.Cell(row, 13).Value = c.DistanceMm.HasValue ? c.DistanceMm.Value.ToString("F1") : "";
            ws.Cell(row, 14).Value = c.RequiredClearanceMm.HasValue ? c.RequiredClearanceMm.Value.ToString("F1") : "";
            ws.Cell(row, 15).Value = c.DetectionMethod;
            ws.Cell(row, 16).Value = c.Confidence;
            ws.Cell(row, 17).Value = c.Message;
        }

        ws.Columns().AdjustToContents(1, Math.Min(clashes.Count + 2, 1000));
    }

    private static void AddGroupSheet(IXLWorkbook wb, List<ClashResultDto> clashes, string sheetName, Func<ClashResultDto, string> key)
    {
        var ws = wb.Worksheets.Add(sheetName);
        ws.Cell(1, 1).Value = sheetName.Replace("By ", "");
        ws.Cell(1, 2).Value = "Count";
        ws.Row(1).Style.Font.Bold = true;

        var groups = clashes.GroupBy(key).OrderByDescending(g => g.Count()).ToList();
        for (var i = 0; i < groups.Count; i++)
        {
            ws.Cell(i + 2, 1).Value = groups[i].Key;
            ws.Cell(i + 2, 2).Value = groups[i].Count();
        }
        ws.Column(1).AdjustToContents();
    }

    private static void AddWarningsSheet(IXLWorkbook wb, List<string> warnings)
    {
        var ws = wb.Worksheets.Add("Warnings");
        ws.Cell(1, 1).Value = "Warning";
        ws.Row(1).Style.Font.Bold = true;
        for (var i = 0; i < warnings.Count; i++)
            ws.Cell(i + 2, 1).Value = warnings[i];
        ws.Column(1).AdjustToContents();
    }
}
