namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

/// <summary>
/// Result of matching an IFC Space's bottom elevation to the nearest host Level.
/// Phase 1: read-only.
/// </summary>
public class LevelMatchResult
{
    /// <summary>Revit element id of the matched Level, or null if none found.</summary>
    public long? LevelId { get; set; }

    /// <summary>Name of the matched Level, or null if no match.</summary>
    public string? LevelName { get; set; }

    /// <summary>Elevation of the matched Level in Revit internal units (feet).</summary>
    public double LevelElevationFeet { get; set; }

    /// <summary>Vertical offset between the space bottom and the matched Level (feet).</summary>
    public double OffsetFeet { get; set; }

    /// <summary>True when the absolute offset is within the user-configured tolerance.</summary>
    public bool IsWithinTolerance { get; set; }

    /// <summary>
    /// Match quality status:
    /// Ready — matched level within tolerance.
    /// Warning — matched level found but offset exceeds tolerance.
    /// Failed — no host levels exist or elevation could not be determined.
    /// </summary>
    public string Status { get; set; } = "Failed";
}
