using System.Diagnostics;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Standards;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Standards;

/// <summary>
/// Lists all configured standards sources and their index status.
/// Tool name: standards_list_sources
/// </summary>
public class StandardsListSourcesTool : IRevitMcpTool, IBackgroundMcpTool
{
    public string Name        => "standards_list_sources";
    public string Description => "Lists all configured company standards sources and shows which are indexed and up to date. Sources are configured in %ProgramData%\\RKTools\\MCP\\Config\\StandardsSources.json.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory   Category   => ToolCategory.Documentation;

    private static readonly StandardsSourceService _sourceService = new();
    private static readonly StandardsIndexer       _indexer       = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var config = _sourceService.Load();

        var items = config.Sources.Select(src =>
        {
            var meta = _indexer.GetMeta(src.Id);
            return new
            {
                id          = src.Id,
                name        = src.Name,
                description = src.Description,
                path        = src.Path,
                discipline  = src.Discipline,
                enabled     = src.Enabled,
                indexed     = meta != null,
                indexedAt   = meta?.IndexedAt,
                totalChunks = meta != null ? (int?)meta.FileStamps.Count : null
            };
        }).ToList();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = $"{config.Sources.Count} source(s) configured, {items.Count(x => x.indexed)} indexed.",
            Data       = new { configFile = _sourceService.ConfigFilePath, sources = items },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}
