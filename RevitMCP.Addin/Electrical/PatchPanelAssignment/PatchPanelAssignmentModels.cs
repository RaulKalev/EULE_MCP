namespace RevitMCP.Addin.Electrical.PatchPanelAssignment;

// Pure data model for the data-device → patch-panel assignment planner.
// No Revit API types here: the planner and its tests run without Revit.

public sealed class DeviceConnectorInput
{
    public int ConnectorId { get; init; }
    public bool IsCircuited { get; init; }
    public long? ExistingCircuitId { get; init; }
    public string? ExistingPanelName { get; init; }
}

public sealed class DeviceInput
{
    public long ElementId { get; init; }
    public string TypeName { get; init; } = string.Empty;
    public double X { get; init; }
    public double Y { get; init; }
    public List<DeviceConnectorInput> Connectors { get; init; } = new();
}

public sealed class PanelInput
{
    public long ElementId { get; init; }
    public string Name { get; init; } = string.Empty;
    /// <summary>Capacity from the panel's "Maximum Amount of Circuits" parameter (or override).</summary>
    public int MaxCircuits { get; init; }
    public int ExistingCircuitCount { get; init; }
}

public sealed class ConnectorRule
{
    public string TypeNameRegex { get; init; } = string.Empty;
    public int ConnectorsToUse { get; init; }
}

public sealed class AssignmentPlanOptions
{
    public string RouteMode { get; init; } = "ClockwisePerimeter";
    public string StartCorner { get; init; } = "TopLeft";
    public bool KeepDeviceConnectorsTogether { get; init; } = true;
    public bool SkipAlreadyCircuitedConnectors { get; init; } = true;
    /// <summary>When set, overrides every panel's own capacity parameter.</summary>
    public int? MaxCircuitsPerPanel { get; init; }
}

public sealed class PlannedCircuit
{
    public long DeviceElementId { get; init; }
    public int ConnectorId { get; init; }
    public string PanelName { get; init; } = string.Empty;
    public long PanelElementId { get; init; }
}

public sealed class PlannedDevice
{
    public long ElementId { get; init; }
    public string TypeName { get; init; } = string.Empty;
    /// <summary>Position of the device along the route (0-based).</summary>
    public int SortIndex { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public List<int> ConnectorIds { get; init; } = new();
    public string PanelName { get; init; } = string.Empty;
    public long PanelElementId { get; init; }
}

public sealed class SkippedDevice
{
    public long ElementId { get; init; }
    public string TypeName { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

public sealed class PanelUtilization
{
    public long PanelElementId { get; init; }
    public string PanelName { get; init; } = string.Empty;
    public int Capacity { get; init; }
    public int ExistingCircuits { get; init; }
    public int PlannedNewCircuits { get; init; }
    public int FinalTotal => ExistingCircuits + PlannedNewCircuits;
    public int Spare => Capacity - FinalTotal;
}

public sealed class AssignmentPlan
{
    public bool IsValid { get; init; }
    public List<PlannedDevice> Devices { get; init; } = new();
    public List<PlannedCircuit> Circuits { get; init; } = new();
    public List<SkippedDevice> Skipped { get; init; } = new();
    public List<PanelUtilization> Panels { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
    public List<string> Errors { get; init; } = new();
    public int TotalCircuitsPlanned => Circuits.Count;
}
