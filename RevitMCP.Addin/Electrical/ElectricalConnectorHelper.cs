using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace RevitMCP.Addin.Electrical;

/// <summary>
/// Per-connector electrical queries. Circuit membership is per connector in
/// Revit — a family with two electrical connectors can sit on two circuits —
/// so multi-connector devices must be inspected connector by connector.
/// </summary>
public static class ElectricalConnectorHelper
{
    /// <summary>All electrical-domain connectors of a family instance, ordered by connector id.</summary>
    public static List<Connector> GetElectricalConnectors(FamilyInstance fi)
    {
        var result = new List<Connector>();
        var manager = fi.MEPModel?.ConnectorManager;
        if (manager == null) return result;

        foreach (Connector c in manager.Connectors)
        {
            try
            {
                if (c.Domain == Domain.DomainElectrical)
                    result.Add(c);
            }
            catch
            {
                // Skip connectors that cannot report their domain.
            }
        }
        return result.OrderBy(c => c.Id).ToList();
    }

    /// <summary>Finds an electrical connector by its ConnectorManager id, or null.</summary>
    public static Connector? FindById(FamilyInstance fi, int connectorId)
        => GetElectricalConnectors(fi).FirstOrDefault(c => c.Id == connectorId);

    /// <summary>
    /// The electrical system this specific connector is already circuited to, or
    /// null when the connector is unused. Detected through the connector's
    /// references: a circuited device connector references a connector owned by
    /// the ElectricalSystem element.
    /// </summary>
    public static ElectricalSystem? GetAssignedSystem(Connector connector)
    {
        try
        {
            foreach (Connector reference in connector.AllRefs)
            {
                if (reference.Owner is ElectricalSystem system)
                    return system;
            }
        }
        catch
        {
            // Treat unreadable references as "not circuited" — callers re-validate at write time.
        }
        return null;
    }
}
