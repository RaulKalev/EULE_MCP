namespace RevitMCP.Addin.Coordination.Clash.DTOs;

public class ClashPresetFile
{
    public int Version { get; set; } = 1;
    public List<ClashPresetDto> Presets { get; set; } = new();
}

public class ClashPresetDto
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<ClashRuleDto> Rules { get; set; } = new();
}
