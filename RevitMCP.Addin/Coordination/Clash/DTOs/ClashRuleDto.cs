namespace RevitMCP.Addin.Coordination.Clash.DTOs;

public class ClashRuleDto
{
    public string Name { get; set; } = string.Empty;
    public List<string> SourceCategories { get; set; } = new();
    public List<string> TargetCategories { get; set; } = new();
    /// <summary>Host | HostAndLinks</summary>
    public string TargetScope { get; set; } = "HostAndLinks";
    /// <summary>HardClash | Clearance</summary>
    public string ClashType { get; set; } = "HardClash";
    /// <summary>Required separation in mm for Clearance rules.</summary>
    public double ClearanceMm { get; set; } = 50;
    /// <summary>Intersection tolerance in mm for HardClash rules.</summary>
    public double ToleranceMm { get; set; } = 5;
    /// <summary>Low | Medium | High | Critical</summary>
    public string Severity { get; set; } = "Medium";
    public bool IncludeGenericModels { get; set; } = true;
    public bool IncludeImportedGeometry { get; set; } = true;
}
