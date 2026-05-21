namespace RevitMCP.Addin.Query;

public class ElementInfoDto
{
    public long ElementId { get; set; }
    public string UniqueId { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Family { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public long? TypeElementId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Level { get; set; } = string.Empty;
    public Dictionary<string, ParameterValueDto> Parameters { get; set; } = new();
}
