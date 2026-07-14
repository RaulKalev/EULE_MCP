using System.Text.RegularExpressions;

namespace RevitMCP.Addin.Electrical.PatchPanelAssignment;

/// <summary>
/// Pure planning logic for assigning data-device connectors to patch panels.
/// Deterministic and side-effect free: the same inputs always produce the same
/// plan, which is what makes preview → approve → execute safe and reruns
/// idempotent (already-circuited connectors count toward each device's quota,
/// so a second run plans zero new circuits).
/// </summary>
public static class PatchPanelAssignmentPlanner
{
    public static AssignmentPlan Plan(
        IReadOnlyList<DeviceInput> devices,
        IReadOnlyList<PanelInput> panels,
        IReadOnlyList<ConnectorRule> rules,
        AssignmentPlanOptions options)
    {
        var warnings = new List<string>();
        var errors = new List<string>();
        var skipped = new List<SkippedDevice>();
        var plannedDevices = new List<PlannedDevice>();
        var plannedCircuits = new List<PlannedCircuit>();

        // ── Validate panels and rules ────────────────────────────────────
        if (panels.Count == 0)
            errors.Add("No target panels were resolved.");

        var capacities = new int[panels.Count];
        for (int i = 0; i < panels.Count; i++)
        {
            capacities[i] = options.MaxCircuitsPerPanel ?? panels[i].MaxCircuits;
            if (capacities[i] <= 0)
                errors.Add($"Panel '{panels[i].Name}' has no usable circuit capacity " +
                           "(parameter 'Maximum Amount of Circuits' missing or 0, and no maxCircuitsPerPanel override).");
            if (panels[i].ExistingCircuitCount > capacities[i])
                warnings.Add($"Panel '{panels[i].Name}' already exceeds its capacity " +
                             $"({panels[i].ExistingCircuitCount}/{capacities[i]} circuits).");
        }

        var compiledRules = new List<(Regex Regex, int ConnectorsToUse, string Pattern)>();
        foreach (var rule in rules)
        {
            try
            {
                compiledRules.Add((new Regex(rule.TypeNameRegex,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), rule.ConnectorsToUse, rule.TypeNameRegex));
            }
            catch (ArgumentException ex)
            {
                errors.Add($"connectorRules: invalid regex '{rule.TypeNameRegex}': {ex.Message}");
            }
        }

        if (errors.Count > 0)
            return new AssignmentPlan { IsValid = false, Errors = errors, Warnings = warnings, Panels = BuildUtilization(panels, capacities, new int[panels.Count]) };

        // ── Route order ──────────────────────────────────────────────────
        var ordered = SortByRoute(devices, options.RouteMode, options.StartCorner, warnings);

        // ── Decide connectors per device ─────────────────────────────────
        var unmatchedTypesWarned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deviceChoices = new List<(DeviceInput Device, int SortIndex, List<int> ConnectorIds)>();

        for (int sortIndex = 0; sortIndex < ordered.Count; sortIndex++)
        {
            var device = ordered[sortIndex];
            var connectors = device.Connectors.OrderBy(c => c.ConnectorId).ToList();

            if (connectors.Count == 0)
            {
                skipped.Add(new SkippedDevice
                {
                    ElementId = device.ElementId,
                    TypeName = device.TypeName,
                    Reason = "No electrical connectors."
                });
                continue;
            }

            var circuitedCount = connectors.Count(c => c.IsCircuited);
            var free = connectors.Where(c => !c.IsCircuited).ToList();

            int needed;
            var matched = compiledRules.FirstOrDefault(r => r.Regex.IsMatch(device.TypeName));
            if (matched.Regex != null)
            {
                needed = matched.ConnectorsToUse;
            }
            else
            {
                needed = free.Count + (options.SkipAlreadyCircuitedConnectors ? circuitedCount : 0);
                if (unmatchedTypesWarned.Add(device.TypeName))
                    warnings.Add($"Type '{device.TypeName}' matches no connectorRule — using all of its usable connectors.");
            }

            // Idempotency: connectors that already carry a circuit satisfy part of
            // the quota, so a rerun plans nothing new for a completed device.
            var toCreate = options.SkipAlreadyCircuitedConnectors
                ? Math.Max(0, needed - circuitedCount)
                : needed;

            if (toCreate > free.Count)
            {
                warnings.Add($"Device {device.ElementId} ('{device.TypeName}'): rule requests {toCreate} new circuit(s) " +
                             $"but only {free.Count} free connector(s) exist. Planning {free.Count}.");
                toCreate = free.Count;
            }

            if (toCreate == 0)
            {
                skipped.Add(new SkippedDevice
                {
                    ElementId = device.ElementId,
                    TypeName = device.TypeName,
                    Reason = circuitedCount > 0
                        ? $"Already circuited ({circuitedCount} connector(s) on existing circuits)."
                        : "Connector rule requests 0 circuits."
                });
                continue;
            }

            var chosen = free.Take(toCreate).Select(c => c.ConnectorId).ToList();
            deviceChoices.Add((device, sortIndex, chosen));
        }

        // ── Allocate to panels (forward-only pointer) ────────────────────
        var plannedNew = new int[panels.Count];
        int panelIdx = 0;
        bool outOfCapacity = false;

        int Remaining(int i) => capacities[i] - panels[i].ExistingCircuitCount - plannedNew[i];

        foreach (var (device, sortIndex, connectorIds) in deviceChoices)
        {
            if (outOfCapacity)
            {
                skipped.Add(new SkippedDevice
                {
                    ElementId = device.ElementId,
                    TypeName = device.TypeName,
                    Reason = "No panel capacity left."
                });
                continue;
            }

            if (options.KeepDeviceConnectorsTogether)
            {
                // A device that doesn't fit leaves the shortfall slots spare and
                // moves on — the pointer never returns to a passed panel.
                while (panelIdx < panels.Count && Remaining(panelIdx) < connectorIds.Count)
                    panelIdx++;

                if (panelIdx >= panels.Count)
                {
                    outOfCapacity = true;
                    errors.Add($"Ran out of panel capacity at device {device.ElementId} " +
                               $"('{device.TypeName}', needs {connectorIds.Count} circuit(s) on one panel).");
                    skipped.Add(new SkippedDevice
                    {
                        ElementId = device.ElementId,
                        TypeName = device.TypeName,
                        Reason = "No panel capacity left."
                    });
                    continue;
                }

                var panel = panels[panelIdx];
                plannedNew[panelIdx] += connectorIds.Count;
                plannedDevices.Add(new PlannedDevice
                {
                    ElementId = device.ElementId,
                    TypeName = device.TypeName,
                    SortIndex = sortIndex,
                    X = device.X,
                    Y = device.Y,
                    ConnectorIds = connectorIds,
                    PanelName = panel.Name,
                    PanelElementId = panel.ElementId
                });
                foreach (var connectorId in connectorIds)
                {
                    plannedCircuits.Add(new PlannedCircuit
                    {
                        DeviceElementId = device.ElementId,
                        ConnectorId = connectorId,
                        PanelName = panel.Name,
                        PanelElementId = panel.ElementId
                    });
                }
            }
            else
            {
                // Splitting allowed: connectors fill panels one by one.
                var assignedPanels = new List<string>();
                long firstPanelId = 0;
                string firstPanelName = string.Empty;
                bool deviceFailed = false;

                foreach (var connectorId in connectorIds)
                {
                    while (panelIdx < panels.Count && Remaining(panelIdx) < 1)
                        panelIdx++;
                    if (panelIdx >= panels.Count)
                    {
                        outOfCapacity = true;
                        errors.Add($"Ran out of panel capacity at device {device.ElementId} connector {connectorId}.");
                        deviceFailed = true;
                        break;
                    }

                    var panel = panels[panelIdx];
                    plannedNew[panelIdx]++;
                    if (firstPanelId == 0) { firstPanelId = panel.ElementId; firstPanelName = panel.Name; }
                    assignedPanels.Add(panel.Name);
                    plannedCircuits.Add(new PlannedCircuit
                    {
                        DeviceElementId = device.ElementId,
                        ConnectorId = connectorId,
                        PanelName = panel.Name,
                        PanelElementId = panel.ElementId
                    });
                }

                if (deviceFailed)
                {
                    skipped.Add(new SkippedDevice
                    {
                        ElementId = device.ElementId,
                        TypeName = device.TypeName,
                        Reason = "No panel capacity left."
                    });
                    continue;
                }

                plannedDevices.Add(new PlannedDevice
                {
                    ElementId = device.ElementId,
                    TypeName = device.TypeName,
                    SortIndex = sortIndex,
                    X = device.X,
                    Y = device.Y,
                    ConnectorIds = connectorIds,
                    PanelName = string.Join(", ", assignedPanels.Distinct()),
                    PanelElementId = firstPanelId
                });
            }
        }

        return new AssignmentPlan
        {
            IsValid = errors.Count == 0,
            Devices = plannedDevices,
            Circuits = plannedCircuits,
            Skipped = skipped,
            Panels = BuildUtilization(panels, capacities, plannedNew),
            Warnings = warnings,
            Errors = errors
        };
    }

    private static List<PanelUtilization> BuildUtilization(
        IReadOnlyList<PanelInput> panels, int[] capacities, int[] plannedNew)
    {
        var result = new List<PanelUtilization>();
        for (int i = 0; i < panels.Count; i++)
        {
            result.Add(new PanelUtilization
            {
                PanelElementId = panels[i].ElementId,
                PanelName = panels[i].Name,
                Capacity = capacities.Length > i ? capacities[i] : panels[i].MaxCircuits,
                ExistingCircuits = panels[i].ExistingCircuitCount,
                PlannedNewCircuits = plannedNew.Length > i ? plannedNew[i] : 0
            });
        }
        return result;
    }

    // ── Route sorting ─────────────────────────────────────────────────────

    /// <summary>
    /// ClockwisePerimeter: devices are walked clockwise around the floor's
    /// centroid, starting from the direction of the chosen corner (default
    /// top-left), i.e. top-left → across the top → down the right side →
    /// across the bottom → up the left side. Ties break on coordinates, then
    /// element id, so the order is stable between runs.
    /// </summary>
    internal static List<DeviceInput> SortByRoute(
        IReadOnlyList<DeviceInput> devices, string routeMode, string startCorner, List<string> warnings)
    {
        if (devices.Count == 0) return new List<DeviceInput>();

        if (!string.Equals(routeMode, "ClockwisePerimeter", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"Unknown routeMode '{routeMode}' — falling back to reading order (top-left, row by row).");
            return devices
                .OrderByDescending(d => d.Y)
                .ThenBy(d => d.X)
                .ThenBy(d => d.ElementId)
                .ToList();
        }

        double cx = devices.Average(d => d.X);
        double cy = devices.Average(d => d.Y);

        double startAngle = startCorner.ToLowerInvariant() switch
        {
            "topleft" => 135.0,
            "topright" => 45.0,
            "bottomright" => -45.0,
            "bottomleft" => -135.0,
            _ => 135.0
        };
        if (!new[] { "topleft", "topright", "bottomright", "bottomleft" }
                .Contains(startCorner.ToLowerInvariant()))
            warnings.Add($"Unknown startCorner '{startCorner}' — using TopLeft.");

        return devices
            .Select(d =>
            {
                double dx = d.X - cx, dy = d.Y - cy;
                double key;
                if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9)
                {
                    key = 360.0; // Centroid-coincident devices go last, stably.
                }
                else
                {
                    double angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                    key = startAngle - angle; // Clockwise = decreasing angle.
                    while (key < 0) key += 360.0;
                    while (key >= 360.0) key -= 360.0;
                }
                return (Device: d, Key: key);
            })
            .OrderBy(t => t.Key)
            .ThenBy(t => t.Device.X)
            .ThenByDescending(t => t.Device.Y)
            .ThenBy(t => t.Device.ElementId)
            .Select(t => t.Device)
            .ToList();
    }
}
