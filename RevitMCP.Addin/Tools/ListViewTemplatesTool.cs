using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class ListViewTemplatesTool : IRevitMcpTool
{
    public string Name => "revit_list_view_templates";
    public string Description =>
        "Lists all view templates in the active document. " +
        "Optional: viewType (string, e.g. \"FloorPlan\") to filter by view type. " +
        "Returns: elementId, uniqueId, name, viewType, assignedViewCount (how many views use this template).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw  = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "No active document." });

        var viewTypeFilter = ToolArguments.GetString(request.Arguments, "viewType");

        // Count how many non-template views reference each template
        var usageCounts = new Dictionary<ElementId, int>();
        foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                     .Where(v => !v.IsTemplate && v.ViewTemplateId != ElementId.InvalidElementId))
        {
            usageCounts.TryGetValue(v.ViewTemplateId, out var c);
            usageCounts[v.ViewTemplateId] = c + 1;
        }

        IEnumerable<View> templates = new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .Where(v => v.IsTemplate);

        if (!string.IsNullOrWhiteSpace(viewTypeFilter))
            templates = templates.Where(v =>
                string.Equals(v.ViewType.ToString(), viewTypeFilter, StringComparison.OrdinalIgnoreCase));

        var result = templates
            .OrderBy(v => v.ViewType.ToString())
            .ThenBy(v => v.Name)
            .Select(v => new
            {
                elementId          = v.Id.Value,
                uniqueId           = v.UniqueId,
                name               = v.Name,
                viewType           = v.ViewType.ToString(),
                assignedViewCount  = usageCounts.TryGetValue(v.Id, out var cnt) ? cnt : 0
            })
            .ToList();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = $"Returned {result.Count} view templates.",
            Data       = new { count = result.Count, templates = result },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}
