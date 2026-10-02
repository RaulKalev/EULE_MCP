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

    // IFC import can keep a space's body as non-visible geometry; the footprint fallback looks there too (#68).
    private static readonly Options HiddenGeomOptions = new()
    {
        ComputeReferences  = false,
        DetailLevel        = ViewDetailLevel.Fine,
        IncludeNonVisibleObjects = true
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
    public (List<Solid> Solids, List<Mesh> Meshes) GetSolidsAndMeshes(Element element, bool includeNonVisible = false)
    {
        var solids = new List<Solid>();
        var meshes = new List<Mesh>();
        try
        {
            var geomElem = element.get_Geometry(includeNonVisible ? HiddenGeomOptions : GeomOptions);
            if (geomElem != null) Collect(geomElem, solids, meshes);
        }
        catch
        {
            // Non-fatal — caller checks for empty lists
        }
        return (solids, meshes);
    }

    /// <summary>
    /// The element's curves and polylines (visible and non-visible) as plan segments, with how many
    /// curve objects there were and their lowest Z. IFC spaces carry their footprint as 2D curves,
    /// which the footprint fallback chains into an outline (#68).
    /// </summary>
    public (List<PlanSegment> Segments, int CurveCount, double MinZ) GetPlanSegments(Element element)
    {
        var segments = new List<PlanSegment>();
        var count = 0;
        var minZ = double.MaxValue;
        try
        {
            var geomElem = element.get_Geometry(HiddenGeomOptions);
            if (geomElem != null) CollectCurves(geomElem, segments, ref count, ref minZ);
        }
        catch
        {
            // Non-fatal — an empty list means no curve fallback
        }
        return (segments, count, minZ == double.MaxValue ? 0 : minZ);
    }

    private static void CollectCurves(GeometryElement geomElem, List<PlanSegment> segments, ref int count, ref double minZ)
    {
        foreach (var obj in geomElem)
        {
            IList<XYZ>? points = obj switch
            {
                Line line => new List<XYZ> { line.GetEndPoint(0), line.GetEndPoint(1) },
                Curve curve => curve.Tessellate(),
                PolyLine polyLine => polyLine.GetCoordinates(),
                _ => null
            };

            if (points != null)
            {
                count++;
                for (int i = 0; i + 1 < points.Count; i++)
                {
                    segments.Add(new PlanSegment(points[i].X, points[i].Y, points[i + 1].X, points[i + 1].Y));
                    minZ = Math.Min(minZ, Math.Min(points[i].Z, points[i + 1].Z));
                }
                continue;
            }

            if (obj is GeometryInstance gi)
            {
                var instanceGeom = gi.GetInstanceGeometry();
                if (instanceGeom != null)
                    CollectCurves(instanceGeom, segments, ref count, ref minZ);
            }
        }
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
