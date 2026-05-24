namespace RevitMCP.Core.Safety;

/// <summary>
/// Optional pagination and detail-level controls sent by the caller alongside a query request.
/// All fields are optional; omitting them applies the <see cref="QueryLimits.Default"/> values.
/// </summary>
public sealed class QueryOptions
{
    /// <summary>Number of items to return in this page. Clamped to <see cref="QueryLimits.MaxPageSize"/>.</summary>
    public int? PageSize { get; set; }

    /// <summary>Zero-based page index for offset-based pagination.</summary>
    public int? Page { get; set; }

    /// <summary>Opaque continuation token from a previous response (alternative to Page).</summary>
    public string? PageToken { get; set; }

    /// <summary>"Basic" returns core identity fields only; "Full" includes all parameters.</summary>
    public string DetailLevel { get; set; } = "Basic";

    /// <summary>When true, return category/family counts only — no element list.</summary>
    public bool SummaryOnly { get; set; } = false;

    /// <summary>When true, include geometry data. Requires a narrow filter (category / ids / view).</summary>
    public bool IncludeGeometry { get; set; } = false;

    /// <summary>When false, parameters are omitted from the response to reduce size.</summary>
    public bool IncludeParameters { get; set; } = true;
}
