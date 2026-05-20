using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class ListViewsTool : IRevitMcpTool
{
    public string Name => "revit_list_views";
    public string Description => "Lists all views in the active Revit document.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Views;

    private static string TryGetDiscipline(View v)
    {
        try { return v.Discipline.ToString(); }
        catch { return string.Empty; }
    }

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "No active document." });

        // Collect placed view IDs (views that appear on sheets)
        var placedViewIds = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .SelectMany(s => s.GetAllPlacedViews())
            .ToHashSet();

        var views = new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .Where(v => !v.IsTemplate && v.CanBePrinted)
            .OrderBy(v => v.ViewType.ToString())
            .ThenBy(v => v.Name)
            .Select(v => new
            {
                elementId = v.Id.Value,
                name = v.Name,
                viewType = v.ViewType.ToString(),
                hasViewTemplate = v.ViewTemplateId != ElementId.InvalidElementId,
                isPlacedOnSheet = placedViewIds.Contains(v.Id),
                scale = v.Scale > 0 ? $"1:{v.Scale}" : "N/A",
                discipline = TryGetDiscipline(v)
            })
            .ToList();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Returned {views.Count} views.",
            Data = new { count = views.Count, views },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}
