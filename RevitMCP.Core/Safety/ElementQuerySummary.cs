namespace RevitMCP.Core.Safety;

/// <summary>
/// Returned instead of a full element list when <c>SummaryOnly = true</c> or when the
/// query is too broad for detailed data to be useful.
/// </summary>
public sealed class ElementQuerySummary
{
    public int TotalElements { get; set; }

    /// <summary>Element counts per Revit category, sorted descending by count.</summary>
    public IReadOnlyList<CategoryCount> Categories { get; set; } = Array.Empty<CategoryCount>();

    /// <summary>Element counts per family name (FamilyInstance elements only), sorted descending. Capped at 50 entries.</summary>
    public IReadOnlyList<FamilyCount> Families { get; set; } = Array.Empty<FamilyCount>();

    public string Message { get; set; } = string.Empty;
}

public sealed class CategoryCount
{
    public string Category { get; set; } = string.Empty;
    public int Count { get; set; }
}

public sealed class FamilyCount
{
    public string Family { get; set; } = string.Empty;
    public int Count { get; set; }
}
