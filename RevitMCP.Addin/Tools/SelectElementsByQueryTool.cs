using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Query;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class SelectElementsByQueryTool : IRevitMcpTool
{
    public string Name => "revit_select_elements_by_query";
    public string Description => "Selects elements in the active Revit UI based on category and parameter filters. Does not modify model data.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Selection;

    private static readonly ElementQueryEngine _engine = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null)
            return Task.FromResult(Fail(request, "No active document."));

        var filtersParsed = ToolArguments.GetFiltersWithWarnings(request.Arguments);
        var replaceSelection = ToolArguments.GetBool(request.Arguments, "replaceSelection", true);
        var zoomToSelection = ToolArguments.GetBool(request.Arguments, "zoomToSelection");
        var limit = ToolArguments.GetInt(request.Arguments, "limit", 500);

        var opts = new ElementQueryOptions
        {
            Category = ToolArguments.GetString(request.Arguments, "category"),
            Filters = filtersParsed.Items,
            IncludeInstanceParameters = true,
            IncludeTypeParameters = true,
            Limit = limit
        };

        if (string.IsNullOrWhiteSpace(opts.Category) && filtersParsed.Items.Count == 0)
            return Task.FromResult(Fail(request, "Provide at least a category or filters."));

        var result = _engine.Query(uidoc.Document, uidoc, opts);
        if (!result.Success)
            return Task.FromResult(Fail(request, result.Message));

        if (result.Elements.Count == 0)
            return Task.FromResult(Fail(request, "No elements matched the query."));

        var validIds = result.Elements
            .Select(e => new ElementId(e.ElementId))
            .ToList();

        ICollection<ElementId> finalIds;
        if (replaceSelection)
        {
            finalIds = validIds;
        }
        else
        {
            var existing = uidoc.Selection.GetElementIds().ToList();
            existing.AddRange(validIds);
            finalIds = existing.Distinct().ToList();
        }

        uidoc.Selection.SetElementIds(finalIds);

        if (zoomToSelection && validIds.Count > 0)
        {
            try { uidoc.ShowElements(validIds); }
            catch { }
        }

        var warnings = result.Warnings.Concat(filtersParsed.Warnings).ToList();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Selected {validIds.Count} elements from query ({result.TotalMatched} matched total).",
            Data = new
            {
                selectedCount = validIds.Count,
                totalMatched = result.TotalMatched,
                selectedElementIds = validIds.Select(id => id.Value).ToList()
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
