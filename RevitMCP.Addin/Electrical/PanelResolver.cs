using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace RevitMCP.Addin.Electrical;

/// <summary>
/// Resolves panel (electrical equipment) elements for circuit assignment operations.
/// </summary>
public class PanelResolver
{
    public List<FamilyInstance> GetAllPanels(Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(FamilyInstance))
            .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
            .Cast<FamilyInstance>()
            .ToList();
    }

    /// <summary>
    /// Resolves a panel by element ID or name. Prefers ID, falls back to name matching.
    /// Returns an error if multiple panels match the name.
    /// </summary>
    public (FamilyInstance? Panel, string Error) Resolve(Document doc, long elementId, string name)
    {
        if (elementId > 0)
        {
            var byId = doc.GetElement(new ElementId(elementId)) as FamilyInstance;
            if (byId != null) return (byId, "");
            return (null, $"No element found with ID {elementId}.");
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            var panels = GetAllPanels(doc);

            // Exact match first
            var exact = panels.FirstOrDefault(p =>
                p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return (exact, "");

            // Contains match
            var matches = panels
                .Where(p => p.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 1) return (matches[0], "");
            if (matches.Count > 1)
            {
                var names = string.Join(", ", matches.Select(p => p.Name));
                return (null, $"Multiple panels match '{name}': {names}. Use a more specific name or targetPanelElementId.");
            }

            return (null, $"No panel found matching '{name}'.");
        }

        return (null, "Provide targetPanelElementId or targetPanelName.");
    }

    public int GetCircuitCountForPanel(Document doc, FamilyInstance panel)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(ElectricalSystem))
            .Cast<ElectricalSystem>()
            .Count(c =>
            {
                var bp = CircuitDtoBuilder.TryGetPanel(c);
                return bp?.Id == panel.Id;
            });
    }
}
