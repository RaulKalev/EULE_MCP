using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Query;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class CountElementsTool : IRevitMcpTool
{
    public string Name => "revit_count_elements";
    public string Description => "Counts model elements, grouped by Category or FamilyAndType. Optionally filtered to a specific category.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    private static readonly CategoryResolver _resolver = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(Fail(request, "No active document."));

        var filterCategory = ToolArguments.GetString(request.Arguments, "category");
        var groupBy = ToolArguments.GetString(request.Arguments, "groupBy", "Category");
        var warnings = new List<string>();

        var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();

        if (!string.IsNullOrWhiteSpace(filterCategory))
        {
            var resolve = _resolver.Resolve(doc, filterCategory);
            if (resolve.Category == null)
            {
                var sug = resolve.Suggestions.Count > 0
                    ? $" Did you mean: {string.Join(", ", resolve.Suggestions)}?"
                    : string.Empty;
                return Task.FromResult(Fail(request, resolve.Message + sug));
            }
            if (resolve.Message.Contains("Resolved") && resolve.Category.Name != filterCategory)
                warnings.Add($"Category resolved to '{resolve.Category.Name}'.");
            collector = collector.OfCategoryId(resolve.Category.Id);
        }

        var elements = collector.Where(e => e.Category != null).ToList();
        int totalCount = elements.Count;
        object data;

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

            data = new { totalCount, groupBy = "FamilyAndType", categoryFilter = filterCategory, groups = grouped };
        }
        else
        {
            var grouped = elements
                .GroupBy(e => e.Category!.Name)
                .OrderByDescending(g => g.Count())
                .Select(g => new { category = g.Key, count = g.Count() })
                .ToList();

            data = new { totalCount, groupBy = "Category", categoryFilter = filterCategory, groups = grouped };
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Counted {totalCount} elements.",
            Data = data,
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
