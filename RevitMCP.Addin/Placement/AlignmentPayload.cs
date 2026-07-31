using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Placement;

/// <summary>
/// Shapes an <see cref="AlignmentPlan"/> into the response payload, and works out the rotation that
/// would square an element to its chosen surface. Shared so the preview reports exactly what the
/// write tool will do.
/// </summary>
internal static class AlignmentPayload
{
    /// <summary>
    /// The rotation about the vertical axis, in radians, that turns the element to face away from
    /// its surface. Null when rotation was not requested, the element has no facing direction, or
    /// the surface is horizontal — spinning about Z cannot square anything to a ceiling or floor.
    /// </summary>
    public static double? RotationRadians(Document doc, AlignmentPlan plan, AlignmentOptions options)
    {
        if (!options.RotateToSurface || plan.Chosen == null)
            return null;
        if (plan.Chosen.SurfaceKind != AlignmentMath.SurfaceWall)
            return null;

        if (doc.GetElement(new ElementId(plan.ElementId)) is not FamilyInstance instance)
            return null;

        XYZ facing;
        try { facing = instance.FacingOrientation; }
        catch { return null; }

        if (facing == null || facing.IsZeroLength())
            return null;

        var rotation = AlignmentMath.RotationAboutZ(
            AlignmentService.ToVec(facing), plan.Chosen.SurfaceNormal);

        // Ignore rotations small enough to be numerical noise.
        return rotation.HasValue && Math.Abs(rotation.Value) > 1e-4 ? rotation : null;
    }

    public static object Describe(
        AlignmentPlan plan,
        AlignmentOptions options,
        int maxAlternates,
        bool willMove,
        double? rotationRadians = null,
        string? outcome = null)
    {
        var chosen = plan.Chosen;
        var alternates = plan.Candidates
            .Where(c => !ReferenceEquals(c, chosen))
            .Take(maxAlternates)
            .Select(c => c.ToPayload())
            .ToList();

        return new
        {
            elementId = plan.ElementId,
            elementName = plan.ElementName,
            category = plan.Category,
            canAlign = plan.CanAlign,
            reason = plan.BlockedReason ?? DescribeMove(chosen, options),
            outcome,
            willMove = willMove && plan.CanAlign,
            target = chosen?.ToPayload(),
            rotationDegrees = rotationRadians.HasValue
                ? Math.Round(rotationRadians.Value * 180.0 / Math.PI, 2)
                : (double?)null,
            alternates
        };
    }

    private static string DescribeMove(AlignmentCandidate? chosen, AlignmentOptions options)
    {
        if (chosen == null)
            return "No surface chosen.";

        var travelMm = AlignmentRequest.FtToMm(chosen.TravelDistanceFt);
        if (Math.Abs(travelMm) < 0.5)
            return $"Already flush against the {chosen.SurfaceKind} in {chosen.Model}.";

        var direction = travelMm > 0 ? "toward" : "back from";
        var qualifier = chosen.NormalIsMeasured
            ? string.Empty
            : " Surface orientation was inferred from the search direction, not measured.";

        return $"Move {Math.Abs(travelMm):F1} mm {direction} the {chosen.SurfaceKind} " +
               $"({chosen.TargetCategory} in {chosen.Model}).{qualifier}";
    }
}
