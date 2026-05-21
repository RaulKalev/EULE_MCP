using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClosedXML.Excel;
using RevitMCP.Addin.Export;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class ExportSheetListToExcelTool : IRevitMcpTool
{
    public string Name => "revit_export_sheet_list_to_excel";
    public string Description => "Exports all sheets to a formatted .xlsx file with sheet number, name, and placed views.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Sheets;

    private static readonly ExportPathService _pathService = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(Fail(request, "No active document."));

        var includePlacedViews = ToolArguments.GetBool(request.Arguments, "includePlacedViews", true);
        var fileName = ToolArguments.GetString(request.Arguments, "fileName", "Revit_Sheet_List.xlsx");

        var sheets = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .OrderBy(s => s.SheetNumber)
            .ToList();

        var filePath = _pathService.ResolveFilePath(fileName);

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Sheets");

        var headers = includePlacedViews
            ? new[] { "SheetId", "SheetNumber", "SheetName", "PlacedViewCount", "PlacedViews" }
            : new[] { "SheetId", "SheetNumber", "SheetName", "PlacedViewCount" };

        for (int i = 0; i < headers.Length; i++)
        {
            ws.Cell(1, i + 1).Value = headers[i];
            ws.Cell(1, i + 1).Style.Font.Bold = true;
        }

        int row = 2;
        foreach (var s in sheets)
        {
            var placedViews = s.GetAllPlacedViews()
                .Select(vid => doc.GetElement(vid))
                .Where(v => v != null)
                .Select(v => v!.Name)
                .ToList();

            ws.Cell(row, 1).Value = s.Id.Value;
            ws.Cell(row, 2).Value = s.SheetNumber;
            ws.Cell(row, 3).Value = s.Name;
            ws.Cell(row, 4).Value = placedViews.Count;
            if (includePlacedViews)
                ws.Cell(row, 5).Value = string.Join("; ", placedViews);
            row++;
        }

        ws.RangeUsed()?.SetAutoFilter();
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents();

        wb.SaveAs(filePath);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Exported {sheets.Count} sheets to Excel.",
            Data = new { filePath, sheetCount = sheets.Count },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
