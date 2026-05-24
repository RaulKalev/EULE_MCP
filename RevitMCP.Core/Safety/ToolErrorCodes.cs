namespace RevitMCP.Core.Safety;

/// <summary>
/// Machine-readable error codes set in <see cref="SafeToolResult.Error"/> and <c>McpToolResult.Status</c>
/// for query-safety failures.
/// </summary>
public static class ToolErrorCodes
{
    /// <summary>Query matches too many elements without sufficient filters.</summary>
    public const string QueryTooBroad = "QueryTooBroad";

    /// <summary>Requested page size or limit exceeds the allowed maximum.</summary>
    public const string QueryTooLarge = "QueryTooLarge";

    /// <summary>The serialized response exceeded the byte limit.</summary>
    public const string ResponseTooLarge = "ResponseTooLarge";

    /// <summary>The operation exceeded its allowed execution time.</summary>
    public const string Timeout = "Timeout";

    /// <summary>Geometry query was issued without a narrowing filter (category, ids, or view).</summary>
    public const string GeometryQueryTooBroad = "GeometryQueryTooBroad";

    /// <summary>The requested page size is out of the valid range.</summary>
    public const string InvalidPageSize = "InvalidPageSize";
}
