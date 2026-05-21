namespace RevitMCP.Addin.Query;

public class ParameterReadOptions
{
    public bool IncludeInstanceParameters { get; set; } = true;
    public bool IncludeTypeParameters { get; set; } = true;
    public IReadOnlyList<string> ParameterNames { get; set; } = [];
    public string ParameterNameMatchMode { get; set; } = "Contains";
}
