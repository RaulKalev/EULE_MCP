namespace RevitMCP.Addin.Coordination.Clash.DTOs;

public class ClashLocationDto
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}

public class ClashResultDto
{
    public string ClashId { get; set; } = string.Empty;
    public string RuleName { get; set; } = string.Empty;
    /// <summary>HardClash or Clearance</summary>
    public string ClashType { get; set; } = string.Empty;
    /// <summary>Low | Medium | High | Critical</summary>
    public string Severity { get; set; } = string.Empty;
    public ClashElementRefDto Source { get; set; } = new();
    public ClashElementRefDto Target { get; set; } = new();
    public ClashLocationDto? Location { get; set; }
    public string? Level { get; set; }
    /// <summary>Approximate distance in mm (clearance checks only).</summary>
    public double? DistanceMm { get; set; }
    public double? RequiredClearanceMm { get; set; }
    /// <summary>Intersection volume in cubic mm (hard clashes only, null = bbox-fallback).</summary>
    public double? IntersectionVolume { get; set; }
    public string Status { get; set; } = "New";
    public string Message { get; set; } = string.Empty;
}
