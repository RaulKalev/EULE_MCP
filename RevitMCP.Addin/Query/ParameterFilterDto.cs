namespace RevitMCP.Addin.Query;

public class ParameterFilterDto
{
    public string ParameterName { get; set; } = string.Empty;
    public string MatchMode { get; set; } = "Contains";
    public string Operator { get; set; } = "equals";
    public string Value { get; set; } = string.Empty;
    public string Scope { get; set; } = "InstanceAndType";
}
