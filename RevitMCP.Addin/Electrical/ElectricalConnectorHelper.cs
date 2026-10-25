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
        => GetAssignedSystems(connector).FirstOrDefault();

    /// <summary>
    /// Every electrical system this connector is circuited to, ordered by id. Reads both the
    /// connector's <see cref="Connector.MEPSystem"/> and the owners of its references, because
    /// neither alone is reliable for every family (a reference may point at the panel's connector
    /// rather than at the circuit).
    /// </summary>
    public static List<ElectricalSystem> GetAssignedSystems(Connector connector)
    {
        var result = new Dictionary<long, ElectricalSystem>();
        try
        {
            if (connector.MEPSystem is ElectricalSystem own)
                result[own.Id.Value] = own;
        }
        catch
        {
            // Some connectors cannot report their system; the references below still can.
        }
        try
        {
            foreach (Connector reference in connector.AllRefs)
            {
                if (reference.Owner is ElectricalSystem system)
                    result[system.Id.Value] = system;
            }
        }
        catch
        {
            // Treat unreadable references as "not circuited" — callers re-validate at write time.
        }
        return result.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
    }

    /// <summary>
    /// The electrical systems the whole device is assigned to (any connector), ordered by id.
    /// This is the device-level view Revit's own "Circuits" list shows; callers compare it with the
    /// per-connector view to catch circuits no connector reports.
    /// </summary>
    public static List<ElectricalSystem> GetDeviceSystems(FamilyInstance fi)
    {
        try
        {
            var systems = fi.MEPModel?.GetAssignedElectricalSystems();
            if (systems == null) return new List<ElectricalSystem>();
            return systems.Where(s => s != null).GroupBy(s => s.Id.Value).Select(g => g.First())
                .OrderBy(s => s.Id.Value).ToList();
        }
        catch
        {
            return new List<ElectricalSystem>();
        }
    }

    /// <summary>
    /// Ids of the device's connectors that a circuit's own connectors reference — the reverse of
    /// <see cref="GetAssignedSystems"/>, used to attribute a device-level circuit to a connector.
    /// </summary>
    public static List<int> GetDeviceConnectorIdsOn(ElectricalSystem system, FamilyInstance fi)
    {
        var ids = new SortedSet<int>();
        try
        {
            var manager = system.ConnectorManager;
            if (manager == null) return ids.ToList();
            foreach (Connector systemConnector in manager.Connectors)
            {
                try
                {
                    foreach (Connector reference in systemConnector.AllRefs)
                    {
                        if (reference.Owner?.Id == fi.Id && reference.Domain == Domain.DomainElectrical)
                            ids.Add(reference.Id);
                    }
                }
                catch
                {
                    // Skip a system connector whose references cannot be read.
                }
            }
        }
        catch
        {
            // No attribution possible; the device-level circuit stays unattributed.
        }
        return ids.ToList();
    }
}
