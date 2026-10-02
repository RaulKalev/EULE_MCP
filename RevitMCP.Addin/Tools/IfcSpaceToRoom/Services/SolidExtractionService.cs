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

    /// <summary>
    /// Solids and meshes of the element. IFC import can bring a space in as a mesh (or as a solid
    /// with a mesh for most of its volume); the footprint fallback needs those too (#68).
    /// </summary>
    public (List<Solid> Solids, List<Mesh> Meshes) GetSolidsAndMeshes(Element element)
    {
        var solids = new List<Solid>();
        var meshes = new List<Mesh>();
        try
        {
            var geomElem = element.get_Geometry(GeomOptions);
            if (geomElem != null) Collect(geomElem, solids, meshes);
        }
        catch
        {
            // Non-fatal — caller checks for empty lists
        }
        return (solids, meshes);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static void CollectSolids(GeometryElement geomElem, List<Solid> result) =>
        Collect(geomElem, result, null);

    private static void Collect(GeometryElement geomElem, List<Solid> solids, List<Mesh>? meshes)
    {
        foreach (var obj in geomElem)
        {
            if (obj is Solid solid)
            {
                // Accept solids with positive volume AND at least one face
                if (solid.Volume > MinVolumeFt3 && solid.Faces.Size > 0)
                    solids.Add(solid);
            }
            else if (obj is Mesh mesh)
            {
                if (meshes != null && mesh.NumTriangles > 0)
                    meshes.Add(mesh);
            }
            else if (obj is GeometryInstance gi)
            {
                // GetInstanceGeometry() returns geometry in the document/linked-document
                // coordinate system (instance transform already applied).
                var instanceGeom = gi.GetInstanceGeometry();
                if (instanceGeom != null)
                    Collect(instanceGeom, solids, meshes);
            }
            // PolyLines and Curves are skipped — they cannot bound a room.
        }
    }
}
