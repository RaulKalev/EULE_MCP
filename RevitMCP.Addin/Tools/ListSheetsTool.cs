using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class ListSheetsTool : IRevitMcpTool
{
    public string Name => "revit_list_sheets";
    public string Description => "Lists all sheets in the active Revit document.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Sheets;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "No active document." });

        var sheets = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .OrderBy(s => s.SheetNumber)
            .Select(s =>
            {
                var placedViews = s.GetAllPlacedViews()
                    .Select(vid => doc.GetElement(vid))
                    .Where(v => v != null)
                    .Select(v => v!.Name)
                    .ToList();

                return new
                {
                    elementId = s.Id.Value,
                    sheetNumber = s.SheetNumber,
                    sheetName = s.Name,
                    placedViewCount = placedViews.Count,
                    placedViews
                };
            })
            .ToList();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Returned {sheets.Count} sheets.",
            Data = new { count = sheets.Count, sheets },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}
