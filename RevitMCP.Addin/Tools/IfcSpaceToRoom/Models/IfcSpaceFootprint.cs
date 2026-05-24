using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

/// <summary>
/// Internal result from <c>IfcSpaceGeometryExtractor</c> for one IFC Space element.
/// Contains Revit geometry types (CurveLoop, XYZ) and is therefore NOT serialized directly.
/// The serializable counterpart is <see cref="IfcGeometryItem"/>.
/// Phase 2: read-only geometry extraction — no document modifications.
/// </summary>
public class IfcSpaceFootprint
{
    /// <summary>Revit element id of the Generic Model / DirectShape in the linked document.</summary>
    public long LinkedElementId { get; set; }

    /// <summary>True when geometry was successfully extracted and validated.</summary>
    public bool Success { get; set; }

    /// <summary>Overall status of this extraction attempt.</summary>
    public string Status { get; set; } = IfcGeometryStatus.Error;

    // ── Footprint geometry (null when Success = false) ─────────────────────

    /// <summary>
    /// Outer boundary CurveLoop in host-document coordinates.
    /// Null when extraction failed or validation did not pass.
    /// </summary>
    public CurveLoop? OuterLoop { get; set; }

    /// <summary>
    /// Inner loops (holes / voids) in host-document coordinates.
    /// In Phase 2 MVP these are detected but not used for room boundary creation.
    /// </summary>
    public List<CurveLoop> InnerLoops { get; set; } = new();

    // ── Derived metrics ───────────────────────────────────────────────────

    /// <summary>Number of curves in the outer loop (0 if no loop).</summary>
    public int OuterLoopCurveCount { get; set; }

    /// <summary>Approximate area computed from the outer loop (square feet, Revit internal units).</summary>
    public double ApproxAreaSquareFeet { get; set; }

    /// <summary>Bottom elevation of the footprint in host coordinates (feet, Revit internal units).</summary>
    public double BottomElevationFeet { get; set; }

    // ── Placement point ───────────────────────────────────────────────────

    /// <summary>A point guaranteed to lie inside the outer loop, for future Room placement.</summary>
    public XYZ? PlacementPoint { get; set; }

    /// <summary>How the placement point was determined (Centroid / BoundingBoxCenter / SampledGridPoint / Failed).</summary>
    public string PlacementMethod { get; set; } = "Failed";

    // ── Diagnostics ───────────────────────────────────────────────────────

    /// <summary>Non-blocking advisory messages about this candidate.</summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>Blocking error messages that caused Success = false.</summary>
    public List<string> Errors { get; set; } = new();
}
