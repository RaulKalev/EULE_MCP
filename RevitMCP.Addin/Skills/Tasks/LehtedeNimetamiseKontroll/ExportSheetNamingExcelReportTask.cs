using System.Diagnostics;
using System.IO;
using ClosedXML.Excel;
using RevitMCP.Addin.Skills;
using RevitMCP.Addin.Skills.Models;
using RevitMCP.Addin.Skills.Tasks.LehtedeNimetamiseKontroll.Models;

namespace RevitMCP.Addin.Skills.Tasks.LehtedeNimetamiseKontroll;

/// <summary>
/// Task ID: export.excel-report
/// Writes a 4-sheet Excel report:
///   Kokkuvõte   — run summary
///   Probleemid  — all issues
///   Revit lehed — collected sheets with parsed components
///   Excel loetelu — document register (if loaded)
/// </summary>
internal sealed class ExportSheetNamingExcelReportTask : SheetNamingBaseTask
{
    public override string Id   => "export.excel-report";
    public override string Name => "Ekspordi Exceli aruanne";

    public override SkillTaskResult Run(SkillContext ctx, SkillTaskDefinition taskDef)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var sheets      = GetSheets(ctx) ?? new List<SheetNamingItem>();
            var issues      = GetOrCreateIssuesList(ctx);
            var register    = GetDocumentRegister(ctx) ?? new List<DocumentRegisterItem>();
            var projectNum  = GetProjectNumber(ctx);

            var fileNamePattern = SkillSettings.GetString(taskDef.Settings, "fileNamePattern",
                "Lehtede_Nimetamise_Kontroll_{ProjectNumber}.xlsx");
            var fileName = fileNamePattern.Replace("{ProjectNumber}", projectNum.Length > 0 ? projectNum : "projekt");

            var outputFolder = SkillSettings.GetString(taskDef.Settings, "outputFolder");
            if (string.IsNullOrWhiteSpace(outputFolder))
                outputFolder = ctx.OutputFolder;
            Directory.CreateDirectory(outputFolder);

            var filePath = Path.Combine(outputFolder, fileName);

            using var wb = new XLWorkbook();

            WriteKokkuvote(wb, ctx, sheets, issues, projectNum);
            WriteProbleemid(wb, issues);
            WriteRevitLehed(wb, sheets);
            WriteExcelLoetelu(wb, register);

            wb.SaveAs(filePath);

            return OkResult(0, $"Exceli aruanne salvestatud: {filePath}", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return FailResult($"Exceli aruande kirjutamine ebaõnnestus: {ex.Message}", sw.ElapsedMilliseconds);
        }
    }

    // ── Sheet 1: Kokkuvõte ──────────────────────────────────────────────────

    private static void WriteKokkuvote(IXLWorkbook wb, SkillContext ctx,
        List<SheetNamingItem> sheets, List<SheetNamingIssue> issues, string projectNumber)
    {
        var ws = wb.Worksheets.Add("Kokkuvõte");
        int row = 1;

        SetHeader(ws, row++, "Lehtede Nimetamise Kontroll — Kokkuvõte");
        ws.Cell(row, 1).Value = "Projekt:";     ws.Cell(row++, 2).Value = projectNumber;
        ws.Cell(row, 1).Value = "Dokument:";    ws.Cell(row++, 2).Value = ctx.ProjectName;
        ws.Cell(row, 1).Value = "Kontroll:";    ws.Cell(row++, 2).Value = DateTime.Now.ToString("dd.MM.yyyy HH:mm");
        row++;

        ws.Cell(row, 1).Value = "Kokku lehti:";         ws.Cell(row++, 2).Value = sheets.Count;
        ws.Cell(row, 1).Value = "Kokku probleeme:";     ws.Cell(row++, 2).Value = issues.Count;
        ws.Cell(row, 1).Value = "Kriitilised:";         ws.Cell(row++, 2).Value = issues.Count(i => i.Severity == "Kriitiline");
        ws.Cell(row, 1).Value = "Vead:";                ws.Cell(row++, 2).Value = issues.Count(i => i.Severity == "Viga");
        ws.Cell(row, 1).Value = "Hoiatused:";           ws.Cell(row++, 2).Value = issues.Count(i => i.Severity == "Hoiatus");
        ws.Cell(row, 1).Value = "Info:";                ws.Cell(row++, 2).Value = issues.Count(i => i.Severity == "Info");

        ws.Column(1).AdjustToContents();
        ws.Column(2).AdjustToContents();
    }

    // ── Sheet 2: Probleemid ─────────────────────────────────────────────────

    private static void WriteProbleemid(IXLWorkbook wb, List<SheetNamingIssue> issues)
    {
        var ws = wb.Worksheets.Add("Probleemid");
        var headers = new[] { "Tõsidus", "Lehe number", "Lehe nimi", "Reegel", "Probleem", "Soovitus", "Allikas" };
        WriteRowHeaders(ws, 1, headers);

        int row = 2;
        foreach (var issue in issues.OrderBy(i => SeverityOrder(i.Severity)).ThenBy(i => i.SheetNumber))
        {
            ws.Cell(row, 1).Value = issue.Severity;
            ws.Cell(row, 2).Value = issue.SheetNumber;
            ws.Cell(row, 3).Value = issue.SheetName;
            ws.Cell(row, 4).Value = issue.RuleId;
            ws.Cell(row, 5).Value = issue.MessageEt;
            ws.Cell(row, 6).Value = issue.RecommendationEt;
            ws.Cell(row, 7).Value = issue.Source;

            // Colour-code severity
            var colour = issue.Severity switch
            {
                "Kriitiline" => XLColor.FromHtml("#FFB3B3"),
                "Viga"       => XLColor.FromHtml("#FFD9B3"),
                "Hoiatus"    => XLColor.FromHtml("#FFFAB3"),
                _            => XLColor.FromHtml("#D9F2FF"),
            };
            ws.Row(row).Style.Fill.BackgroundColor = colour;
            row++;
        }

        ws.Columns().AdjustToContents();
    }

    // ── Sheet 3: Revit lehed ────────────────────────────────────────────────

    private static void WriteRevitLehed(IXLWorkbook wb, List<SheetNamingItem> sheets)
    {
        var ws = wb.Worksheets.Add("Revit lehed");
        var headers = new[] { "Lehe number", "Lehe nimi", "Projekt", "Staadium", "Eriala", "Grupp", "Jrk nr", "Revisioon", "Kohaasendaja" };
        WriteRowHeaders(ws, 1, headers);

        int row = 2;
        foreach (var s in sheets)
        {
            ws.Cell(row, 1).Value = s.SheetNumber;
            ws.Cell(row, 2).Value = s.SheetName;
            ws.Cell(row, 3).Value = s.ProjectNumber;
            ws.Cell(row, 4).Value = s.Stage;
            ws.Cell(row, 5).Value = s.Discipline;
            ws.Cell(row, 6).Value = s.Group;
            ws.Cell(row, 7).Value = s.Sequence;
            ws.Cell(row, 8).Value = s.Revision;
            ws.Cell(row, 9).Value = s.IsPlaceholder ? "Jah" : "Ei";
            row++;
        }

        ws.Columns().AdjustToContents();
    }

    // ── Sheet 4: Excel loetelu ──────────────────────────────────────────────

    private static void WriteExcelLoetelu(IXLWorkbook wb, List<DocumentRegisterItem> register)
    {
        var ws = wb.Worksheets.Add("Excel loetelu");

        if (register.Count == 0)
        {
            ws.Cell(1, 1).Value = "Exceli dokumentide loetelu ei ole laetud.";
            return;
        }

        var headers = new[] { "Dokumendi nr", "Nimetus", "Eriala", "Staadium", "Rida" };
        WriteRowHeaders(ws, 1, headers);

        int row = 2;
        foreach (var r in register)
        {
            ws.Cell(row, 1).Value = r.DocumentNumber;
            ws.Cell(row, 2).Value = r.DocumentName;
            ws.Cell(row, 3).Value = r.Discipline;
            ws.Cell(row, 4).Value = r.Stage;
            ws.Cell(row, 5).Value = r.RowNumber;
            row++;
        }

        ws.Columns().AdjustToContents();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static void SetHeader(IXLWorksheet ws, int row, string text)
    {
        var cell = ws.Cell(row, 1);
        cell.Value = text;
        cell.Style.Font.Bold = true;
        cell.Style.Font.FontSize = 13;
    }

    private static void WriteRowHeaders(IXLWorksheet ws, int row, string[] headers)
    {
        for (int c = 0; c < headers.Length; c++)
        {
            var cell = ws.Cell(row, c + 1);
            cell.Value = headers[c];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#4472C4");
            cell.Style.Font.FontColor = XLColor.White;
        }
    }

    private static int SeverityOrder(string sev) => sev switch
    {
        "Kriitiline" => 0,
        "Viga"       => 1,
        "Hoiatus"    => 2,
        "Info"       => 3,
        _            => 4,
    };
}
