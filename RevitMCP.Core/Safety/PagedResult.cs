namespace RevitMCP.Core.Safety;

/// <summary>
/// Wraps a page of query results together with pagination metadata.
/// Returned by tools that support offset-based paging.
/// </summary>
public sealed class PagedResult<T>
{
    public bool Success { get; set; } = true;

    /// <summary>Always "paged" to allow callers to detect this wrapper.</summary>
    public string Mode { get; set; } = "paged";

    public int ItemsReturned { get; set; }
    public int TotalAvailable { get; set; }
    public int PageSize { get; set; }

    /// <summary>Zero-based index of this page.</summary>
    public int Page { get; set; }

    /// <summary>Non-null when a next page exists.</summary>
    public string? NextPageToken { get; set; }

    /// <summary>True when TotalAvailable > ItemsReturned and the caller has not reached the last page.</summary>
    public bool HasMore { get; set; }

    public string? Warning { get; set; }

    public IReadOnlyList<T> Items { get; set; } = Array.Empty<T>();
}
