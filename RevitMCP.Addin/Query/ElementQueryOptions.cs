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
}
