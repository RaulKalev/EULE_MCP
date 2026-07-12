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

    /// <summary>
    /// Hard cap on elements scanned (and parameter-read) by a single narrowed query — one
    /// that has a category, explicit elementIds, or useSelection. Guards against a single
    /// huge category still freezing/crashing Revit on very large models.
    /// </summary>
    public int MaxScanElements { get; set; } = 20_000;

    /// <summary>
    /// Tighter scan cap applied when a query has no narrowing at all (no category,
    /// elementIds, or useSelection). This is the scenario that most often freezes or
    /// crashes Revit on large projects: an unbounded per-element parameter read across
    /// the entire model.
    /// </summary>
    public int MaxUnscopedScanElements { get; set; } = 2_000;

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

    /// <summary>Maximum requests waiting for the Revit API thread.</summary>
    public int MaxQueuedRequests { get; set; } = 64;

    /// <summary>Maximum queued requests processed by one ExternalEvent callback.</summary>
    public int MaxRequestsPerExternalEvent { get; set; } = 8;

    /// <summary>Maximum audited non-Revit I/O tools executing concurrently.</summary>
    public int MaxConcurrentBackgroundRequests { get; set; } = 1;

    /// <summary>Maximum requests waiting for a user approval decision.</summary>
    public int MaxPendingApprovals { get; set; } = 64;

    /// <summary>Minutes before an approval becomes stale and must be previewed again.</summary>
    public int ApprovalTimeoutMinutes { get; set; } = 10;

    /// <summary>When true, broad queries without filters are rejected rather than warned.</summary>
    public bool EnableStrictMode { get; set; } = false;

    /// <summary>Shared default instance. Never mutate — clone if you need custom values.</summary>
    public static QueryLimits Default { get; } = new();
}
