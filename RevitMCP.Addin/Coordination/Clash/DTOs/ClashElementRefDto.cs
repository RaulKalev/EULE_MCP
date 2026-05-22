namespace RevitMCP.Addin.Coordination.Clash.DTOs;

public class ClashElementRefDto
{
    public long ElementId { get; set; }
    public string UniqueId { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Family { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Model { get; set; } = "Host";
    public long? LinkInstanceId { get; set; }
    public string? LinkName { get; set; }
}
