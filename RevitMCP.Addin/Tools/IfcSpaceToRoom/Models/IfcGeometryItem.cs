namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

/// <summary>
/// JSON-serializable result for one IFC Space candidate from the
/// <c>preview_ifc_space_geometry</c> endpoint.
/// No Revit API types — safe for pipe serialization.
/// </summary>
public class IfcGeometryItem
{
    // ── Identity ──────────────────────────────────────────────────────────────

    /// <summary>Revit element id of the linked element (in the linked document).</summary>
    public long LinkedElementId { get; set; }

    // ── IFC Metadata ─────────────────────────────────────────────────────────

    /// <summary>Room number / reference read from IFC parameters.</summary>
    public string? Number { get; set; }

    /// <summary>Room name / long name read from IFC parameters.</summary>
    public string? Name { get; set; }

    /// <summary>Building storey name from the IFC model.</summary>
    public string? StoreyName { get; set; }

    public string? NumberSource { get; set; }
    public string? NameSource { get; set; }
    public string? StoreySource { get; set; }
    public double? DeclaredAreaM2 { get; set; }
    public string? AreaSource { get; set; }
    public string DetectionConfidence { get; set; } = "Confirmed";
    public string DetectionReason { get; set; } = "ExplicitIfcSpace";

    /// <summary>Name of the matched host Level.</summary>
    public string? TargetLevelName { get; set; }

    /// <summary>Revit element id of the matched host Level.</summary>
    public long? TargetLevelId { get; set; }

    // ── Geometry Status ───────────────────────────────────────────────────────

    /// <summary>
    /// Geometry extraction status. Only <c>GeometryReady</c> items are eligible
    /// for Phase 3 conversion.
    /// Possible values: see <see cref="IfcGeometryStatus"/>.
    /// </summary>
    public string Status { get; set; } = IfcGeometryStatus.Error;

    // ── Loop Metrics ──────────────────────────────────────────────────────────

    /// <summary>Total number of loops found (outer + inner). 0 if extraction failed.</summary>
    public int LoopCount { get; set; }

    /// <summary>Number of curve segments in the outer loop. 0 if no loop.</summary>
    public int OuterLoopCurveCount { get; set; }

    // ── Derived Metrics ───────────────────────────────────────────────────────

    /// <summary>
    /// Approximate footprint area derived from the outer loop in square metres.
    /// Null if geometry extraction failed.
    /// </summary>
    public double? ApproxAreaM2 { get; set; }

    /// <summary>
    /// Bottom elevation of the footprint in host coordinates, in millimetres.
    /// Null if elevation could not be determined.
    /// </summary>
    public double? BottomElevationMm { get; set; }

    /// <summary>
    /// A point inside the outer loop suitable for later Room placement.
    /// Null if no valid interior point could be found.
    /// </summary>
    public PointMm? PlacementPoint { get; set; }

    /// <summary>How the placement point was determined (Centroid / BoundingBoxCenter / SampledGridPoint / Failed).</summary>
    public string? PlacementMethod { get; set; }

    // ── Optional Loop Coordinates ─────────────────────────────────────────────

    /// <summary>
    /// Loop coordinate info, populated only when <c>includeLoopCoordinates = true</c>.
    /// Null otherwise.
    /// </summary>
    public List<IfcGeometryLoopInfo>? Loops { get; set; }

    // ── Diagnostics ───────────────────────────────────────────────────────────

    /// <summary>Non-blocking advisory messages about this element.</summary>
    public List<string> Warnings { get; set; } = new();
}
