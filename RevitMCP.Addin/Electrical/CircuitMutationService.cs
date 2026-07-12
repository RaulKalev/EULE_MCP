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

    public record SetPathModeResult(
        bool Success,
        string Message,
        int UpdatedCount,
        int AlreadyCorrectCount,
        int SkippedCustomPathCount,
        int SkippedUnsupportedModeCount,
        List<CircuitPathAction> Actions);

    public record CircuitPathAction(long CircuitId, string CircuitNumber, string Action);

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
                try
                {
#if !REVIT2024
                    circuit.CableType = wireTypeElem.Id;
#else
                    // CableType is the Revit 2025+ API; net48/Revit 2024 only has the deprecated
                    // WireType property. Callers already resolve via WireTypeResolver on this
                    // build (CableTypeResolver always returns null — the class doesn't exist),
                    // so wireTypeElem is guaranteed to be a WireType instance here.
                    circuit.WireType = (WireType)wireTypeElem;
#endif
                }
                catch (Exception ex) { errors.Add($"Wire type assignment failed: {ex.Message}"); }
            }

            var usedIds = validIds.Select(id => id.Value).ToList();
            RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(trans);
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

            RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(trans);
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
            RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(trans);
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
#if !REVIT2024
            circuit.CableType = newTypeElement.Id;
#else
            // CableType is the Revit 2025+ API; net48/Revit 2024 only has the deprecated
            // WireType property. Callers already resolve via WireTypeResolver on this build
            // (CableTypeResolver always returns null — the class doesn't exist), so
            // newTypeElement is guaranteed to be a WireType instance here.
            circuit.WireType = (WireType)newTypeElement;
#endif
            RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(trans);
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

    // ── Set path mode ─────────────────────────────────────────────────────

    public static SetPathModeResult SetPathMode(
        Document doc,
        IEnumerable<ElectricalSystem> circuits)
    {
        var actions = new List<CircuitPathAction>();
        int updatedCount = 0, alreadyCorrectCount = 0, skippedCustomCount = 0, skippedUnsupportedCount = 0;

        using var trans = new Transaction(doc, "Revit MCP - Set Circuit Path Mode to All Devices");
        trans.Start();
        try
        {
            foreach (var circuit in circuits)
            {
                string num = circuit.CircuitNumber ?? circuit.Id.Value.ToString();

                if (circuit.CircuitPathMode == ElectricalCircuitPathMode.Custom || circuit.HasCustomCircuitPath)
                {
                    actions.Add(new CircuitPathAction(circuit.Id.Value, num, "skipped_custom"));
                    skippedCustomCount++;
                }
                else if (circuit.CircuitPathMode == ElectricalCircuitPathMode.AllDevices)
                {
                    actions.Add(new CircuitPathAction(circuit.Id.Value, num, "already_correct"));
                    alreadyCorrectCount++;
                }
                else if (circuit.CircuitPathMode == ElectricalCircuitPathMode.FarthestDevice)
                {
                    circuit.CircuitPathMode = ElectricalCircuitPathMode.AllDevices;
                    actions.Add(new CircuitPathAction(circuit.Id.Value, num, "updated"));
                    updatedCount++;
                }
                else
                {
                    actions.Add(new CircuitPathAction(
                        circuit.Id.Value,
                        num,
                        $"skipped_unsupported_mode:{circuit.CircuitPathMode}"));
                    skippedUnsupportedCount++;
                }
            }
            RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(trans);
            return new SetPathModeResult(
                true,
                $"Updated {updatedCount} circuit(s) to All Devices. {skippedCustomCount} skipped (custom path). {alreadyCorrectCount} already correct. {skippedUnsupportedCount} skipped (unsupported mode).",
                updatedCount, alreadyCorrectCount, skippedCustomCount, skippedUnsupportedCount, actions);
        }
        catch (Exception ex)
        {
            trans.RollBack();
            return new SetPathModeResult(
                false, $"Transaction failed: {ex.Message}", 0, 0, 0, 0, actions);
        }
    }
}
