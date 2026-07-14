using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using RevitMCP.Addin.Transactions;

namespace RevitMCP.Addin.Electrical.PatchPanelAssignment;

/// <summary>
/// Revit-side collection and execution for the data-device → patch-panel
/// assignment tools. Collection is read-only; execution creates one Data
/// circuit per planned connector inside a TransactionGroup (assimilated into
/// a single undo entry, or rolled back entirely when atomic). Must be called
/// on the Revit API thread.
/// </summary>
public static class PatchPanelAssignmentService
{
    public const string CapacityParameterName = "Maximum Amount of Circuits";

    // ── Collection (read-only) ────────────────────────────────────────────

    public sealed class CollectionResult
    {
        public List<DeviceInput> Devices { get; } = new();
        public List<PanelInput> Panels { get; } = new();
        public List<string> Warnings { get; } = new();
        public string? Error { get; set; }
    }

    public static CollectionResult Collect(
        Document doc,
        string levelName,
        long[] elementIds,
        long[] panelElementIds,
        string[] panelNames,
        ElectricalSystemType systemType)
    {
        var result = new CollectionResult();

        // ── Devices ───────────────────────────────────────────────────────
        List<FamilyInstance> deviceInstances;
        if (elementIds.Length > 0)
        {
            deviceInstances = new List<FamilyInstance>();
            foreach (var id in elementIds)
            {
                if (doc.GetElement(new ElementId(id)) is FamilyInstance fi)
                    deviceInstances.Add(fi);
                else
                    result.Warnings.Add($"Element {id}: not found or not a family instance — skipped.");
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(levelName))
            {
                result.Error = "Provide levelName or elementIds.";
                return result;
            }

            var level = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .FirstOrDefault(l => l.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase));
            if (level == null)
            {
                var available = string.Join(", ", new FilteredElementCollector(doc)
                    .OfClass(typeof(Level)).Cast<Level>().Select(l => l.Name).OrderBy(n => n));
                result.Error = $"Level '{levelName}' not found. Available levels: {available}";
                return result;
            }

            deviceInstances = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_DataDevices)
                .WhereElementIsNotElementType()
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi => fi.LevelId == level.Id)
                .ToList();
        }

        foreach (var fi in deviceInstances)
        {
            var point = (fi.Location as LocationPoint)?.Point;
            if (point == null)
            {
                var bb = fi.get_BoundingBox(null);
                if (bb != null)
                    point = (bb.Min + bb.Max) * 0.5;
            }
            if (point == null)
            {
                result.Warnings.Add($"Device {fi.Id.Value}: no location point — skipped.");
                continue;
            }

            var connectors = new List<DeviceConnectorInput>();
            foreach (var connector in ElectricalConnectorHelper.GetElectricalConnectors(fi))
            {
                try
                {
                    var connectorSystemType = connector.ElectricalSystemType;
                    if (connectorSystemType != systemType &&
                        connectorSystemType != ElectricalSystemType.UndefinedSystemType)
                        continue;
                }
                catch
                {
                    continue;
                }

                var assigned = ElectricalConnectorHelper.GetAssignedSystem(connector);
                connectors.Add(new DeviceConnectorInput
                {
                    ConnectorId = connector.Id,
                    IsCircuited = assigned != null,
                    ExistingCircuitId = assigned?.Id.Value,
                    ExistingPanelName = assigned == null ? null : CircuitDtoBuilder.TryGetPanel(assigned)?.Name
                });
            }

            result.Devices.Add(new DeviceInput
            {
                ElementId = fi.Id.Value,
                TypeName = fi.Symbol?.Name ?? string.Empty,
                X = point.X,
                Y = point.Y,
                Connectors = connectors
            });
        }

        // ── Panels (order = caller's order) ───────────────────────────────
        var existingCounts = CountExistingCircuitsByPanel(doc);
        var resolver = new PanelResolver();

        void AddPanel(FamilyInstance panel)
        {
            if (result.Panels.Any(p => p.ElementId == panel.Id.Value)) return;
            existingCounts.TryGetValue(panel.Id.Value, out var existing);
            result.Panels.Add(new PanelInput
            {
                ElementId = panel.Id.Value,
                Name = panel.Name,
                MaxCircuits = ReadCapacity(panel),
                ExistingCircuitCount = existing
            });
        }

        foreach (var id in panelElementIds)
        {
            var (panel, error) = resolver.Resolve(doc, id, string.Empty);
            if (panel == null) { result.Error = error; return result; }
            AddPanel(panel);
        }
        foreach (var name in panelNames.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            var (panel, error) = resolver.Resolve(doc, 0, name);
            if (panel == null) { result.Error = error; return result; }
            AddPanel(panel);
        }

        return result;
    }

    private static int ReadCapacity(FamilyInstance panel)
    {
        try
        {
            var param = panel.LookupParameter(CapacityParameterName)
                        ?? (panel.Document.GetElement(panel.GetTypeId()) as Element)
                            ?.LookupParameter(CapacityParameterName);
            if (param is { HasValue: true })
            {
                return param.StorageType switch
                {
                    StorageType.Integer => param.AsInteger(),
                    StorageType.Double => (int)Math.Round(param.AsDouble()),
                    StorageType.String => int.TryParse(param.AsString(), out var parsed) ? parsed : 0,
                    _ => 0
                };
            }
        }
        catch
        {
            // Missing capacity is reported by the planner as a validation error.
        }
        return 0;
    }

    private static Dictionary<long, int> CountExistingCircuitsByPanel(Document doc)
    {
        var counts = new Dictionary<long, int>();
        foreach (var circuit in new FilteredElementCollector(doc)
                     .OfClass(typeof(ElectricalSystem)).Cast<ElectricalSystem>())
        {
            var panel = CircuitDtoBuilder.TryGetPanel(circuit);
            if (panel == null) continue;
            counts.TryGetValue(panel.Id.Value, out var count);
            counts[panel.Id.Value] = count + 1;
        }
        return counts;
    }

    // ── Execution ─────────────────────────────────────────────────────────

    public sealed class CreatedCircuit
    {
        public long CircuitId { get; init; }
        public long DeviceElementId { get; init; }
        public int ConnectorId { get; init; }
        public string PanelName { get; init; } = string.Empty;
        public long PanelElementId { get; init; }
    }

    public sealed class ExecutionOutcome
    {
        public bool Success { get; set; }
        public bool RolledBack { get; set; }
        public List<CreatedCircuit> Created { get; } = new();
        public List<string> SkippedAlreadyCircuited { get; } = new();
        public List<string> Failures { get; } = new();
        public List<string> Warnings { get; } = new();
    }

    /// <summary>
    /// Executes a validated plan. One Transaction per device (clear per-device
    /// errors), all inside a TransactionGroup: assimilated into a single undo
    /// entry on success, rolled back entirely on any failure when atomic.
    /// </summary>
    public static ExecutionOutcome Execute(Document doc, AssignmentPlan plan, bool atomic)
    {
        var outcome = new ExecutionOutcome();

        using var group = new TransactionGroup(doc, "Revit MCP - Assign Data Devices to Patch Panels");
        TransactionStatus groupStatus;
        try
        {
            groupStatus = group.Start();
        }
        catch (Exception ex)
        {
            outcome.Failures.Add($"TransactionGroup.Start() threw {ex.GetType().Name}: {ex.Message}");
            return outcome;
        }
        if (groupStatus != TransactionStatus.Started)
        {
            outcome.Failures.Add($"TransactionGroup could not start (status: {groupStatus}). " +
                                 $"doc.IsModifiable={doc.IsModifiable}, doc.IsReadOnly={doc.IsReadOnly}. No changes were made.");
            return outcome;
        }

        bool anyFailure = false;

        foreach (var device in plan.Devices)
        {
            if (anyFailure && atomic) break;

            if (doc.GetElement(new ElementId(device.ElementId)) is not FamilyInstance fi || fi.MEPModel == null)
            {
                outcome.Failures.Add($"Device {device.ElementId}: no longer a valid family instance.");
                anyFailure = true;
                continue;
            }

            var panelElement = doc.GetElement(new ElementId(device.PanelElementId)) as FamilyInstance;
            if (panelElement == null)
            {
                outcome.Failures.Add($"Device {device.ElementId}: target panel {device.PanelName} " +
                                     $"({device.PanelElementId}) not found.");
                anyFailure = true;
                continue;
            }

            var deviceCreated = new List<CreatedCircuit>();
            var (success, diag) = RevitTransactionRunner.Run(
                doc, $"Assign data device {device.ElementId} to {device.PanelName}", () =>
                {
                    foreach (var connectorId in device.ConnectorIds)
                    {
                        var connector = ElectricalConnectorHelper.FindById(fi, connectorId);
                        if (connector == null)
                            throw new InvalidOperationException(
                                $"Connector {connectorId} not found on device {device.ElementId}.");

                        // Execution-time idempotency: the plan may be re-run after a
                        // partial success — never duplicate an existing circuit.
                        var existing = ElectricalConnectorHelper.GetAssignedSystem(connector);
                        if (existing != null)
                        {
                            outcome.SkippedAlreadyCircuited.Add(
                                $"Device {device.ElementId} connector {connectorId}: already on circuit " +
                                $"ID:{existing.Id.Value} — skipped.");
                            continue;
                        }

                        var circuit = ElectricalSystem.Create(connector, ElectricalSystemType.Data);
                        circuit.SelectPanel(panelElement);
                        deviceCreated.Add(new CreatedCircuit
                        {
                            CircuitId = circuit.Id.Value,
                            DeviceElementId = device.ElementId,
                            ConnectorId = connectorId,
                            PanelName = device.PanelName,
                            PanelElementId = device.PanelElementId
                        });
                    }
                });

            if (success)
            {
                outcome.Created.AddRange(deviceCreated);
            }
            else
            {
                anyFailure = true;
                outcome.Failures.Add($"Device {device.ElementId} ('{device.TypeName}') → {device.PanelName}: " +
                                     $"{diag.OriginalError}");
                foreach (var line in diag.ToErrorLines().Skip(1))
                    outcome.Failures.Add($"  {line}");
            }
        }

        if (anyFailure && atomic)
        {
            outcome.RolledBack = SafeGroupRollBack(group, outcome);
            outcome.Created.Clear(); // Rolled back — nothing was kept.
            outcome.Success = false;
            return outcome;
        }

        try
        {
            var assimilateStatus = group.Assimilate();
            if (assimilateStatus != TransactionStatus.Committed)
            {
                outcome.Failures.Add($"TransactionGroup.Assimilate() returned {assimilateStatus}.");
                outcome.Success = false;
                return outcome;
            }
        }
        catch (Exception ex)
        {
            outcome.Failures.Add($"TransactionGroup.Assimilate() threw {ex.GetType().Name}: {ex.Message}");
            outcome.Success = false;
            return outcome;
        }

        outcome.Success = !anyFailure;
        return outcome;
    }

    private static bool SafeGroupRollBack(TransactionGroup group, ExecutionOutcome outcome)
    {
        try
        {
            if (group.GetStatus() != TransactionStatus.Started)
                return false;
            var status = group.RollBack();
            if (status != TransactionStatus.RolledBack)
            {
                outcome.Warnings.Add($"TransactionGroup.RollBack() returned {status}.");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            outcome.Warnings.Add(
                $"TransactionGroup.RollBack() threw {ex.GetType().Name}: {ex.Message} " +
                "(reported separately — the per-device failures above are the original errors).");
            return false;
        }
    }

    /// <summary>Reads back live per-panel utilization after execution.</summary>
    public static List<PanelUtilization> ReadUtilization(
        Document doc, IReadOnlyList<PanelInput> panels, int? maxCircuitsPerPanel)
    {
        var counts = CountExistingCircuitsByPanel(doc);
        return panels.Select(p =>
        {
            counts.TryGetValue(p.ElementId, out var current);
            return new PanelUtilization
            {
                PanelElementId = p.ElementId,
                PanelName = p.Name,
                Capacity = maxCircuitsPerPanel ?? p.MaxCircuits,
                ExistingCircuits = current,
                PlannedNewCircuits = 0
            };
        }).ToList();
    }
}
