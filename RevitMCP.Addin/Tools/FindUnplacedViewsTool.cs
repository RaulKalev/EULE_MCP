using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class FindUnplacedViewsTool : IRevitMcpTool
{
    public string Name => "revit_find_unplaced_views";
    public string Description =>
        "Finds views that are not placed on any sheet. " +
        "Optional: viewTypes (string array, e.g. [\"FloorPlan\",\"Section\"]), nameFilter (string), " +
        "includeTemplates (bool, default false), limit (int, default 0=all). " +
        "Returns: elementId, uniqueId, name, viewType, scale, discipline.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Documentation;

    private static string TryGetDiscipline(View v)
    {
        try { return v.Discipline.ToString(); } catch { return string.Empty; }
    }

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw  = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "No active document." });

        var viewTypesFilter  = ToolArguments.GetStringArray(request.Arguments, "viewTypes");
        var nameFilter       = ToolArguments.GetString(request.Arguments, "nameFilter");
        var includeTemplates = ToolArguments.GetBool(request.Arguments, "includeTemplates", false);
        var limit            = ToolArguments.GetInt(request.Arguments, "limit", 0);

        // Placed view IDs
        var placedIds = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .SelectMany(s => s.GetAllPlacedViews())
            .ToHashSet();

        IEnumerable<View> query = new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .Where(v => !placedIds.Contains(v.Id))
            .Where(v => includeTemplates || !v.IsTemplate)
            .Where(v => v.CanBePrinted);

        if (viewTypesFilter.Length > 0)
            query = query.Where(v => viewTypesFilter.Any(vt =>
                string.Equals(v.ViewType.ToString(), vt, StringComparison.OrdinalIgnoreCase)));

        if (!string.IsNullOrWhiteSpace(nameFilter))
            query = query.Where(v => v.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase));

        var ordered = query.OrderBy(v => v.ViewType.ToString()).ThenBy(v => v.Name);
        if (limit > 0) ordered = (IOrderedEnumerable<View>)ordered.Take(limit);

        var views = ordered.Select(v => new
        {
            elementId  = v.Id.Value,
            uniqueId   = v.UniqueId,
            name       = v.Name,
            viewType   = v.ViewType.ToString(),
            scale      = v.Scale > 0 ? $"1:{v.Scale}" : "N/A",
            discipline = TryGetDiscipline(v)
        }).ToList();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = $"Found {views.Count} unplaced view(s).",
            Data       = new { count = views.Count, views },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}
