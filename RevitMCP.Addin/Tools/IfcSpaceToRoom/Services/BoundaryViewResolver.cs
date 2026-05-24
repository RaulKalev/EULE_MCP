using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>
/// Finds or creates a floor-plan ViewPlan suitable for hosting Room Separation Lines
/// on a given Level.
/// Strategy:
///   1. Return the first existing ViewPlan associated with the Level.
///   2. If none exists, create a minimal ViewPlan named
///      "MCP_IFC Room Boundary - {LevelName}" using the first available Floor Plan
///      ViewFamilyType.
/// Caller must wrap any writes inside a Transaction.
/// Phase 3.
/// </summary>
public class BoundaryViewResolver
{
    // ── Cached view-family type id (loaded once) ───────────────────────────────

    private ElementId? _floorPlanViewFamilyTypeId;

    /// <summary>
    /// Pre-loads the first available Floor Plan <c>ViewFamilyType</c> from the document.
    /// Call once before resolving views.
    /// </summary>
    public void LoadViewFamilyType(Document hostDoc)
    {
        _floorPlanViewFamilyTypeId = new FilteredElementCollector(hostDoc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .Where(vft => vft.ViewFamily == ViewFamily.FloorPlan)
            .Select(vft => vft.Id)
            .FirstOrDefault();
    }

    /// <summary>
    /// Returns an existing ViewPlan for <paramref name="level"/>, or creates one inside
    /// a caller-supplied open Transaction.
    /// </summary>
    /// <param name="hostDoc">Host document (Transaction must be open).</param>
    /// <param name="level">The Level whose floor plan view is needed.</param>
    /// <param name="warning">Set to an advisory string when a new view was created.</param>
    /// <returns>
    /// A ViewPlan suitable for passing to <c>doc.Create.NewRoomBoundaryLines()</c>,
    /// or <c>null</c> when no Floor Plan ViewFamilyType exists in the document.
    /// </returns>
    public ViewPlan? Resolve(Document hostDoc, Level level, out string? warning)
    {
        warning = null;

        // ── 1. Look for any existing ViewPlan on this Level ───────────────────
        var existing = new FilteredElementCollector(hostDoc)
            .OfClass(typeof(ViewPlan))
            .Cast<ViewPlan>()
            .FirstOrDefault(v =>
                !v.IsTemplate &&
                v.ViewType == ViewType.FloorPlan &&
                v.GenLevel?.Id == level.Id);

        if (existing != null)
            return existing;

        // ── 2. Create a minimal floor-plan view ───────────────────────────────
        if (_floorPlanViewFamilyTypeId == null)
        {
            warning = $"No Floor Plan ViewFamilyType found in document; " +
                      $"cannot create a boundary view for level '{level.Name}'.";
            return null;
        }

        var newView = ViewPlan.Create(hostDoc, _floorPlanViewFamilyTypeId, level.Id);
        newView.Name = $"MCP_IFC Room Boundary - {level.Name}";

        warning = $"Created new floor-plan view '{newView.Name}' for Room Separation Lines on level '{level.Name}'.";
        return newView;
    }
}
