namespace RevitMCP.Addin.Coordination.Clash.DTOs;

public class ClashRunResultDto
{
    public string RunId { get; set; } = string.Empty;
    public DateTime RunAt { get; set; }
    public string? PresetName { get; set; }
    public int TotalClashes { get; set; }
    public List<ClashResultDto> Clashes { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public Dictionary<string, int> CountByRule { get; set; } = new();
}
