using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Coordination.Clash.Geometry;

public static class ClashSolidExtractor
{
    private static readonly Options _geomOptions = new()
    {
        ComputeReferences = false,
        DetailLevel = ViewDetailLevel.Fine,
        IncludeNonVisibleObjects = false
    };

    /// <summary>
    /// Tries to extract the first usable solid from an element.
    /// Returns null if no usable solid found; sets <paramref name="warning"/> to a diagnostic message.
    /// Warning-friendly: never throws.
    /// </summary>
    public static Solid? TryExtract(Element element, out string? warning)
    {
        warning = null;
        try
        {
            var geomElem = element.get_Geometry(_geomOptions);
            if (geomElem == null)
            {
                warning = $"No geometry on element {element.Id.Value} ({element.Category?.Name ?? "?"})";
                return null;
            }
            return FindFirstSolid(geomElem, element.Id.Value, out warning);
        }
        catch (Exception ex)
        {
            warning = $"Geometry extraction failed for element {element.Id.Value}: {ex.Message}";
            return null;
        }
    }

    private static Solid? FindFirstSolid(GeometryElement geomElem, long elementId, out string? warning)
    {
        warning = null;
        foreach (var obj in geomElem)
        {
            if (obj is Solid s && s.Volume > 1e-9) return s;
            if (obj is GeometryInstance gi)
            {
                var instanceGeom = gi.GetInstanceGeometry();
                if (instanceGeom == null) continue;
                var solid = FindFirstSolid(instanceGeom, elementId, out _);
                if (solid != null) return solid;
            }
        }
        warning = $"No usable solid for element {elementId}";
        return null;
    }
}
