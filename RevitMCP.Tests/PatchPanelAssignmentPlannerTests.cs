using RevitMCP.Addin.Electrical.PatchPanelAssignment;
using Xunit;

namespace RevitMCP.Tests;

public class PatchPanelAssignmentPlannerTests
{
    private static readonly List<ConnectorRule> Rj45Rules = new()
    {
        new ConnectorRule { TypeNameRegex = @"^1\s*x\s*RJ45", ConnectorsToUse = 1 },
        new ConnectorRule { TypeNameRegex = @"^2\s*x\s*RJ45", ConnectorsToUse = 2 }
    };

    private static DeviceInput Device(
        long id, string type, double x, double y, int connectors, int circuited = 0)
        => new()
        {
            ElementId = id,
            TypeName = type,
            X = x,
            Y = y,
            Connectors = Enumerable.Range(1, connectors)
                .Select(i => new DeviceConnectorInput
                {
                    ConnectorId = i,
                    IsCircuited = i <= circuited,
                    ExistingCircuitId = i <= circuited ? 100000 + id * 10 + i : null
                })
                .ToList()
        };

    private static PanelInput Panel(long id, string name, int max, int existing = 0)
        => new() { ElementId = id, Name = name, MaxCircuits = max, ExistingCircuitCount = existing };

    private static AssignmentPlanOptions Options(bool keepTogether = true, int? maxOverride = null)
        => new()
        {
            RouteMode = "ClockwisePerimeter",
            StartCorner = "TopLeft",
            KeepDeviceConnectorsTogether = keepTogether,
            SkipAlreadyCircuitedConnectors = true,
            MaxCircuitsPerPanel = maxOverride
        };

    // Test 4: a "1 x RJ45" family that mistakenly contains two connectors gets exactly one circuit.
    [Fact]
    public void OnePortDevice_WithTwoConnectors_GetsExactlyOneCircuit()
    {
        var plan = PatchPanelAssignmentPlanner.Plan(
            new[] { Device(1, "1 x RJ45", 0, 0, connectors: 2) },
            new[] { Panel(10, "FD-01", 24) },
            Rj45Rules, Options());

        Assert.True(plan.IsValid);
        Assert.Equal(1, plan.TotalCircuitsPlanned);
        Assert.Equal(1, plan.Circuits[0].ConnectorId); // lowest connector id wins
    }

    // Test 5: a "2 x RJ45" family creates two circuits on distinct connectors.
    [Fact]
    public void TwoPortDevice_GetsTwoCircuits_OnDistinctConnectors()
    {
        var plan = PatchPanelAssignmentPlanner.Plan(
            new[] { Device(1, "2 x RJ45", 0, 0, connectors: 2) },
            new[] { Panel(10, "FD-01", 24) },
            Rj45Rules, Options());

        Assert.True(plan.IsValid);
        Assert.Equal(2, plan.TotalCircuitsPlanned);
        Assert.Equal(2, plan.Circuits.Select(c => c.ConnectorId).Distinct().Count());
        Assert.All(plan.Circuits, c => Assert.Equal(1, c.DeviceElementId));
    }

    // Test 6: a 2-port device moves to the next panel when only one slot remains,
    // and the single slot is left spare (the pointer never goes back).
    [Fact]
    public void TwoPortDevice_MovesToNextPanel_WhenOneSlotRemains_LeavingSlotSpare()
    {
        // Devices are spread evenly around a full circle (centroid = center),
        // clockwise from the top-left, so the route order equals the
        // declaration order: 11 one-port devices fill FD-01 to 11/12, then a
        // 2-port device needs 2 slots.
        (double X, double Y) OnRoute(int step)
        {
            var angle = (135.0 - step * (360.0 / 13.0)) * Math.PI / 180.0;
            return (Math.Cos(angle) * 100.0, Math.Sin(angle) * 100.0);
        }

        var devices = new List<DeviceInput>();
        for (int i = 1; i <= 11; i++)
        {
            var (x, y) = OnRoute(i - 1);
            devices.Add(Device(i, "1 x RJ45", x, y, connectors: 1));
        }
        var (x50, y50) = OnRoute(11);
        devices.Add(Device(50, "2 x RJ45", x50, y50, connectors: 2));
        // A later one-port device must NOT back-fill the spare slot on FD-01.
        var (x60, y60) = OnRoute(12);
        devices.Add(Device(60, "1 x RJ45", x60, y60, connectors: 1));

        var plan = PatchPanelAssignmentPlanner.Plan(
            devices,
            new[] { Panel(10, "FD-01", 12), Panel(11, "FD-02", 12) },
            Rj45Rules, Options());

        Assert.True(plan.IsValid);
        var twoPort = plan.Devices.Single(d => d.ElementId == 50);
        Assert.Equal("FD-02", twoPort.PanelName);

        var fd01 = plan.Panels.Single(p => p.PanelName == "FD-01");
        Assert.Equal(11, fd01.PlannedNewCircuits);
        Assert.Equal(1, fd01.Spare); // spare slot stays spare

        var lateOnePort = plan.Devices.Single(d => d.ElementId == 60);
        Assert.Equal("FD-02", lateOnePort.PanelName);
    }

    // Test 7: existing circuits count against panel capacity.
    [Fact]
    public void ExistingCircuits_CountAgainstCapacity()
    {
        var devices = Enumerable.Range(1, 5)
            .Select(i => Device(i, "1 x RJ45", i, 0, connectors: 1))
            .ToList();

        var plan = PatchPanelAssignmentPlanner.Plan(
            devices,
            new[] { Panel(10, "FD-01", 24, existing: 22), Panel(11, "FD-02", 24) },
            Rj45Rules, Options());

        Assert.True(plan.IsValid);
        var fd01 = plan.Panels.Single(p => p.PanelName == "FD-01");
        Assert.Equal(2, fd01.PlannedNewCircuits);
        Assert.Equal(24, fd01.FinalTotal);
        var fd02 = plan.Panels.Single(p => p.PanelName == "FD-02");
        Assert.Equal(3, fd02.PlannedNewCircuits);
    }

    // Test 8: a rerun over already-circuited connectors plans no duplicates.
    [Fact]
    public void Rerun_OverFullyCircuitedDevices_PlansNothing()
    {
        var devices = new[]
        {
            Device(1, "1 x RJ45", 0, 0, connectors: 2, circuited: 1),
            Device(2, "2 x RJ45", 1, 0, connectors: 2, circuited: 2)
        };

        var plan = PatchPanelAssignmentPlanner.Plan(
            devices,
            new[] { Panel(10, "FD-01", 24) },
            Rj45Rules, Options());

        Assert.True(plan.IsValid);
        Assert.Equal(0, plan.TotalCircuitsPlanned);
        Assert.Equal(2, plan.Skipped.Count);
        Assert.All(plan.Skipped, s => Assert.Contains("Already circuited", s.Reason));
    }

    // A partially circuited 2-port device only gets its missing circuit.
    [Fact]
    public void PartiallyCircuitedTwoPortDevice_GetsOnlyMissingCircuit()
    {
        var plan = PatchPanelAssignmentPlanner.Plan(
            new[] { Device(1, "2 x RJ45", 0, 0, connectors: 2, circuited: 1) },
            new[] { Panel(10, "FD-01", 24) },
            Rj45Rules, Options());

        Assert.True(plan.IsValid);
        Assert.Equal(1, plan.TotalCircuitsPlanned);
        Assert.Equal(2, plan.Circuits[0].ConnectorId); // connector 1 is taken
    }

    // Capacity is a hard limit: the plan errors instead of exceeding it.
    [Fact]
    public void PlanErrors_InsteadOfExceedingCapacity()
    {
        var devices = Enumerable.Range(1, 5)
            .Select(i => Device(i, "1 x RJ45", i, 0, connectors: 1))
            .ToList();

        var plan = PatchPanelAssignmentPlanner.Plan(
            devices,
            new[] { Panel(10, "FD-01", 3) },
            Rj45Rules, Options());

        Assert.False(plan.IsValid);
        Assert.Contains(plan.Errors, e => e.Contains("Ran out of panel capacity"));
        Assert.All(plan.Panels, p => Assert.True(p.FinalTotal <= p.Capacity));
    }

    // The user's validation dataset shape: 37×1-port + 74×2-port = 185 circuits
    // over panels with capacity 24 — pairs stay together, no panel exceeds 24.
    [Fact]
    public void ValidationDataset_185Circuits_RespectsCapacityAndPairing()
    {
        var devices = new List<DeviceInput>();
        long id = 1;
        // Interleave types like a real floor: repeating pattern 2,2,1 around the perimeter.
        for (int i = 0; i < 111; i++)
        {
            var angle = 2 * Math.PI * i / 111.0;
            var (x, y) = (Math.Cos(angle) * 100, Math.Sin(angle) * 100);
            devices.Add(i % 3 == 2
                ? Device(id++, "1 x RJ45", x, y, connectors: 1)
                : Device(id++, "2 x RJ45", x, y, connectors: 2));
        }
        Assert.Equal(37, devices.Count(d => d.TypeName == "1 x RJ45"));
        Assert.Equal(74, devices.Count(d => d.TypeName == "2 x RJ45"));

        var panels = Enumerable.Range(1, 20)
            .Select(i => Panel(1000 + i, $"FD10.1-{i:D2}", 24))
            .ToList();

        var plan = PatchPanelAssignmentPlanner.Plan(devices, panels, Rj45Rules, Options());

        Assert.True(plan.IsValid);
        Assert.Equal(185, plan.TotalCircuitsPlanned);
        Assert.All(plan.Panels, p => Assert.True(p.FinalTotal <= 24,
            $"{p.PanelName} exceeded capacity: {p.FinalTotal}"));
        // Both circuits of every 2-port device share one panel.
        foreach (var device in plan.Devices.Where(d => d.TypeName == "2 x RJ45"))
        {
            var deviceCircuitPanels = plan.Circuits
                .Where(c => c.DeviceElementId == device.ElementId)
                .Select(c => c.PanelName)
                .Distinct();
            Assert.Single(deviceCircuitPanels);
        }
        // Sum over panels equals the total.
        Assert.Equal(185, plan.Panels.Sum(p => p.PlannedNewCircuits));
    }

    // Clockwise perimeter order: top-left → top-right → bottom-right → bottom-left.
    [Fact]
    public void ClockwisePerimeter_FromTopLeft_VisitsCornersInClockwiseOrder()
    {
        var devices = new[]
        {
            Device(1, "1 x RJ45", -10, 10, 1),  // top-left
            Device(2, "1 x RJ45", 10, 10, 1),   // top-right
            Device(3, "1 x RJ45", 10, -10, 1),  // bottom-right
            Device(4, "1 x RJ45", -10, -10, 1)  // bottom-left
        };

        var warnings = new List<string>();
        var ordered = PatchPanelAssignmentPlanner.SortByRoute(devices, "ClockwisePerimeter", "TopLeft", warnings);

        Assert.Equal(new long[] { 1, 2, 3, 4 }, ordered.Select(d => d.ElementId).ToArray());
        Assert.Empty(warnings);
    }

    [Fact]
    public void RouteSort_IsStable_ForCoincidentDevices()
    {
        var devices = new[]
        {
            Device(5, "1 x RJ45", 3, 3, 1),
            Device(2, "1 x RJ45", 3, 3, 1),
            Device(9, "1 x RJ45", 3, 3, 1)
        };

        var warnings = new List<string>();
        var ordered = PatchPanelAssignmentPlanner.SortByRoute(devices, "ClockwisePerimeter", "TopLeft", warnings);

        Assert.Equal(new long[] { 2, 5, 9 }, ordered.Select(d => d.ElementId).ToArray());
    }

    [Fact]
    public void MissingCapacityParameter_FailsValidation_UnlessOverridden()
    {
        var devices = new[] { Device(1, "1 x RJ45", 0, 0, 1) };
        var panelsWithoutCapacity = new[] { Panel(10, "FD-01", 0) };

        var invalid = PatchPanelAssignmentPlanner.Plan(devices, panelsWithoutCapacity, Rj45Rules, Options());
        Assert.False(invalid.IsValid);
        Assert.Contains(invalid.Errors, e => e.Contains("Maximum Amount of Circuits"));

        var overridden = PatchPanelAssignmentPlanner.Plan(
            devices, panelsWithoutCapacity, Rj45Rules, Options(maxOverride: 24));
        Assert.True(overridden.IsValid);
        Assert.Equal(1, overridden.TotalCircuitsPlanned);
    }

    [Fact]
    public void InvalidRegexRule_FailsValidation()
    {
        var plan = PatchPanelAssignmentPlanner.Plan(
            new[] { Device(1, "1 x RJ45", 0, 0, 1) },
            new[] { Panel(10, "FD-01", 24) },
            new[] { new ConnectorRule { TypeNameRegex = "([unclosed", ConnectorsToUse = 1 } },
            Options());

        Assert.False(plan.IsValid);
        Assert.Contains(plan.Errors, e => e.Contains("invalid regex"));
    }

    [Fact]
    public void UnmatchedType_UsesAllFreeConnectors_WithWarning()
    {
        var plan = PatchPanelAssignmentPlanner.Plan(
            new[] { Device(1, "WiFi AP", 0, 0, 2) },
            new[] { Panel(10, "FD-01", 24) },
            Rj45Rules, Options());

        Assert.True(plan.IsValid);
        Assert.Equal(2, plan.TotalCircuitsPlanned);
        Assert.Contains(plan.Warnings, w => w.Contains("matches no connectorRule"));
    }
}
