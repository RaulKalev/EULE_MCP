using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using RevitMCP.Addin.Transactions;

namespace RevitMCP.Addin.Electrical;

/// <summary>
/// Performs all circuit-modifying operations. Every public method wraps its
/// work in <see cref="RevitTransactionRunner"/>, which validates the
/// Transaction.Start() status before touching the model, only commits or rolls
/// back a transaction that actually started, and never lets a rollback failure
/// mask the original error. Must be called on the Revit API thread (inside
/// ExternalEvent.Execute).
/// </summary>
public static class CircuitMutationService
{
    public record CreateResult(
        bool Success,
        string Message,
        long CircuitId,
        List<long> AddedElementIds,
        List<string> Errors,
        TransactionDiagnostics? Diagnostics = null);

    public record AddElementsResult(
        bool Success,
        string Message,
        List<long> Added,
        List<(long Id, string Reason)> Rejected,
        TransactionDiagnostics? Diagnostics = null);

    public record ReassignPanelResult(
        bool Success,
        string Message,
        string OldPanel,
        string NewPanel,
        TransactionDiagnostics? Diagnostics = null);

    public record ChangeTypeResult(
        bool Success,
        string Message,
        string OldWireType,
        string NewWireType,
        List<string> Warnings,
        TransactionDiagnostics? Diagnostics = null);

    public record SetPathModeResult(
        bool Success,
        string Message,
        int UpdatedCount,
        int AlreadyCorrectCount,
        int SkippedCustomPathCount,
        int SkippedUnsupportedModeCount,
        List<CircuitPathAction> Actions,
        TransactionDiagnostics? Diagnostics = null);

    public record CircuitPathAction(long CircuitId, string CircuitNumber, string Action);

    // ── Create ───────────────────────────────────────────────────────────

    public static CreateResult CreateCircuit(
        Document doc,
        IEnumerable<ElementId> elementIds,
        ElectricalSystemType systemType,
        FamilyInstance? panel,
        Element? wireTypeElem,
        int connectorId = 0)
    {
        var errors = new List<string>();
        var validIds = new List<ElementId>();
        var idList = elementIds.ToList();

        // Connector-explicit path: a family with multiple electrical connectors
        // (e.g. a 2xRJ45 outlet) is ambiguous by ElementId alone — the caller
        // names the exact connector to circuit.
        if (connectorId > 0)
        {
            if (idList.Count != 1)
                return new CreateResult(false,
                    $"connectorId targets one connector on one element — got {idList.Count} elements. " +
                    "Pass exactly one element id together with connectorId.",
                    0, new List<long>(), errors);

            if (doc.GetElement(idList[0]) is not FamilyInstance connFi || connFi.MEPModel == null)
                return new CreateResult(false,
                    $"Element {idList[0].Value}: not a family instance with an MEP model.",
                    0, new List<long>(), errors);

            var connector = ElectricalConnectorHelper.FindById(connFi, connectorId);
            if (connector == null)
            {
                var available = string.Join(", ",
                    ElectricalConnectorHelper.GetElectricalConnectors(connFi).Select(c => c.Id));
                return new CreateResult(false,
                    $"Element {idList[0].Value} has no electrical connector with id {connectorId}. " +
                    $"Available electrical connector ids: [{available}]",
                    0, new List<long>(), errors);
            }

            return CreateCircuitFromConnector(doc, connector, systemType, panel, wireTypeElem);
        }

        foreach (var eid in idList)
        {
            var element = doc.GetElement(eid);
            if (element is not FamilyInstance fi || fi.MEPModel == null)
            {
                errors.Add($"Element {eid.Value}: No MEP model — skipped.");
                continue;
            }

            var connectors = ElectricalConnectorHelper.GetElectricalConnectors(fi);
            if (connectors.Count == 0)
            {
                errors.Add($"Element {eid.Value}: No electrical connector found.");
                continue;
            }

            if (connectors.Count > 1)
                errors.Add($"Element {eid.Value}: has {connectors.Count} electrical connectors " +
                           $"(ids: {string.Join(", ", connectors.Select(c => c.Id))}). " +
                           "Revit picks one implicitly — pass connectorId to choose explicitly.");

            validIds.Add(eid);
        }

        if (validIds.Count == 0)
            return new CreateResult(false,
                "No elements with electrical connectors found.", 0, new List<long>(), errors);

        ElectricalSystem? circuit = null;
        var (success, diag) = RevitTransactionRunner.Run(doc, "Revit MCP - Create Electrical Circuit", () =>
        {
            circuit = ElectricalSystem.Create(doc, validIds, systemType);
            AssignPanelAndType(circuit, panel, wireTypeElem, errors);
        });

        if (!success || circuit == null)
        {
            errors.AddRange(diag.ToErrorLines());
            return new CreateResult(false,
                $"Circuit creation failed: {diag.OriginalError}", 0, new List<long>(), errors, diag);
        }

        var usedIds = validIds.Select(id => id.Value).ToList();
        return new CreateResult(true,
            $"Created circuit ID:{circuit.Id.Value} with {usedIds.Count} element(s).",
            circuit.Id.Value, usedIds, errors, diag);
    }

    /// <summary>
    /// Creates a circuit from one explicit device connector — the supported way
    /// to disambiguate families that expose several electrical connectors.
    /// </summary>
    public static CreateResult CreateCircuitFromConnector(
        Document doc,
        Connector connector,
        ElectricalSystemType systemType,
        FamilyInstance? panel,
        Element? wireTypeElem)
    {
        var errors = new List<string>();
        long ownerId = connector.Owner?.Id.Value ?? 0;

        var existing = ElectricalConnectorHelper.GetAssignedSystem(connector);
        if (existing != null)
            return new CreateResult(false,
                $"Connector {connector.Id} on element {ownerId} is already on circuit " +
                $"ID:{existing.Id.Value} ({existing.CircuitNumber ?? "?"}). Not creating a duplicate.",
                0, new List<long>(), errors);

        ElectricalSystem? circuit = null;
        var (success, diag) = RevitTransactionRunner.Run(doc, "Revit MCP - Create Electrical Circuit", () =>
        {
            circuit = ElectricalSystem.Create(connector, systemType);
            AssignPanelAndType(circuit, panel, wireTypeElem, errors);
        });

        if (!success || circuit == null)
        {
            errors.AddRange(diag.ToErrorLines());
            return new CreateResult(false,
                $"Circuit creation failed: {diag.OriginalError}", 0, new List<long>(), errors, diag);
        }

        return new CreateResult(true,
            $"Created circuit ID:{circuit.Id.Value} from connector {connector.Id} on element {ownerId}.",
            circuit.Id.Value, new List<long> { ownerId }, errors, diag);
    }

    private static void AssignPanelAndType(
        ElectricalSystem? circuit,
        FamilyInstance? panel,
        Element? wireTypeElem,
        List<string> errors)
    {
        if (circuit == null) return;

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
    }

    // ── Add elements ─────────────────────────────────────────────────────

    public static AddElementsResult AddToCircuit(
        Document doc,
        ElectricalSystem circuit,
        IEnumerable<ElementId> elementIds)
    {
        var added = new List<long>();
        var rejected = new List<(long Id, string Reason)>();

        var (success, diag) = RevitTransactionRunner.Run(doc, "Revit MCP - Add Elements To Circuit", () =>
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
        });

        if (!success)
            return new AddElementsResult(false,
                $"Transaction failed: {diag.OriginalError}", added, rejected, diag);

        return new AddElementsResult(
            true,
            $"Added {added.Count} element(s). {rejected.Count} rejected.",
            added, rejected, diag);
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

        var (success, diag) = RevitTransactionRunner.Run(doc, "Revit MCP - Reassign Circuit Panel",
            () => circuit.SelectPanel(newPanel));

        if (!success)
            return new ReassignPanelResult(
                false, $"Panel reassignment failed: {diag.OriginalError}", oldName, newName, diag);

        return new ReassignPanelResult(
            true, $"Reassigned from '{oldName}' to '{newName}'.", oldName, newName, diag);
    }

    // ── Change wire/cable type ────────────────────────────────────────────

    public static ChangeTypeResult ChangeWireType(
        Document doc,
        ElectricalSystem circuit,
        Element newTypeElement)
    {
        string oldWireType = CircuitDtoBuilder.GetWireTypeName(doc, circuit);
        var warnings = new List<string>();

        var (success, diag) = RevitTransactionRunner.Run(doc, "Revit MCP - Change Circuit Cable/Wire Type", () =>
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
        });

        if (!success)
            return new ChangeTypeResult(
                false, $"Wire type change failed: {diag.OriginalError}",
                oldWireType, "", warnings, diag);

        return new ChangeTypeResult(
            true,
            $"Changed wire type from '{oldWireType}' to '{newTypeElement.Name}'.",
            oldWireType, newTypeElement.Name, warnings, diag);
    }

    // ── Set path mode ─────────────────────────────────────────────────────

    public static SetPathModeResult SetPathMode(
        Document doc,
        IEnumerable<ElectricalSystem> circuits)
    {
        var actions = new List<CircuitPathAction>();
        int updatedCount = 0, alreadyCorrectCount = 0, skippedCustomCount = 0, skippedUnsupportedCount = 0;

        var (success, diag) = RevitTransactionRunner.Run(doc, "Revit MCP - Set Circuit Path Mode to All Devices", () =>
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
        });

        if (!success)
            return new SetPathModeResult(
                false, $"Transaction failed: {diag.OriginalError}", 0, 0, 0, 0, actions, diag);

        return new SetPathModeResult(
            true,
            $"Updated {updatedCount} circuit(s) to All Devices. {skippedCustomCount} skipped (custom path). {alreadyCorrectCount} already correct. {skippedUnsupportedCount} skipped (unsupported mode).",
            updatedCount, alreadyCorrectCount, skippedCustomCount, skippedUnsupportedCount, actions, diag);
    }
}
