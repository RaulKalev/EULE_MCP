namespace RevitMCP.Addin.ViewElements;

/// <summary>The part of one source (a view, a viewport, the sheet itself) that falls on the requested page.</summary>
public readonly struct ViewPageSlice
{
    public ViewPageSlice(int source, int skip, int take)
    {
        Source = source;
        Skip = skip;
        Take = take;
    }

    public int Source { get; }
    public int Skip { get; }
    public int Take { get; }
}

/// <summary>Pages through several sources as one list, so a sheet pages like a single view (#136). Pure.</summary>
public static class ViewResultPaging
{
    public static List<ViewPageSlice> Slice(IReadOnlyList<int> sourceCounts, int page, int pageSize)
    {
        var slices = new List<ViewPageSlice>();
        if (pageSize <= 0) return slices;
        var start = (long)Math.Max(0, page) * pageSize;
        var end = start + pageSize;
        long offset = 0;
        for (var i = 0; i < sourceCounts.Count; i++)
        {
            var count = sourceCounts[i];
            var from = Math.Max(start, offset);
            var to = Math.Min(end, offset + count);
            if (to > from) slices.Add(new ViewPageSlice(i, (int)(from - offset), (int)(to - from)));
            offset += count;
        }
        return slices;
    }

    public static bool HasMore(int total, int page, int pageSize) =>
        total > ((long)Math.Max(0, page) + 1) * pageSize;
}
