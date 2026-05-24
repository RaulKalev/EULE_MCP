namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

/// <summary>
/// JSON-serializable description of one CurveLoop from an IFC Space footprint.
/// Coordinates are in millimetres and only populated when the caller opts-in with
/// <c>includeLoopCoordinates = true</c>.
/// </summary>
public class IfcGeometryLoopInfo
{
    /// <summary>0 = outer loop, 1+ = inner loops / holes.</summary>
    public int LoopIndex { get; set; }

    /// <summary>True for the outer boundary loop.</summary>
    public bool IsOuter { get; set; }

    /// <summary>Number of curve segments in the loop.</summary>
    public int CurveCount { get; set; }

    /// <summary>
    /// Tessellated XYZ points along the loop boundary, in millimetres, in host coordinates.
    /// Null when <c>includeLoopCoordinates = false</c>.
    /// </summary>
    public List<PointMm>? Points { get; set; }

    /// <summary>Approximate perimeter length of this loop in millimetres.</summary>
    public double PerimeterMm { get; set; }
}

/// <summary>A simple 3-component point in millimetres.</summary>
public class PointMm
{
    public double XMm { get; set; }
    public double YMm { get; set; }
    public double ZMm { get; set; }
}
