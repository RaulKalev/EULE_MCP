using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

/// <summary>
/// Internal result from <c>RoomPlacementPointService</c>.
/// Contains a Revit XYZ — not serialized directly.
/// Phase 2: read-only.
/// </summary>
public class PlacementPointResult
{
    /// <summary>True when a valid interior point was found.</summary>
    public bool Success { get; set; }

    /// <summary>The point inside the footprint loop in host-document coordinates (feet).</summary>
    public XYZ? Point { get; set; }

    /// <summary>
    /// How the point was determined:
    /// Centroid, BoundingBoxCenter, SampledGridPoint, or Failed.
    /// </summary>
    public string Method { get; set; } = "Failed";

    /// <summary>Optional diagnostic message.</summary>
    public string? Warning { get; set; }
}
