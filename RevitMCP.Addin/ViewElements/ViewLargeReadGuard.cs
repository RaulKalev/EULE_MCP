namespace RevitMCP.Addin.ViewElements;

/// <summary>The shape of a list read with <c>view</c>, for the large-view guard.</summary>
public sealed class ViewReadShape
{
    public int Total { get; set; }
    public bool HasCategory { get; set; }
    public bool HasFilters { get; set; }
    public bool HasElementIds { get; set; }
    public bool UseSelection { get; set; }
    public bool SummaryOnly { get; set; }
    /// <summary>The caller's pageSize argument; -1 or 0 means not given.</summary>
    public int RequestedPageSize { get; set; } = -1;
    public int RequestedPage { get; set; }
}

public sealed class ViewNextCall
{
    public string Tool { get; set; } = string.Empty;
    public Dictionary<string, object> Args { get; set; } = new();
    public string Why { get; set; } = string.Empty;
}

/// <summary>
/// #136: a list read of a large view with nothing to narrow it answers with counts per category and the calls to
/// continue with, instead of reading the elements. Asking for a page (pageSize or page) is the way through it. Pure.
/// </summary>
public static class ViewLargeReadGuard
{
    public const int Threshold = 200;
    public const string Status = "view_large_read";
    public const int MaxCategoryCalls = 5;
    public const int PagedReadSize = 200;

    public static bool Applies(ViewReadShape read) =>
        read.Total >= Threshold
        && !read.HasCategory && !read.HasFilters && !read.HasElementIds && !read.UseSelection && !read.SummaryOnly
        && read.RequestedPageSize <= 0 && read.RequestedPage <= 0;

    /// <summary>The calls to continue with: the largest categories first, then paging through everything.</summary>
    public static List<ViewNextCall> Next(
        string tool, long viewId, string? document, string scope, IEnumerable<KeyValuePair<string, int>> categoryCounts)
    {
        var calls = categoryCounts
            .OrderByDescending(c => c.Value)
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .Take(MaxCategoryCalls)
            .Select(c => new ViewNextCall
            {
                Tool = tool,
                Args = WithView(viewId, document, scope, new Dictionary<string, object> { ["category"] = c.Key }),
                Why = $"Read the {c.Value} '{c.Key}' elements in the view."
            })
            .ToList();
        calls.Add(new ViewNextCall
        {
            Tool = tool,
            Args = WithView(viewId, document, scope, new Dictionary<string, object> { ["pageSize"] = PagedReadSize, ["page"] = 0 }),
            Why = "Page through every element in the view."
        });
        return calls;
    }

    private static Dictionary<string, object> WithView(long viewId, string? document, string scope, Dictionary<string, object> args)
    {
        args["view"] = viewId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (scope != "all") args["scope"] = scope;
        if (!string.IsNullOrWhiteSpace(document)) args["document"] = document!;
        return args;
    }
}
