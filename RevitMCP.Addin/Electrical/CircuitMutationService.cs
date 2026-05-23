using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace RevitMCP.Addin.Electrical;

/// <summary>
/// Performs all circuit-modifying operations. Every public method wraps its
/// work in a named Revit Transaction so changes appear in the Revit Undo stack.
/// Must be called on the Revit API thread (inside ExternalEvent.Execute).
/// </summary>
public static class CircuitMutationService
{
    public record CreateResult(
        bool Success,
        string Message,
        long CircuitId,
        List<long> AddedElementIds,
        List<string> Errors);

    public record AddElementsResult(
        bool Success,
        string Message,
        List<long> Added,
        List<(long Id, string Reason)> Rejected);

    public record ReassignPanelResult(
        bool Success,
        string Message,
        string OldPanel,
        string NewPanel);

    public record ChangeTypeResult(
        bool Success,
        string Message,
        string OldWireType,
        string NewWireType,
        List<string> Warnings);

    // ── Create ───────────────────────────────────────────────────────────

    public static CreateResult CreateCircuit(
        Document doc,
        IEnumerable<ElementId> elementIds,
        ElectricalSystemType systemType,
        FamilyInstance? panel,
        Element? wireTypeElem)
    {
        var errors = new List<string>();
        var validIds = new List<ElementId>();

        foreach (var eid in elementIds)
        {
            var element = doc.GetElement(eid);
            if (element is not FamilyInstance fi || fi.MEPModel == null)
            {
                errors.Add($"Element {eid.Value}: No MEP model — skipped.");
                continue;
            }

            bool hasElecConn = false;
            try
            {
                foreach (Connector conn in fi.MEPModel.ConnectorManager.Connectors)
                {
                    if (conn.Domain == Domain.DomainElectrical)
                    {
                        hasElecConn = true;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Element {eid.Value}: {ex.Message}");
                continue;
            }

            if (hasElecConn)
                validIds.Add(eid);
            else
                errors.Add($"Element {eid.Value}: No electrical connector found.");
        }

        if (validIds.Count == 0)
            return new CreateResult(false,
                "No elements with electrical connectors found.", 0, new List<long>(), errors);

        using var trans = new Transaction(doc, "Revit MCP - Create Electrical Circuit");
        trans.Start();
        try
        {
            var circuit = ElectricalSystem.Create(doc, validIds, systemType);

            if (panel != null)
            {
                try { circuit.SelectPanel(panel); }
                catch (Exception ex) { errors.Add($"Panel assignment failed: {ex.Message}"); }
            }

            if (wireTypeElem != null)
            {
                try { circuit.CableType = wireTypeElem.Id; }
                catch (Exception ex) { errors.Add($"Wire type assignment failed: {ex.Message}"); }
            }

            var usedIds = validIds.Select(id => id.Value).ToList();
            trans.Commit();
            return new CreateResult(true,
                $"Created circuit ID:{circuit.Id.Value} with {usedIds.Count} element(s).",
                circuit.Id.Value, usedIds, errors);
        }
        catch (Exception ex)
        {
            trans.RollBack();
            return new CreateResult(false,
                $"Circuit creation failed: {ex.Message}", 0, new List<long>(), errors);
        }
    }

    // ── Add elements ─────────────────────────────────────────────────────

    public static AddElementsResult AddToCircuit(
        Document doc,
        ElectricalSystem circuit,
        IEnumerable<ElementId> elementIds)
    {
        var added = new List<long>();
        var rejected = new List<(long Id, string Reason)>();

        using var trans = new Transaction(doc, "Revit MCP - Add Elements To Circuit");
        trans.Start();
        try
        {
            foreach (var eid in elementIds)
            {
                var element = doc.GetElement(eid);
                if (element == null) { rejected.Add((eid.Value, "Element not found.")); continue; }

                if (element is not FamilyInstance fi || fi.MEPModel == null)
                {
                    rejected.Add((eid.Value, "Element has no MEP model."));
                    continue;
                }

                try
                {
                    var elemSet = new ElementSet();
                    elemSet.Insert(element);
                    circuit.AddToCircuit(elemSet);
                    added.Add(eid.Value);
                }
                catch (Exception ex)
                {
                    rejected.Add((eid.Value, ex.Message));
                }
            }

            trans.Commit();
            return new AddElementsResult(
                true,
                $"Added {added.Count} element(s). {rejected.Count} rejected.",
                added, rejected);
        }
        catch (Exception ex)
        {
            trans.RollBack();
            return new AddElementsResult(false,
                $"Transaction failed: {ex.Message}", added, rejected);
        }
    }

    // ── Reassign panel ───────────────────────────────────────────────────

    public static ReassignPanelResult ReassignPanel(
        Document doc,
        ElectricalSystem circuit,
        FamilyInstance newPanel)
    {
        var oldPanel = CircuitDtoBuilder.TryGetPanel(circuit);
        string oldName = oldPanel?.Name ?? "(none)";
        string newName = newPanel.Name;

        using var trans = new Transaction(doc, "Revit MCP - Reassign Circuit Panel");
        trans.Start();
        try
        {
            circuit.SelectPanel(newPanel);
            trans.Commit();
            return new ReassignPanelResult(
                true, $"Reassigned from '{oldName}' to '{newName}'.", oldName, newName);
        }
        catch (Exception ex)
        {
            trans.RollBack();
            return new ReassignPanelResult(
                false, $"Panel reassignment failed: {ex.Message}", oldName, newName);
        }
    }

    // ── Change wire/cable type ────────────────────────────────────────────

    public static ChangeTypeResult ChangeWireType(
        Document doc,
        ElectricalSystem circuit,
        Element newTypeElement)
    {
        string oldWireType = CircuitDtoBuilder.GetWireTypeName(doc, circuit);
        var warnings = new List<string>();

        using var trans = new Transaction(doc, "Revit MCP - Change Circuit Cable/Wire Type");
        trans.Start();
        try
        {
            circuit.CableType = newTypeElement.Id;
            trans.Commit();
            return new ChangeTypeResult(
                true,
                $"Changed wire type from '{oldWireType}' to '{newTypeElement.Name}'.",
                oldWireType, newTypeElement.Name, warnings);
        }
        catch (Exception ex)
        {
            trans.RollBack();
            return new ChangeTypeResult(
                false, $"Wire type change failed: {ex.Message}",
                oldWireType, "", warnings);
        }
    }
}
