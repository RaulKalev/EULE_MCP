using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Standards;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Standards;

/// <summary>
/// Returns a specific indexed standards chunk by ID plus optional surrounding context chunks.
/// Tool name: standards_get_document_chunk
/// </summary>
public class StandardsGetDocumentChunkTool : IRevitMcpTool
{
    public string Name        => "standards_get_document_chunk";
    public string Description => "Returns a specific indexed standards document chunk by its chunk ID, with optional surrounding context. Use chunk IDs returned by standards_search.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory   Category   => ToolCategory.Documentation;

    private static readonly StandardsIndexer _indexer = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        var chunkId  = ToolArguments.GetString(request.Arguments, "chunkId");
        var sourceId = ToolArguments.GetString(request.Arguments, "sourceId");
        var before   = ToolArguments.GetInt(request.Arguments, "contextBefore", 1);
        var after    = ToolArguments.GetInt(request.Arguments, "contextAfter",  1);

        if (string.IsNullOrWhiteSpace(chunkId))
        {
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success   = false,
                Message   = "chunkId is required."
            });
        }

        before = Math.Max(0, Math.Min(before, 5));
        after  = Math.Max(0, Math.Min(after,  5));

        var srcArg = string.IsNullOrWhiteSpace(sourceId) ? null : sourceId;

        if (before == 0 && after == 0)
        {
            var chunk = _indexer.GetChunkById(chunkId, srcArg);
            sw.Stop();
            if (chunk == null)
            {
                return Task.FromResult(new McpToolResult
                {
                    RequestId = request.RequestId,
                    Success   = false,
                    Message   = $"Chunk '{chunkId}' not found. Run standards_index_sources first."
                });
            }

            return Task.FromResult(new McpToolResult
            {
                RequestId  = request.RequestId,
                Success    = true,
                Message    = $"Chunk '{chunkId}' found.",
                Data       = new { chunk, contextChunks = Array.Empty<object>() },
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        var chunks = _indexer.GetChunksAround(chunkId, before, after, srcArg);
        sw.Stop();

        if (chunks.Count == 0)
        {
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success   = false,
                Message   = $"Chunk '{chunkId}' not found. Run standards_index_sources first."
            });
        }

        var target = chunks.FirstOrDefault(c => c.ChunkId.Equals(chunkId, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = $"Returned {chunks.Count} chunk(s) around '{chunkId}'.",
            Data       = new { targetChunk = target, contextChunks = chunks },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}
