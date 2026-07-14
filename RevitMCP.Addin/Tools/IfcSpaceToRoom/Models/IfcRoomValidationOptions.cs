namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

/// <summary>
/// Options for the <c>validate_ifc_space_room_conversion</c> endpoint.
/// All dimension tolerances are in millimetres for human readability.
/// </summary>
public class IfcRoomValidationOptions : IfcMetadataMappingOptions
{
    // ── Scope ──────────────────────────────────────────────────────────────────

    /// <summary>Revit element id of the RevitLinkInstance to validate against.</summary>
    public long LinkInstanceId { get; set; }

    /// <summary>
    /// Optional filter — if non-empty, only these linked element IDs are validated.
    /// If empty, all IFC Space candidates in the link are processed.
    /// </summary>
    public List<long> LinkedElementIds { get; set; } = new();

    // ── Tolerances ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Maximum vertical offset (mm) for matching an IFC Space bottom elevation to a host Level.
    /// Default 300 mm.
    /// </summary>
    public double LevelMatchToleranceMm { get; set; } = 300.0;

    /// <summary>
    /// Maximum 2D distance (mm) between the IFC Space placement point and the Revit Room
    /// location point for them to be considered co-located.
    /// Used for Medium and Low confidence scoring.
    /// Default 1000 mm (1 m).
    /// </summary>
    public double LocationToleranceMm { get; set; } = 1000.0;

    /// <summary>
    /// Maximum relative area difference (percentage) between the IFC Space approximate area
    /// and the Revit Room area for them to be considered area-equivalent.
    /// Default 10 %.
    /// </summary>
    public double AreaTolerancePercent { get; set; } = 10.0;

    // ── Geometry ──────────────────────────────────────────────────────────────

    /// <summary>
    /// When true, run the Phase 2 geometry extraction pipeline to obtain a precise placement
    /// point and area for each IFC Space. Improves location and area comparison accuracy.
    /// When false, use only bounding-box centre and bbox area as approximations.
    /// Default true.
    /// </summary>
    public bool IncludeGeometryComparison { get; set; } = true;

    /// <summary>Endpoint snap tolerance for geometry extraction (mm). Default 3 mm.</summary>
    public double EndpointSnapToleranceMm { get; set; } = 3.0;

    /// <summary>Tiny segment removal threshold for geometry extraction (mm). Default 1 mm.</summary>
    public double TinySegmentToleranceMm { get; set; } = 1.0;

    // ── Output ────────────────────────────────────────────────────────────────

    /// <summary>Maximum number of validation items to return. Default 1000.</summary>
    public int MaxResults { get; set; } = 1000;
}
