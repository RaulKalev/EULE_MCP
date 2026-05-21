using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClosedXML.Excel;
using RevitMCP.Addin.Export;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class ExportViewListToExcelTool : IRevitMcpTool
{
    public string Name => "revit_export_view_list_to_excel";
    public string Description => "Exports all views to a formatted .xlsx file with view type, scale, discipline, template, and sheet placement info.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Views;

    private static readonly ExportPathService _pathService = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(Fail(request, "No active document."));

        var includeTemplates = ToolArguments.GetBool(request.Arguments, "includeTemplates");
        var includeUnplaced = ToolArguments.GetBool(request.Arguments, "includeUnplacedViews", true);
        var fileName = ToolArguments.GetString(request.Arguments, "fileName", "Revit_View_List.xlsx");

        // Collect sheet placement info
        var sheetMap = new Dictionary<ElementId, (string Number, string Name)>();
        foreach (var sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
        {
            foreach (var vid in sheet.GetAllPlacedViews())
                sheetMap.TryAdd(vid, (sheet.SheetNumber, sheet.Name));
        }

        var views = new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .Where(v => (includeTemplates || !v.IsTemplate) && (includeUnplaced || sheetMap.ContainsKey(v.Id)))
            .OrderBy(v => v.ViewType.ToString())
            .ThenBy(v => v.Name)
            .ToList();

        var filePath = _pathService.ResolveFilePath(fileName);

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Views");

        // Header
        var headers = new[] { "ViewId", "ViewName", "ViewType", "IsTemplate", "Scale", "Discipline", "SheetNumber", "SheetName", "IsPlacedOnSheet" };
        for (int i = 0; i < headers.Length; i++)
        {
            ws.Cell(1, i + 1).Value = headers[i];
            ws.Cell(1, i + 1).Style.Font.Bold = true;
        }

        int row = 2;
        foreach (var v in views)
        {
            var onSheet = sheetMap.TryGetValue(v.Id, out var sheetInfo);
            ws.Cell(row, 1).Value = v.Id.Value;
            ws.Cell(row, 2).Value = v.Name;
            ws.Cell(row, 3).Value = v.ViewType.ToString();
            ws.Cell(row, 4).Value = v.IsTemplate;
            ws.Cell(row, 5).Value = v.Scale > 0 ? $"1:{v.Scale}" : "N/A";
            ws.Cell(row, 6).Value = TryGetDiscipline(v);
            ws.Cell(row, 7).Value = onSheet ? sheetInfo.Number : string.Empty;
            ws.Cell(row, 8).Value = onSheet ? sheetInfo.Name : string.Empty;
            ws.Cell(row, 9).Value = onSheet;
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
            Message = $"Exported {views.Count} views to Excel.",
            Data = new { filePath, viewCount = views.Count },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static string TryGetDiscipline(View v)
    {
        try { return v.Discipline.ToString(); }
        catch { return string.Empty; }
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
