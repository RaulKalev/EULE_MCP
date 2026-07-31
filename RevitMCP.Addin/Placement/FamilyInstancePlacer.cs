using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace RevitMCP.Addin.Placement;

/// <summary>
/// Resolving a family type and creating instances of it — shared by every tool that places
/// families, so they all handle placement types, levels, and hosts the same way.
/// </summary>
internal static class FamilyInstancePlacer
{
    /// <summary>
    /// Finds the family type to place from an explicit id, or from a family and/or type name.
    /// An ambiguous name is reported with its candidates rather than resolved arbitrarily.
    /// </summary>
    public static (FamilySymbol? Symbol, string? Error) ResolveSymbol(
        Document doc, long typeId, string familyName, string typeName)
    {
        if (typeId > 0)
        {
            if (doc.GetElement(new ElementId(typeId)) is FamilySymbol byId)
                return (byId, null);
            return (null, $"Element {typeId} is not a family type (FamilySymbol).");
        }

        if (string.IsNullOrWhiteSpace(familyName) && string.IsNullOrWhiteSpace(typeName))
            return (null, "Provide typeId, or familyName and/or typeName to pick the family type to place.");

        var candidates = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .Where(s =>
                (string.IsNullOrWhiteSpace(familyName) ||
                 s.Family.Name.Contains(familyName, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(typeName) ||
                 s.Name.Contains(typeName, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // Prefer exact (case-insensitive) matches before partial ones.
        var exact = candidates.Where(s =>
            (string.IsNullOrWhiteSpace(familyName) ||
             string.Equals(s.Family.Name, familyName, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(typeName) ||
             string.Equals(s.Name, typeName, StringComparison.OrdinalIgnoreCase))).ToList();
        if (exact.Count > 0) candidates = exact;

        if (candidates.Count == 0)
            return (null, $"No loaded family type matches familyName='{familyName}', typeName='{typeName}'.");

        if (candidates.Count > 1)
        {
            var sample = string.Join("; ", candidates.Take(10)
                .Select(s => $"{s.Family.Name} : {s.Name} (typeId {s.Id.Value})"));
            return (null, $"{candidates.Count} family types match — narrow the name or pass typeId. Candidates: {sample}");
        }

        return (candidates[0], null);
    }

    /// <summary>Creates one instance, using the overload the family's placement type requires.</summary>
    public static FamilyInstance CreateInstance(
        Document doc,
        UIDocument uidoc,
        FamilySymbol symbol,
        FamilyPlacementType placementType,
        XYZ point,
        View? view,
        Level? explicitLevel,
        Element? host)
    {
        switch (placementType)
        {
            case FamilyPlacementType.ViewBased:
                return doc.Create.NewFamilyInstance(point, symbol, view);

            case FamilyPlacementType.OneLevelBased:
            case FamilyPlacementType.TwoLevelsBased:
            {
                var level = ResolveLevel(doc, uidoc, explicitLevel, point)
                    ?? throw new InvalidOperationException("No level found in the model — pass levelName.");
                if (host != null)
                    return doc.Create.NewFamilyInstance(point, symbol, host, level, StructuralType.NonStructural);
                return doc.Create.NewFamilyInstance(point, symbol, level, StructuralType.NonStructural);
            }

            case FamilyPlacementType.OneLevelBasedHosted:
            {
                if (host == null)
                    throw new InvalidOperationException(
                        $"Family '{symbol.Family.Name}' is host-based ({placementType}) — pass hostElementId (e.g. the wall to place into).");
                var level = ResolveLevel(doc, uidoc, explicitLevel, point);
                return level != null
                    ? doc.Create.NewFamilyInstance(point, symbol, host, level, StructuralType.NonStructural)
                    : doc.Create.NewFamilyInstance(point, symbol, host, StructuralType.NonStructural);
            }

            case FamilyPlacementType.WorkPlaneBased:
            {
                if (host != null)
                    return doc.Create.NewFamilyInstance(point, symbol, host, StructuralType.NonStructural);
                // Generic overload works for many work-plane-based families when the
                // document has an implicit placement plane; otherwise Revit throws and
                // the per-item error explains what to pass.
                return doc.Create.NewFamilyInstance(point, symbol, StructuralType.NonStructural);
            }

            default:
                throw new InvalidOperationException(
                    $"Family placement type '{placementType}' is not supported by this tool (family '{symbol.Family.Name}').");
        }
    }

    public static Level? ResolveLevel(Document doc, UIDocument uidoc, Level? explicitLevel, XYZ point)
    {
        if (explicitLevel != null) return explicitLevel;
        if (uidoc.ActiveView is ViewPlan plan && plan.GenLevel != null) return plan.GenLevel;
        return PlacementHelpers.NearestLevel(doc, point.Z);
    }

    /// <summary>Finds a level by name, or returns null when the model has no level with that name.</summary>
    public static Level? FindLevel(Document doc, string levelName) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .FirstOrDefault(l => string.Equals(l.Name, levelName, StringComparison.OrdinalIgnoreCase));
}
