namespace RevitMCP.Addin.Query;

/// <summary>One annotation tag attached to a model element.</summary>
public class TagInfoDto
{
    public long TagId { get; set; }
    public string TagText { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Family { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public long? ViewId { get; set; }
    public string ViewName { get; set; } = string.Empty;
}
