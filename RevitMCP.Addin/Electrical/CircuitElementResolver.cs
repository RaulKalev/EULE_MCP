using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Query;

namespace RevitMCP.Addin.Electrical;

/// <summary>
/// Resolves element IDs from selection/explicit/query sources and validates
/// whether each element is compatible with electrical circuit operations.
/// </summary>
public class CircuitElementResolver
{
    private readonly ElementQueryEngine _queryEngine = new();

    /// <summary>
    /// Resolves element IDs from one of three sources: current selection, explicit IDs,
    /// or an ElementQueryOptions query. Returns an error string if resolution fails.
    /// </summary>
    public (List<ElementId> Ids, string Error) ResolveElementIds(
        Document doc,
        UIDocument uidoc,
        bool useSelection,
        long[] explicitIds,
        ElementQueryOptions? queryOptions,
        int limit)
    {
        if (useSelection)
        {
            var sel = uidoc.Selection.GetElementIds().Take(limit).ToList();
            return (sel, "");
        }

        if (explicitIds.Length > 0)
        {
            var ids = explicitIds.Take(limit).Select(id => new ElementId(id)).ToList();
            return (ids, "");
        }

        if (queryOptions != null)
        {
            queryOptions.Limit = limit;
            var qResult = _queryEngine.Query(doc, uidoc, queryOptions);
            if (!qResult.Success) return (new List<ElementId>(), qResult.Message);
            var ids = qResult.Elements.Select(e => new ElementId(e.ElementId)).ToList();
            return (ids, "");
        }

        return (new List<ElementId>(), "Provide useSelection=true, elementIds, or a category/query.");
    }

    public record CompatibilityResult(
        long ElementId,
        string Category,
        string Family,
        string Type,
        bool IsCompatible,
        string? IncompatibilityReason,
        List<long> CurrentCircuitIds);

    /// <summary>
    /// Checks each element ID for electrical circuit compatibility.
    /// Optionally validates against a target circuit (checks for duplicate membership).
    /// </summary>
    public List<CompatibilityResult> CheckCompatibility(
        Document doc,
        IEnumerable<ElementId> elementIds,
        ElectricalSystem? targetCircuit = null)
    {
        var results = new List<CompatibilityResult>();

        foreach (var eid in elementIds)
        {
            var element = doc.GetElement(eid);
            if (element == null)
            {
                results.Add(new CompatibilityResult(eid.Value, "", "", "", false,
                    "Element not found.", new List<long>()));
                continue;
            }

            var typeId = element.GetTypeId();
            var typeElem = typeId != null && typeId != ElementId.InvalidElementId
                ? doc.GetElement(typeId) : null;
            string category = element.Category?.Name ?? "";
            string family = element is FamilyInstance fi ? fi.Symbol?.Family?.Name ?? "" : "";
            string type = typeElem?.Name ?? element.Name;

            // Must be a FamilyInstance with an MEP model
            if (element is not FamilyInstance fi2 || fi2.MEPModel == null)
            {
                results.Add(new CompatibilityResult(eid.Value, category, family, type, false,
                    "Element has no MEP model (cannot participate in electrical circuits).",
                    new List<long>()));
                continue;
            }

            // Must have at least one electrical connector
            bool hasElectricalConnector = false;
            try
            {
                foreach (Connector conn in fi2.MEPModel.ConnectorManager.Connectors)
                {
                    if (conn.Domain == Domain.DomainElectrical)
                    {
                        hasElectricalConnector = true;
                        break;
                    }
                }
            }
            catch { }

            if (!hasElectricalConnector)
            {
                results.Add(new CompatibilityResult(eid.Value, category, family, type, false,
                    "Element has no electrical connectors.", new List<long>()));
                continue;
            }

            // Collect current circuit memberships
            var currentCircuitIds = new List<long>();
            try
            {
                currentCircuitIds = new FilteredElementCollector(doc)
                    .OfClass(typeof(ElectricalSystem))
                    .Cast<ElectricalSystem>()
                    .Where(c => c.Elements?.Contains(element) == true)
                    .Select(c => c.Id.Value)
                    .ToList();
            }
            catch { }

            // If checking against a target circuit, reject duplicates
            if (targetCircuit != null && currentCircuitIds.Contains(targetCircuit.Id.Value))
            {
                results.Add(new CompatibilityResult(eid.Value, category, family, type, false,
                    "Element is already a member of the target circuit.", currentCircuitIds));
                continue;
            }

            results.Add(new CompatibilityResult(eid.Value, category, family, type,
                true, null, currentCircuitIds));
        }

        return results;
    }
}
