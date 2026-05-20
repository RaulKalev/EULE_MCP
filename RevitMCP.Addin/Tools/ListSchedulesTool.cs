using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class ListSchedulesTool : IRevitMcpTool
{
    public string Name => "revit_list_schedules";
    public string Description => "Lists all schedules in the active Revit document.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Schedules;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "No active document." });

        var schedules = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSchedule))
            .Cast<ViewSchedule>()
            .Where(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule)
            .OrderBy(s => s.Name)
            .Select(s =>
            {
                var fields = new List<string>();
                try
                {
                    var def = s.Definition;
                    for (int i = 0; i < def.GetFieldCount(); i++)
                        fields.Add(def.GetField(i).GetName());
                }
                catch { }

                return new
                {
                    elementId = s.Id.Value,
                    name = s.Name,
                    category = s.Definition?.CategoryId is { } catId && catId != ElementId.InvalidElementId
                        ? Autodesk.Revit.DB.Category.GetCategory(doc, catId)?.Name ?? string.Empty
                        : string.Empty,
                    fieldCount = fields.Count,
                    fields
                };
            })
            .ToList();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Returned {schedules.Count} schedules.",
            Data = new { count = schedules.Count, schedules },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}
