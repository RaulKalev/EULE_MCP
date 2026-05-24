namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

/// <summary>
/// Options for the preview_ifc_spaces operation.
/// All fields have safe defaults so callers only need to supply LinkInstanceId.
/// </summary>
public class IfcSpacePreviewOptions
{
    /// <summary>Revit element id of the RevitLinkInstance to inspect.</summary>
    public long LinkInstanceId { get; set; }

    /// <summary>
    /// When true, compare each candidate against existing Rooms in the host document.
    /// Uses built-in Room data only (Level, Number, Name) — no shared parameters.
    /// </summary>
    public bool IncludeExistingRoomCheck { get; set; } = true;

    /// <summary>
    /// Maximum acceptable vertical distance between a space bottom elevation and the matched Level,
    /// expressed in millimetres. Default 300 mm.
    /// </summary>
    public double LevelMatchToleranceMm { get; set; } = 300.0;

    /// <summary>
    /// Maximum number of candidate spaces to return in one call.
    /// Protects against oversized payloads. Default 1000.
    /// </summary>
    public int MaxResults { get; set; } = 1000;
}
