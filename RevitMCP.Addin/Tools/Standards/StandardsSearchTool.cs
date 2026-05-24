using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Standards;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Standards;

/// <summary>
/// Searches indexed company standards sources with a keyword query.
/// Tool name: standards_search
/// </summary>
public class StandardsSearchTool : IRevitMcpTool
{
    public string Name        => "standards_search";
    public string Description => "Searches indexed company standards documents with a keyword query. Returns ranked snippets from matching documents. Run standards_index_sources first if no results appear.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory   Category   => ToolCategory.Documentation;

    private static readonly StandardsSourceService _sourceService  = new();
    private static readonly StandardsIndexer       _indexer        = new();
    private static readonly StandardsSearchService _searchService  = new(new StandardsIndexer(), new StandardsSourceService());

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        var query          = ToolArguments.GetString(request.Arguments, "query");
        var maxResults     = ToolArguments.GetInt(request.Arguments, "maxResults", 10);
        var sourceIdFilter = ToolArguments.GetString(request.Arguments, "sourceId");
        var discipline     = ToolArguments.GetString(request.Arguments, "discipline");

        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success   = false,
                Message   = "query is required."
            });
        }

        maxResults = Math.Clamp(maxResults, 1, 50);

        IEnumerable<string>? sourceIds = !string.IsNullOrWhiteSpace(sourceIdFilter)
            ? new[] { sourceIdFilter }
            : null;

        var results = _searchService.Search(
            query,
            maxResults,
            sourceIds,
            string.IsNullOrWhiteSpace(discipline) ? null : discipline);

        sw.Stop();

        if (results.Count == 0)
        {
            return Task.FromResult(new McpToolResult
            {
                RequestId  = request.RequestId,
                Success    = true,
                Message    = "No matching standards found. Try different keywords or run standards_index_sources to update the index.",
                Data       = new { query, results = results },
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = $"Found {results.Count} result(s) for query '{query}'.",
            Data       = new { query, results },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}
