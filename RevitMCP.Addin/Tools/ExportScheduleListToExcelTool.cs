using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClosedXML.Excel;
using RevitMCP.Addin.Export;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class ExportScheduleListToExcelTool : IRevitMcpTool
{
    public string Name => "revit_export_schedule_list_to_excel";
    public string Description => "Exports all schedules to a formatted .xlsx file with schedule name, category, and field names.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Schedules;

    private static readonly ExportPathService _pathService = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(Fail(request, "No active document."));

        var includeFields = ToolArguments.GetBool(request.Arguments, "includeFields", true);
        var fileName = ToolArguments.GetString(request.Arguments, "fileName", "Revit_Schedule_List.xlsx");

        var schedules = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSchedule))
            .Cast<ViewSchedule>()
            .Where(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule)
            .OrderBy(s => s.Name)
            .ToList();

        var filePath = _pathService.ResolveFilePath(fileName);

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Schedules");

        var headers = includeFields
            ? new[] { "ScheduleId", "ScheduleName", "Category", "FieldCount", "Fields" }
            : new[] { "ScheduleId", "ScheduleName", "Category", "FieldCount" };

        for (int i = 0; i < headers.Length; i++)
        {
            ws.Cell(1, i + 1).Value = headers[i];
            ws.Cell(1, i + 1).Style.Font.Bold = true;
        }

        int row = 2;
        foreach (var s in schedules)
        {
            var fields = new List<string>();
            try
            {
                var def = s.Definition;
                for (int i = 0; i < def.GetFieldCount(); i++)
                    fields.Add(def.GetField(i).GetName());
            }
            catch { }

            string catName = string.Empty;
            try
            {
                var catId = s.Definition?.CategoryId;
                if (catId != null && catId != ElementId.InvalidElementId)
                    catName = Autodesk.Revit.DB.Category.GetCategory(doc, catId)?.Name ?? string.Empty;
            }
            catch { }

            ws.Cell(row, 1).Value = s.Id.Value;
            ws.Cell(row, 2).Value = s.Name;
            ws.Cell(row, 3).Value = catName;
            ws.Cell(row, 4).Value = fields.Count;
            if (includeFields)
                ws.Cell(row, 5).Value = string.Join("; ", fields);
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
            Message = $"Exported {schedules.Count} schedules to Excel.",
            Data = new { filePath, scheduleCount = schedules.Count },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
