namespace RevitMCP.Addin.Query;

public class GroupKeyOptions
{
    /// <summary>Category, Family, Type, Level, or Parameter</summary>
    public string Type { get; set; } = "Parameter";
    public string ParameterName { get; set; } = string.Empty;
    public string ParameterMatchMode { get; set; } = "Contains";
    public string Scope { get; set; } = "InstanceAndType";
}
