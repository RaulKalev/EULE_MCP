namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

/// <summary>
/// Options that control how geometry is extracted from linked IFC Space elements.
/// All dimension tolerances are in millimetres for human readability; services convert
/// to Revit internal feet as needed.
/// </summary>
public class IfcGeometryExtractionOptions
{
    /// <summary>
    /// Maximum gap allowed between consecutive curve endpoints for them to be considered
    /// connected. Gaps smaller than this are snapped. In millimetres. Default 3 mm.
    /// </summary>
    public double EndpointSnapToleranceMm { get; set; } = 3.0;

    /// <summary>
    /// Curves shorter than this threshold are removed during loop cleanup.
    /// In millimetres. Default 1 mm.
    /// </summary>
    public double TinySegmentToleranceMm { get; set; } = 1.0;

    /// <summary>
    /// Maximum angle in degrees between a face normal and the vertical axis for the face
    /// to be classified as horizontal. Default 2 degrees.
    /// </summary>
    public double HorizontalFaceToleranceDegrees { get; set; } = 2.0;

    /// <summary>
    /// Minimum footprint area for a candidate to be considered valid.
    /// In square metres. Default 0.1 m².
    /// </summary>
    public double MinimumAreaM2 { get; set; } = 0.1;

    /// <summary>
    /// When true, loop curve coordinates are included in the response.
    /// Off by default to keep response payloads small.
    /// </summary>
    public bool IncludeLoopCoordinates { get; set; } = false;

    /// <summary>
    /// Maximum number of XYZ points returned per loop when IncludeLoopCoordinates is true.
    /// Default 250.
    /// </summary>
    public int MaxCoordinatePoints { get; set; } = 250;

    /// <summary>
    /// Maximum acceptable vertical distance between a space bottom elevation and the
    /// matched Level, in millimetres. Default 300 mm.
    /// </summary>
    public double LevelMatchToleranceMm { get; set; } = 300.0;

    // ── Derived helpers ───────────────────────────────────────────────────────

    /// <summary>Endpoint snap tolerance converted to Revit internal feet.</summary>
    public double EndpointSnapToleranceFeet => EndpointSnapToleranceMm / 304.8;

    /// <summary>Tiny segment threshold converted to Revit internal feet.</summary>
    public double TinySegmentToleranceFeet => TinySegmentToleranceMm / 304.8;

    /// <summary>Minimum area converted to Revit internal square feet.</summary>
    public double MinimumAreaSquareFeet => MinimumAreaM2 / 0.092903;
}
