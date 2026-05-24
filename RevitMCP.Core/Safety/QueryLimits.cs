namespace RevitMCP.Core.Safety;

/// <summary>
/// Configurable limits applied to MCP query and response operations.
/// Use <see cref="Default"/> for the standard production configuration.
/// </summary>
public sealed class QueryLimits
{
    /// <summary>Default number of elements returned per page when the caller does not specify.</summary>
    public int DefaultPageSize { get; set; } = 100;

    /// <summary>Hard upper bound on requested page size. Requests above this are clamped.</summary>
    public int MaxPageSize { get; set; } = 500;

    /// <summary>Maximum total matched elements allowed before a broad-query warning is added.</summary>
    public int MaxElements { get; set; } = 1000;

    /// <summary>Maximum number of parameters included per element. 0 = no limit.</summary>
    public int MaxParametersPerElement { get; set; } = 40;

    /// <summary>Maximum character length of any individual parameter value. 0 = no truncation.</summary>
    public int MaxStringLength { get; set; } = 500;

    /// <summary>Maximum serialized response size in UTF-8 bytes before the response is replaced by a safe fallback.</summary>
    public int MaxResponseBytes { get; set; } = 1_000_000;

    /// <summary>Maximum elements allowed in a geometry query without a narrow filter (category, ids, or view).</summary>
    public int MaxGeometryElements { get; set; } = 100;

    /// <summary>Per-request execution timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>When true, broad queries without filters are rejected rather than warned.</summary>
    public bool EnableStrictMode { get; set; } = false;

    /// <summary>Shared default instance. Never mutate — clone if you need custom values.</summary>
    public static QueryLimits Default { get; } = new();
}
