using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace RevitMCP.Addin.Electrical;

/// <summary>
/// Attempts to resolve Cable Types from the Revit model.
/// In Revit 2026, cable types may be a dedicated class or may be stored as project parameters.
/// Falls back gracefully if the model/template does not have cable types.
/// </summary>
public static class CableTypeResolver
{
    /// <summary>
    /// Resolves a single CableType element by exact or partial name match.
    /// Returns null if the class is unavailable in this Revit version or no match found.
    /// </summary>
    public static (Element? Elem, string Error) Resolve(Document doc, string name)
    {
        var cableTypeClass = typeof(Element).Assembly
            .GetType("Autodesk.Revit.DB.Electrical.CableType");
        if (cableTypeClass == null)
            return (null, "CableType class not available in this Revit version.");

        try
        {
            var all = new FilteredElementCollector(doc)
                .OfClass(cableTypeClass)
                .ToList();

            var exact = all.FirstOrDefault(e =>
                e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return (exact, "");

            var contains = all
                .Where(e => e.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (contains.Count == 1) return (contains[0], "");
            if (contains.Count > 1)
            {
                var names = string.Join(", ", contains.Select(e => e.Name));
                return (null, $"Multiple cable types match '{name}': {names}. Use exact name.");
            }

            return (null, $"No cable type found matching '{name}'.");
        }
        catch (Exception ex)
        {
            return (null, $"Error resolving cable type: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns cable types found in the project, with warnings if not available.
    /// </summary>
    public static (List<object> Types, List<string> Warnings) GetAll(Document doc)
    {
        var types = new List<object>();
        var warnings = new List<string>();

        // Primary: try to find a CableType class via reflection
        // (Revit 2026 may expose it; handle gracefully if absent)
        try
        {
            var cableTypeClass = typeof(Element).Assembly
                .GetType("Autodesk.Revit.DB.Electrical.CableType");
            if (cableTypeClass != null)
            {
                var collected = new FilteredElementCollector(doc)
                    .OfClass(cableTypeClass)
                    .ToList();

                foreach (var elem in collected)
                    types.Add(new { id = elem.Id.Value, name = elem.Name, source = "CableType" });

                if (types.Count > 0)
                    return (types, warnings);
            }
        }
        catch { }

        // Secondary: scan circuit parameters for a cable-type text field
        var circuits = new FilteredElementCollector(doc)
            .OfClass(typeof(ElectricalSystem))
            .Cast<ElectricalSystem>()
            .Take(5)
            .ToList();

        var knownParamNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Cable Type", "CableType", "Cable_Type",
            "Kaabli tüüp", "Kaabli_tüüp", "KaabliTüüp"
        };

        foreach (var circuit in circuits)
        {
            foreach (Parameter p in circuit.Parameters)
            {
                if (knownParamNames.Contains(p.Definition?.Name ?? "")
                    && p.StorageType == StorageType.String)
                {
                    warnings.Add(
                        $"Cable types are stored as text parameter '{p.Definition?.Name}' on circuits. " +
                        "Use revit_get_available_wire_types for type-safe Revit WireType objects.");
                    return (types, warnings);
                }
            }
        }

        // Fallback
        warnings.Add(
            "No Revit Cable Type collection or circuit cable type parameter was found in this model. " +
            "Wire types are available via revit_get_available_wire_types.");
        return (types, warnings);
    }
}
