using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class PreviewDeleteViewsTool : IRevitMcpTool
{
    public string Name => "revit_preview_delete_views";
    public string Description =>
        "Previews which views would be deleted WITHOUT making any changes. " +
        "Required: viewIds (long array) OR (viewTypes + nameFilter) to select views. " +
        "Optional: skipPlacedOnSheets (bool, default true — protect views that are on sheets). " +
        "Returns proposals: viewId, viewName, viewType, isPlacedOnSheet, wouldDelete, reason.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw  = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "No active document." });

        var viewIds          = ToolArguments.GetLongArray(request.Arguments, "viewIds");
        var viewTypes        = ToolArguments.GetStringArray(request.Arguments, "viewTypes");
        var nameFilter       = ToolArguments.GetString(request.Arguments, "nameFilter");
        var skipPlacedOnSheets = ToolArguments.GetBool(request.Arguments, "skipPlacedOnSheets", true);

        if (viewIds.Length == 0 && viewTypes.Length == 0 && string.IsNullOrWhiteSpace(nameFilter))
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "Provide viewIds, viewTypes, or nameFilter." });

        var placedIds = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .SelectMany(s => s.GetAllPlacedViews())
            .ToHashSet();

        IEnumerable<View> views;
        if (viewIds.Length > 0)
        {
            views = viewIds.Select(id => doc.GetElement(new ElementId(id)) as View).Where(v => v != null)!;
        }
        else
        {
            views = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate);
            if (viewTypes.Length > 0)
                views = views.Where(v => viewTypes.Any(vt =>
                    string.Equals(v.ViewType.ToString(), vt, StringComparison.OrdinalIgnoreCase)));
            if (!string.IsNullOrWhiteSpace(nameFilter))
                views = views.Where(v => v.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase));
        }

        var proposals    = new List<object>();
        int wouldDelete  = 0;
        int wouldProtect = 0;

        foreach (var v in views)
        {
            bool placed = placedIds.Contains(v.Id);
            bool del    = !(skipPlacedOnSheets && placed);
            if (del) wouldDelete++; else wouldProtect++;

            proposals.Add(new
            {
                viewId           = v.Id.Value,
                viewName         = v.Name,
                viewType         = v.ViewType.ToString(),
                isPlacedOnSheet  = placed,
                wouldDelete      = del,
                reason           = del ? "Eligible for deletion" : "Protected: placed on sheet"
            });
        }

        var warnings = new List<string>();
        if (wouldDelete > 0)
            warnings.Add($"WARNING: {wouldDelete} view(s) will be permanently deleted. Use revit_delete_views to confirm.");

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = $"Preview: {wouldDelete} view(s) would be deleted, {wouldProtect} protected.",
            Data       = new { wouldDelete, wouldProtect, proposals },
            Warnings   = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}
