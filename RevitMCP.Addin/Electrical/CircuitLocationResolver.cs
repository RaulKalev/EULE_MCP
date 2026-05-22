using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Electrical;

/// <summary>
/// Resolves element locations in Revit internal units (feet).
/// Resolution order: LocationPoint → LocationCurve midpoint → BoundingBox center → null.
/// Must be called on the Revit API thread.
/// </summary>
public static class CircuitLocationResolver
{
    private static readonly string[] _sourceNames =
        ["LocationPoint", "LocationCurve midpoint", "BoundingBox center"];

    /// <summary>
    /// Returns (X, Y, Z) in Revit internal feet (plus source name),
    /// or null when no location is available.
    /// </summary>
    public static ((double X, double Y, double Z) Pt, string Source)? Resolve(Element element)
    {
        // 1. LocationPoint
        if (element.Location is LocationPoint lp)
            return ((lp.Point.X, lp.Point.Y, lp.Point.Z), "LocationPoint");

        // 2. LocationCurve midpoint
        if (element.Location is LocationCurve lc)
        {
            var p0 = lc.Curve.GetEndPoint(0);
            var p1 = lc.Curve.GetEndPoint(1);
            return (((p0.X + p1.X) / 2.0, (p0.Y + p1.Y) / 2.0, (p0.Z + p1.Z) / 2.0),
                    "LocationCurve midpoint");
        }

        // 3. BoundingBox center
        try
        {
            var bb = element.get_BoundingBox(null);
            if (bb != null)
                return (((bb.Min.X + bb.Max.X) / 2.0,
                          (bb.Min.Y + bb.Max.Y) / 2.0,
                          (bb.Min.Z + bb.Max.Z) / 2.0), "BoundingBox center");
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Builds an anonymous location DTO with coordinates in meters (for JSON output).
    /// </summary>
    public static object BuildLocationDto(Element element)
    {
        var r = Resolve(element);
        if (r == null)
            return new { x = 0.0, y = 0.0, z = 0.0, source = "Unavailable", available = false };

        var (pt, source) = r.Value;
        return new
        {
            x = Math.Round(LengthUnitHelper.ToMeters(pt.X), 3),
            y = Math.Round(LengthUnitHelper.ToMeters(pt.Y), 3),
            z = Math.Round(LengthUnitHelper.ToMeters(pt.Z), 3),
            source,
            available = true
        };
    }

    /// <summary>Available source strategy names (informational).</summary>
    public static IReadOnlyList<string> AvailableLocationSources => _sourceNames;
}
