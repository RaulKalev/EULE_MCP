using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace RevitMCP.Addin.Electrical;

/// <summary>
/// Queries and filters ElectricalSystem elements in the active document.
/// Must be called on the Revit API thread.
/// </summary>
public static class CircuitQueryService
{
    public static List<ElectricalSystem> GetAll(Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(ElectricalSystem))
            .Cast<ElectricalSystem>()
            .ToList();
    }

    public static List<ElectricalSystem> Filter(
        IEnumerable<ElectricalSystem> circuits,
        string? panelName,
        string? circuitNumber,
        string? systemType)
    {
        var list = circuits;

        if (!string.IsNullOrWhiteSpace(panelName))
        {
            list = list.Where(c =>
            {
                var panel = CircuitDtoBuilder.TryGetPanel(c);
                if (panel == null) return false;
                return panel.Name.Contains(panelName, StringComparison.OrdinalIgnoreCase);
            });
        }

        if (!string.IsNullOrWhiteSpace(circuitNumber))
            list = list.Where(c =>
                (c.CircuitNumber ?? "").Contains(circuitNumber, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(systemType))
        {
            if (Enum.TryParse<ElectricalSystemType>(systemType, ignoreCase: true, out var parsed))
                list = list.Where(c => c.SystemType == parsed);
            else
                list = list.Where(c =>
                    c.SystemType.ToString().Contains(systemType, StringComparison.OrdinalIgnoreCase));
        }

        return list.ToList();
    }
}
