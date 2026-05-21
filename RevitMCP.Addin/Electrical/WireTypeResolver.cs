using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace RevitMCP.Addin.Electrical;

/// <summary>
/// Resolves WireType elements from the active Revit document.
/// </summary>
public static class WireTypeResolver
{
    public static List<WireType> GetAll(Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(WireType))
            .Cast<WireType>()
            .ToList();
    }

    /// <summary>
    /// Resolves a WireType by exact or partial name match.
    /// Returns an error if multiple match.
    /// </summary>
    public static (WireType? Type, string Error) Resolve(Document doc, string name)
    {
        var all = GetAll(doc);

        var exact = all.FirstOrDefault(wt =>
            wt.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return (exact, "");

        var contains = all
            .Where(wt => wt.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (contains.Count == 1) return (contains[0], "");
        if (contains.Count > 1)
        {
            var names = string.Join(", ", contains.Select(wt => wt.Name));
            return (null, $"Multiple wire types match '{name}': {names}. Use exact name.");
        }

        return (null, $"No wire type found matching '{name}'.");
    }
}
