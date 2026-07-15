using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Query;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Queries elements inside a linked model (Revit or converted IFC link).
/// The returned element IDs live in the LINK's document — use them with
/// revit_select_linked_elements, not with tools that expect host-model ids.
/// </summary>
public class QueryLinkedElementsTool : IRevitMcpTool
{
    public string Name => "revit_query_linked_elements";
    public string Description =>
        "Queries elements inside a linked model (Revit link or IFC converted to a Revit link). " +
        "Required: linkInstanceId (from revit_list_clashable_links or ifc_list_links) plus category and/or nameFilter. " +
        "Optional: limit (default 500). " +
        "Returned elementIds belong to the LINKED document — select them with revit_select_linked_elements.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Selection;

    private static readonly CategoryResolver _categoryResolver = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(Fail(request, "No active document."));

        var linkInstanceId = ToolArguments.GetLong(request.Arguments, "linkInstanceId");
        var category = ToolArguments.GetString(request.Arguments, "category").Trim();
        var nameFilter = ToolArguments.GetString(request.Arguments, "nameFilter").Trim();
        var limit = Math.Max(1, ToolArguments.GetInt(request.Arguments, "limit", 500));

        if (linkInstanceId <= 0)
            return Task.FromResult(Fail(request, "linkInstanceId is required — use revit_list_clashable_links or ifc_list_links to find it."));
        if (string.IsNullOrEmpty(category) && string.IsNullOrEmpty(nameFilter))
            return Task.FromResult(Fail(request, "Provide category and/or nameFilter to narrow the query."));

        if (doc.GetElement(new ElementId(linkInstanceId)) is not RevitLinkInstance link)
            return Task.FromResult(Fail(request, $"Element {linkInstanceId} is not a Revit link instance."));
        var linkDoc = link.GetLinkDocument();
        if (linkDoc == null)
            return Task.FromResult(Fail(request, $"Link '{link.Name}' is not loaded — load it in Manage Links first."));

        var collector = new FilteredElementCollector(linkDoc).WhereElementIsNotElementType();
        if (!string.IsNullOrEmpty(category))
        {
            var resolve = _categoryResolver.Resolve(linkDoc, category);
            if (resolve.Category == null)
            {
                var suggestions = resolve.Suggestions.Count > 0 ? $" Did you mean: {string.Join(", ", resolve.Suggestions)}?" : string.Empty;
                return Task.FromResult(Fail(request, resolve.Message + suggestions));
            }
            collector = collector.OfCategoryId(resolve.Category.Id);
        }

        var elements = new List<object>();
        int totalMatched = 0;
        foreach (var el in collector)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (el.Category == null) continue;

            if (!string.IsNullOrEmpty(nameFilter))
            {
                var typeName = linkDoc.GetElement(el.GetTypeId())?.Name ?? string.Empty;
                if (!el.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) &&
                    !typeName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            totalMatched++;
            if (elements.Count >= limit) continue; // keep counting for totalMatched

            var levelName = el.LevelId != ElementId.InvalidElementId
                ? (linkDoc.GetElement(el.LevelId) as Level)?.Name ?? string.Empty
                : string.Empty;
            string family = string.Empty, type = string.Empty;
            if (el is FamilyInstance fi)
            {
                family = fi.Symbol?.Family?.Name ?? string.Empty;
                type = fi.Symbol?.Name ?? string.Empty;
            }
            else
            {
                type = linkDoc.GetElement(el.GetTypeId())?.Name ?? string.Empty;
            }

            elements.Add(new
            {
                elementId = el.Id.Value,
                uniqueId = el.UniqueId,
                category = el.Category.Name,
                family,
                type,
                name = el.Name,
                level = levelName
            });
        }

        sw.Stop();
        var warnings = new List<string>();
        if (totalMatched > elements.Count)
            warnings.Add($"Output capped at {limit} elements ({totalMatched} matched). Narrow with category/nameFilter or raise limit.");

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{totalMatched} element(s) matched in link '{link.Name}'. Returning {elements.Count}. " +
                      "Select them with revit_select_linked_elements.",
            Data = new
            {
                linkInstanceId,
                linkName = link.Name,
                totalMatched,
                returned = elements.Count,
                elements
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
