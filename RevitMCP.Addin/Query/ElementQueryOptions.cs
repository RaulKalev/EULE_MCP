namespace RevitMCP.Addin.Query;

public class ElementQueryOptions
{
    public string Category { get; set; } = string.Empty;
    public bool UseSelection { get; set; }
    public List<long> ElementIds { get; set; } = new();
    public List<ParameterFilterDto> Filters { get; set; } = new();
    public List<string> ReturnParameters { get; set; } = new();
    public string ReturnParameterMatchMode { get; set; } = "Contains";
    public bool IncludeInstanceParameters { get; set; } = true;
    public bool IncludeTypeParameters { get; set; } = true;
    public int Limit { get; set; } = 500;

    // Pagination — -1 means "use Limit as page size" (backward-compatible default)
    public int PageSize { get; set; } = -1;
    public int Page { get; set; } = 0;

    // Per-element safety limits — 0 means "no limit" (backward-compatible default)
    public int MaxParametersPerElement { get; set; } = 0;
    public int TruncateStringLength { get; set; } = 0;

    // When true, return category/family counts only — no element DTOs built
    public bool SummaryOnly { get; set; } = false;
}

