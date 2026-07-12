using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Query;
using RevitMCP.Core.Models;
using RevitMCP.Core.Safety;

namespace RevitMCP.Addin.Tools;

public class FindElementsByParameterTool : IRevitMcpTool
{
    public string Name => "revit_find_elements_by_parameter";
    public string Description => "Finds model elements matching one or more parameter filters. Supports instance/type parameters, partial name matching, and value operators (equals, contains, startsWith, isEmpty, greaterThan, etc.). Requires a category, filters, elementIds, useSelection, or summaryOnly=true — call revit_count_elements first if you don't know the model's categories yet.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Parameters;

    private static readonly ElementQueryEngine _engine = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null)
            return Task.FromResult(Fail(request, "No active document."));

        var filtersParsed = ToolArguments.GetFiltersWithWarnings(request.Arguments);

        var opts = new ElementQueryOptions
        {
            Category = ToolArguments.GetString(request.Arguments, "category"),
            UseSelection = ToolArguments.GetBool(request.Arguments, "useSelection"),
            ElementIds = ToolArguments.GetLongArray(request.Arguments, "elementIds").ToList(),
            Filters = filtersParsed.Items,
            ReturnParameters = ToolArguments.GetStringArray(request.Arguments, "returnParameters").ToList(),
            IncludeInstanceParameters = ToolArguments.GetBool(request.Arguments, "includeInstanceParameters", true),
            IncludeTypeParameters = ToolArguments.GetBool(request.Arguments, "includeTypeParameters", true),
            Limit = ToolArguments.GetInt(request.Arguments, "limit", 500),
            PageSize = ToolArguments.GetInt(request.Arguments, "pageSize", -1),
            Page = ToolArguments.GetInt(request.Arguments, "page", 0),
            MaxParametersPerElement = ToolArguments.GetInt(request.Arguments, "maxParametersPerElement", 0),
            TruncateStringLength = ToolArguments.GetInt(request.Arguments, "truncateStringLength", 0),
            SummaryOnly = ToolArguments.GetBool(request.Arguments, "summaryOnly", false)
        };

        // Nothing narrows the search at all — rather than scanning (and parameter-reading)
        // the entire model, give the agent a real category breakdown so it can ask the
        // user a concrete follow-up question instead of guessing.
        if (!opts.SummaryOnly &&
            !opts.UseSelection &&
            opts.ElementIds.Count == 0 &&
            string.IsNullOrWhiteSpace(opts.Category) &&
            opts.Filters.Count == 0)
        {
            var guidance = BuildNoNarrowingGuidance(uidoc.Document);
            return Task.FromResult(Fail(request, guidance));
        }

        var result = _engine.Query(uidoc.Document, uidoc, opts, cancellationToken);
        if (!result.Success)
            return Task.FromResult(Fail(request, result.Message));

        var warnings = result.Warnings.Concat(filtersParsed.Warnings).ToList();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Found {result.TotalMatched} matching elements, returned {result.Elements.Count}.",
            Data = new
            {
                totalMatched = result.TotalMatched,
                returned = result.Elements.Count,
                page = result.Page,
                pageSize = result.PageSize,
                hasMore = result.HasMore,
                nextPageToken = result.NextPageToken,
                summary = result.Summary,
                elements = result.Elements
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    /// <summary>
    /// Cheap category breakdown (no parameter reads) used to give the agent concrete
    /// narrowing options when a query has no scope at all.
    /// </summary>
    private static string BuildNoNarrowingGuidance(Document doc)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int total = 0;

        foreach (var element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
        {
            if (element.Category == null) continue;
            total++;
            var cat = element.Category.Name;
            counts[cat] = counts.GetValueOrDefault(cat) + 1;
        }

        var breakdown = QueryGuard.BuildNarrowingGuidance(total, counts);
        return $"This search has no category, parameter filter, elementIds, or selection — " +
               $"scanning the whole model risks freezing Revit on large projects. {breakdown} " +
               "Or set summaryOnly=true to get just the category/family breakdown.";
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
