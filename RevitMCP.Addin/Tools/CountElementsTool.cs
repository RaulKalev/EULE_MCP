using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class CountElementsTool : IRevitMcpTool
{
    public string Name => "revit_count_elements";
    public string Description => "Counts model elements, grouped by Category or FamilyAndType. Optionally filtered to a specific category.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "No active document." });

        var filterCategory = ToolArguments.GetString(request.Arguments, "category");
        var groupBy = ToolArguments.GetString(request.Arguments, "groupBy", "Category");

        var collector = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType();

        // Apply category filter if specified
        if (!string.IsNullOrWhiteSpace(filterCategory))
        {
            var cat = FindCategory(doc, filterCategory);
            if (cat == null)
                return Task.FromResult(new McpToolResult
                {
                    RequestId = request.RequestId,
                    Success = false,
                    Message = $"Category not found: '{filterCategory}'. Check spelling."
                });

            collector = collector.OfCategoryId(cat.Id);
        }

        var elements = collector
            .Where(e => e.Category != null)
            .ToList();

        object data;
        int totalCount = elements.Count;

        if (groupBy.Equals("FamilyAndType", StringComparison.OrdinalIgnoreCase))
        {
            var grouped = elements
                .GroupBy(e =>
                {
                    if (e is FamilyInstance fi)
                        return $"{fi.Symbol?.Family?.Name ?? "?"} : {fi.Symbol?.Name ?? "?"}";
                    var typeEl = doc.GetElement(e.GetTypeId());
                    return $"{e.Category!.Name} : {typeEl?.Name ?? e.Name}";
                })
                .OrderByDescending(g => g.Count())
                .Select(g => new { type = g.Key, count = g.Count() })
                .ToList();

            data = new
            {
                totalCount,
                groupBy = "FamilyAndType",
                categoryFilter = filterCategory,
                groups = grouped
            };
        }
        else
        {
            var grouped = elements
                .GroupBy(e => e.Category!.Name)
                .OrderByDescending(g => g.Count())
                .Select(g => new { category = g.Key, count = g.Count() })
                .ToList();

            data = new
            {
                totalCount,
                groupBy = "Category",
                categoryFilter = filterCategory,
                groups = grouped
            };
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Counted {totalCount} elements.",
            Data = data,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static Autodesk.Revit.DB.Category? FindCategory(Document doc, string name)
    {
        foreach (Autodesk.Revit.DB.Category cat in doc.Settings.Categories)
        {
            if (string.Equals(cat.Name, name, StringComparison.OrdinalIgnoreCase))
                return cat;
        }
        return null;
    }
}
