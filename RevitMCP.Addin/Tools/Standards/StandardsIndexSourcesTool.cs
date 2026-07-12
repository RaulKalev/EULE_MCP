using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Standards;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Standards;

/// <summary>
/// (Re-)indexes one or all company standards sources into the local cache.
/// Tool name: standards_index_sources
/// </summary>
public class StandardsIndexSourcesTool : IRevitMcpTool, IBackgroundMcpTool
{
    public string Name        => "standards_index_sources";
    public string Description => "Indexes (or re-indexes) company standards sources into the local search cache. Pass sourceId to re-index a specific source, or omit to index all enabled sources. Set force=true to reprocess unchanged files.";
    public ToolPermission Permission => ToolPermission.ReadOnly;   // Writes only to local AppData cache, not source documents
    public ToolCategory   Category   => ToolCategory.Documentation;

    private static readonly StandardsSourceService _sourceService = new();
    private static readonly StandardsIndexer       _indexer       = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        var sourceIdFilter = ToolArguments.GetString(request.Arguments, "sourceId");
        var force          = ToolArguments.GetBool(request.Arguments, "force", false);

        var sources = _sourceService.GetEnabledSources();
        if (!string.IsNullOrWhiteSpace(sourceIdFilter))
            sources = sources.Where(s => s.Id == sourceIdFilter).ToList();

        if (sources.Count == 0)
        {
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success   = false,
                Message   = string.IsNullOrWhiteSpace(sourceIdFilter)
                    ? "No enabled standards sources configured. Add sources to StandardsSources.json."
                    : $"No enabled source found with id '{sourceIdFilter}'."
            });
        }

        var results = sources.Select(src => _indexer.IndexSource(src, force)).ToList();
        var errors  = results.Where(r => r.Error != null).ToList();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = !errors.Any(),
            Message    = errors.Any()
                ? $"Indexed {results.Count - errors.Count}/{results.Count} sources. {errors.Count} error(s)."
                : $"Successfully indexed {results.Count} source(s). Total chunks: {results.Sum(r => r.TotalChunks)}.",
            Data       = new { results },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}
