using Autodesk.Revit.DB;
using RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>
/// Creates Room Separation Lines in the host document from a CurveLoop footprint.
/// All curves are projected to the Level's elevation before creation so that
/// Revit places them on the correct plane.
/// Caller must supply an open Transaction; this class does not open or close transactions.
/// Phase 3.
/// </summary>
public class RoomBoundaryCreator
{
    /// <summary>
    /// Creates Room Separation Lines for every segment in <paramref name="outerLoop"/>.
    /// </summary>
    /// <param name="doc">Host document (transaction must be open).</param>
    /// <param name="outerLoop">
    /// Validated footprint CurveLoop in host coordinates.
    /// Each curve is projected to <paramref name="levelElevationFeet"/> before creation.
    /// </param>
    /// <param name="sketchPlane">
    /// SketchPlane at the Level's elevation. Create via <see cref="SketchPlaneResolver"/>.
    /// </param>
    /// <param name="view">
    /// A floor-plan ViewPlan for the Level. Returned by <c>BoundaryViewResolver.Resolve()</c>.
    /// </param>
    /// <param name="levelElevationFeet">Level elevation in Revit internal feet.</param>
    /// <returns>A <see cref="BoundaryCreationResult"/> with success status and created element ids.</returns>
    public BoundaryCreationResult Create(
        Document doc,
        CurveLoop outerLoop,
        SketchPlane sketchPlane,
        ViewPlan view,
        double levelElevationFeet)
    {
        var result = new BoundaryCreationResult();

        try
        {
            var curveArray = BuildProjectedCurveArray(outerLoop, levelElevationFeet);

            if (curveArray.Size == 0)
            {
                result.Error = "No curves remain after projecting loop to level elevation.";
                return result;
            }

            // NewRoomBoundaryLines returns a ModelCurveArray
            var created = doc.Create.NewRoomBoundaryLines(sketchPlane, curveArray, view);

            foreach (ModelCurve mc in created)
            {
                result.CreatedLineIds.Add(mc.Id.Value);
            }

            result.CreatedLineCount = result.CreatedLineIds.Count;
            result.Success = result.CreatedLineCount > 0;

            if (!result.Success)
                result.Error = "NewRoomBoundaryLines returned an empty collection.";
        }
        catch (Exception ex)
        {
            result.Error = $"Exception creating Room Separation Lines: {ex.Message}";
        }

        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Projects every curve in <paramref name="loop"/> to <paramref name="levelElevFeet"/>
    /// and returns a <c>CurveArray</c> suitable for <c>NewRoomBoundaryLines</c>.
    /// Lines are projected by adjusting Z on both endpoints.
    /// Arcs and other curve types are projected via <c>CreateTransformed</c> with a Z-translation.
    /// </summary>
    private static CurveArray BuildProjectedCurveArray(CurveLoop loop, double levelElevFeet)
    {
        var curveArray = new CurveArray();

        foreach (var curve in loop)
        {
            Curve projected = ProjectCurveToElevation(curve, levelElevFeet);
            curveArray.Append(projected);
        }

        return curveArray;
    }

    private static Curve ProjectCurveToElevation(Curve curve, double z)
    {
        if (curve is Line line)
        {
            var p0 = line.GetEndPoint(0);
            var p1 = line.GetEndPoint(1);
            return Line.CreateBound(
                new XYZ(p0.X, p0.Y, z),
                new XYZ(p1.X, p1.Y, z));
        }

        if (curve is Arc arc)
        {
            // Re-create the arc at the target elevation using 3 parametric points
            // (start, mid, end) projected to z
            var p0  = arc.GetEndPoint(0);
            var p1  = arc.Evaluate(0.5, true); // true = normalized (midpoint)
            var p2  = arc.GetEndPoint(1);

            return Arc.Create(
                new XYZ(p0.X, p0.Y, z),
                new XYZ(p2.X, p2.Y, z),
                new XYZ(p1.X, p1.Y, z));
        }

        // For any other curve type (NurbSpline, HermiteSpline, etc.)
        // use a Z-translation transform
        double dz = z - curve.GetEndPoint(0).Z;
        var translation = Transform.CreateTranslation(new XYZ(0, 0, dz));
        return curve.CreateTransformed(translation);
    }
}
