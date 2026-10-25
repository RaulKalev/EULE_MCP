using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Query;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class GetElementsInfoTool : IRevitMcpTool
{
    public string Name => "revit_get_elements_info";
    public string Description => "Returns structured element info and selected parameter values for selection, explicit element IDs, or category+filter queries. More controlled than revit_get_element_parameters.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    private static readonly ElementQueryEngine _engine = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return Task.FromResult(documentFailure!);
        var uidoc = ToolDocument.UiDocumentOf(uiapp, target);

        var filtersParsed = ToolArguments.GetFiltersWithWarnings(request.Arguments);

        var opts = new ElementQueryOptions
        {
            UseSelection = ToolArguments.GetBool(request.Arguments, "useSelection"),
            ElementIds = ToolArguments.GetLongArray(request.Arguments, "elementIds").ToList(),
            Category = ToolArguments.GetString(request.Arguments, "category"),
            Filters = filtersParsed.Items,
            ReturnParameters = ToolArguments.GetStringArray(request.Arguments, "parameterNames").ToList(),
            IncludeInstanceParameters = ToolArguments.GetBool(request.Arguments, "includeInstanceParameters", true),
            IncludeTypeParameters = ToolArguments.GetBool(request.Arguments, "includeTypeParameters", true),
            Limit = ToolArguments.GetInt(request.Arguments, "limit", 500),
            PageSize = ToolArguments.GetInt(request.Arguments, "pageSize", -1),
            Page = ToolArguments.GetInt(request.Arguments, "page", 0),
            MaxParametersPerElement = ToolArguments.GetInt(request.Arguments, "maxParametersPerElement", 0),
            TruncateStringLength = ToolArguments.GetInt(request.Arguments, "truncateStringLength", 0),
            SummaryOnly = ToolArguments.GetBool(request.Arguments, "summaryOnly", false),
            IncludeTags = ToolArguments.GetBool(request.Arguments, "includeTags", false),
            CompactResponse = ToolArguments.GetBool(request.Arguments, "compact", false)
        };

        if (!opts.SummaryOnly &&
            !opts.UseSelection &&
            opts.ElementIds.Count == 0 &&
            string.IsNullOrWhiteSpace(opts.Category))
        {
            return Task.FromResult(Fail(request, "Provide useSelection=true, elementIds, category, or set summaryOnly=true for a broad model summary."));
        }

        if (opts.UseSelection && uidoc == null)
            return Task.FromResult(ToolDocument.ActiveRequired(request, "useSelection", target.Document.Title));

        var result = _engine.Query(target.Document, uidoc, opts, cancellationToken);
        if (!result.Success)
            return Task.FromResult(Fail(request, result.Message));

        var warnings = result.Warnings.Concat(filtersParsed.Warnings).ToList();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Returned {result.Elements.Count} of {result.TotalMatched} matched elements.",
            Data = new
            {
                totalMatched = result.TotalMatched,
                returned = result.Elements.Count,
                page = result.Page,
                pageSize = result.PageSize,
                hasMore = result.HasMore,
                nextPageToken = result.NextPageToken,
                summary = result.Summary,
                elements = ElementQueryResponseFormatter.FormatElements(result.Elements, opts.CompactResponse)
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
