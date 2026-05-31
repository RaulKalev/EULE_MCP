using System.Text;
using Newtonsoft.Json;
using RevitMCP.Core.Models;

namespace RevitMCP.Core.Safety;

/// <summary>
/// Final-boundary guard that prevents oversized JSON responses from being sent over the pipe.
/// Apply once at the serialization point — <c>PipeServer.HandleClientAsync</c> — rather than
/// inside each individual tool.
/// </summary>
public static class ResponseGuard
{
    /// <summary>
    /// Checks whether the serialized form of <paramref name="result"/> exceeds
    /// <see cref="QueryLimits.MaxResponseBytes"/>. If it does, returns a safe fallback
    /// <see cref="McpToolResult"/> that preserves identity fields but replaces <c>Data</c>
    /// with remediation guidance. Otherwise returns <paramref name="result"/> unchanged.
    /// </summary>
    public static McpToolResult GuardResult(McpToolResult result, QueryLimits? limits = null)
        => GuardSerialized(result, limits).Result;

    /// <summary>
    /// Same guard logic as <see cref="GuardResult"/>, but also returns the serialized JSON so the
    /// caller does not have to serialize a second time. On the hot path (every pipe response) this
    /// avoids a redundant full serialization of the result graph.
    /// </summary>
    public static (McpToolResult Result, string Json) GuardSerialized(McpToolResult result, QueryLimits? limits = null)
    {
        limits ??= QueryLimits.Default;

        var json = JsonConvert.SerializeObject(result, Formatting.None);
        var byteCount = Encoding.UTF8.GetByteCount(json);

        if (byteCount <= limits.MaxResponseBytes)
            return (result, json);

        var fallback = BuildOversizeFallback(result, byteCount, limits);
        return (fallback, JsonConvert.SerializeObject(fallback, Formatting.None));
    }

    private static McpToolResult BuildOversizeFallback(McpToolResult result, int byteCount, QueryLimits limits)
    {
        var warnings = new List<string>(result.Warnings)
        {
            $"[ResponseGuard] Response was {byteCount:N0} bytes — exceeded {limits.MaxResponseBytes:N0} byte limit. Response replaced with safe fallback."
        };

        return new McpToolResult
        {
            RequestId = result.RequestId,
            Success = false,
            Status = ToolErrorCodes.ResponseTooLarge,
            Message = $"Response too large ({byteCount:N0} bytes; limit {limits.MaxResponseBytes:N0} bytes). " +
                      "Narrow your query — use a category filter, element ids, a smaller pageSize, or limit parameter names.",
            Data = new SafeToolResult
            {
                Success = false,
                Error = ToolErrorCodes.ResponseTooLarge,
                Message = $"Serialized response was {byteCount:N0} bytes (limit {limits.MaxResponseBytes:N0} bytes).",
                Data = new { measuredBytes = byteCount, limitBytes = limits.MaxResponseBytes },
                SuggestedActions = new[]
                {
                    "Filter by category (e.g. category: 'Walls')",
                    "Use a smaller pageSize (e.g. pageSize: 20)",
                    "Specify parameterNames to reduce parameter count",
                    "Set includeTypeParameters: false",
                    "Provide specific elementIds instead of a broad query"
                }
            },
            Warnings = warnings,
            Errors = new List<string>(result.Errors),
            DurationMs = result.DurationMs
        };
    }
}
