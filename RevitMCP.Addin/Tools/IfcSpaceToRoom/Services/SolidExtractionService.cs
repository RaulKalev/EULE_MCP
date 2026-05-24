using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>
/// Extracts all usable solids from an element's geometry, including geometry nested
/// inside <c>GeometryInstance</c> objects (DirectShape subgeometry, family instances, etc.).
/// Phase 2: read-only.
/// </summary>
public class SolidExtractionService
{
    private static readonly Options GeomOptions = new()
    {
        ComputeReferences  = false,
        DetailLevel        = ViewDetailLevel.Fine,
        IncludeNonVisibleObjects = false
    };

    // Minimum volume to be considered a real solid (Revit internal cubic feet)
    private const double MinVolumeFt3 = 1e-9;

    /// <summary>
    /// Returns all usable solids found in <paramref name="element"/>'s geometry.
    /// Returns an empty list (never throws) when no solid geometry is present.
    /// </summary>
    public List<Solid> GetSolids(Element element)
    {
        var result = new List<Solid>();
        try
        {
            var geomElem = element.get_Geometry(GeomOptions);
            if (geomElem == null) return result;
            CollectSolids(geomElem, result);
        }
        catch
        {
            // Non-fatal — caller checks for empty list
        }
        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static void CollectSolids(GeometryElement geomElem, List<Solid> result)
    {
        foreach (var obj in geomElem)
        {
            if (obj is Solid solid)
            {
                // Accept solids with positive volume AND at least one face
                if (solid.Volume > MinVolumeFt3 && solid.Faces.Size > 0)
                    result.Add(solid);
            }
            else if (obj is GeometryInstance gi)
            {
                // GetInstanceGeometry() returns geometry in the document/linked-document
                // coordinate system (instance transform already applied).
                var instanceGeom = gi.GetInstanceGeometry();
                if (instanceGeom != null)
                    CollectSolids(instanceGeom, result);
            }
            // Meshes, PolyLines, and Curves are intentionally skipped —
            // they cannot be used for room boundary extraction.
        }
    }
}
