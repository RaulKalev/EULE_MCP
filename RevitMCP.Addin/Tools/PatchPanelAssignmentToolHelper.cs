using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Electrical.PatchPanelAssignment;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Shared argument parsing and plan building for the preview/assign
/// data-device → patch-panel tool pair. Both tools plan from the same
/// arguments so an approved run executes exactly what the preview showed
/// (the approval flow already rejects the request if the model changed).
/// </summary>
internal static class PatchPanelAssignmentToolHelper
{
    /// <summary>Default rules for the RJ45 workflow this tool pair was built for.</summary>
    internal static readonly List<ConnectorRule> DefaultRules = new()
    {
        new ConnectorRule { TypeNameRegex = @"^1\s*x\s*RJ45", ConnectorsToUse = 1 },
        new ConnectorRule { TypeNameRegex = @"^2\s*x\s*RJ45", ConnectorsToUse = 2 }
    };

    internal sealed class ParsedArgs
    {
        public string LevelName = string.Empty;
        public long[] ElementIds = Array.Empty<long>();
        public long[] PanelElementIds = Array.Empty<long>();
        public string[] PanelNames = Array.Empty<string>();
        public ElectricalSystemType SystemType = ElectricalSystemType.Data;
        public string SystemTypeName = "Data";
        public string RouteMode = "ClockwisePerimeter";
        public string StartCorner = "TopLeft";
        public int? MaxCircuitsPerPanel;
        public bool KeepDeviceConnectorsTogether = true;
        public bool SkipAlreadyCircuitedConnectors = true;
        public List<ConnectorRule> Rules = new();
        public List<string> Warnings = new();
        public string? Error;
    }

    internal static ParsedArgs Parse(McpToolRequest request)
    {
        var parsed = new ParsedArgs
        {
            LevelName = ToolArguments.GetString(request.Arguments, "levelName"),
            ElementIds = ToolArguments.GetLongArray(request.Arguments, "elementIds"),
            PanelElementIds = ToolArguments.GetLongArray(request.Arguments, "panelElementIds"),
            PanelNames = ToolArguments.GetStringArray(request.Arguments, "panelNames"),
            RouteMode = ToolArguments.GetString(request.Arguments, "routeMode", "ClockwisePerimeter"),
            StartCorner = ToolArguments.GetString(request.Arguments, "startCorner", "TopLeft"),
            KeepDeviceConnectorsTogether = GetBoolWithDefault(request.Arguments, "keepDeviceConnectorsTogether", true),
            SkipAlreadyCircuitedConnectors = GetBoolWithDefault(request.Arguments, "skipAlreadyCircuitedConnectors", true)
        };

        if (string.IsNullOrWhiteSpace(parsed.LevelName) && parsed.ElementIds.Length == 0)
        {
            parsed.Error = "Provide levelName or elementIds to select the data devices.";
            return parsed;
        }
        if (parsed.PanelElementIds.Length == 0 && parsed.PanelNames.Length == 0)
        {
            parsed.Error = "Provide panelNames or panelElementIds (allocation order follows the list order).";
            return parsed;
        }

        parsed.SystemTypeName = ToolArguments.GetString(request.Arguments, "systemType", "Data");
        if (!Enum.TryParse<ElectricalSystemType>(parsed.SystemTypeName, ignoreCase: true, out var systemType))
        {
            parsed.Error = $"Unknown system type '{parsed.SystemTypeName}'. " +
                           $"Valid values: {string.Join(", ", Enum.GetNames(typeof(ElectricalSystemType)))}";
            return parsed;
        }
        parsed.SystemType = systemType;

        var maxOverride = ToolArguments.GetInt(request.Arguments, "maxCircuitsPerPanel", 0);
        parsed.MaxCircuitsPerPanel = maxOverride > 0 ? maxOverride : null;

        parsed.Rules = ParseConnectorRules(request.Arguments, parsed.Warnings);
        return parsed;
    }

    private static bool GetBoolWithDefault(Dictionary<string, object?> args, string key, bool defaultValue)
        => args.ContainsKey(key) ? ToolArguments.GetBool(args, key) : defaultValue;

    private static List<ConnectorRule> ParseConnectorRules(
        Dictionary<string, object?> args, List<string> warnings)
    {
        if (!args.TryGetValue("connectorRules", out var value) || value == null)
            return DefaultRules;

        JArray? array = value switch
        {
            JArray ja => ja,
            JToken { Type: JTokenType.Array } jt => (JArray)jt,
            string s when !string.IsNullOrWhiteSpace(s) => ToolArguments.TryParseJArray(s),
            _ => null
        };

        if (array == null)
        {
            warnings.Add("'connectorRules' could not be parsed as a JSON array — using the default RJ45 rules.");
            return DefaultRules;
        }
        if (array.Count == 0)
            return DefaultRules;

        var rules = new List<ConnectorRule>();
        foreach (var token in array)
        {
            var regex = token["typeNameRegex"]?.Value<string>() ?? string.Empty;
            var count = token["connectorsToUse"]?.Value<int?>() ?? -1;
            if (string.IsNullOrWhiteSpace(regex) || count < 0)
            {
                warnings.Add($"connectorRules entry skipped (needs typeNameRegex and connectorsToUse >= 0): {token}");
                continue;
            }
            rules.Add(new ConnectorRule { TypeNameRegex = regex, ConnectorsToUse = count });
        }
        return rules.Count > 0 ? rules : DefaultRules;
    }

    internal static AssignmentPlanOptions ToOptions(ParsedArgs parsed) => new()
    {
        RouteMode = parsed.RouteMode,
        StartCorner = parsed.StartCorner,
        KeepDeviceConnectorsTogether = parsed.KeepDeviceConnectorsTogether,
        SkipAlreadyCircuitedConnectors = parsed.SkipAlreadyCircuitedConnectors,
        MaxCircuitsPerPanel = parsed.MaxCircuitsPerPanel
    };

    /// <summary>Collects model data and builds the plan. Read-only.</summary>
    internal static (PatchPanelAssignmentService.CollectionResult Collection, AssignmentPlan? Plan, string? Error)
        BuildPlan(Document doc, ParsedArgs parsed)
    {
        var collection = PatchPanelAssignmentService.Collect(
            doc, parsed.LevelName, parsed.ElementIds, parsed.PanelElementIds,
            parsed.PanelNames, parsed.SystemType);
        if (collection.Error != null)
            return (collection, null, collection.Error);

        var plan = PatchPanelAssignmentPlanner.Plan(
            collection.Devices, collection.Panels, parsed.Rules, ToOptions(parsed));
        return (collection, plan, null);
    }

    internal static object PlanToData(ParsedArgs parsed, PatchPanelAssignmentService.CollectionResult collection, AssignmentPlan plan) => new
    {
        valid = plan.IsValid,
        totalCircuitsPlanned = plan.TotalCircuitsPlanned,
        deviceCount = collection.Devices.Count,
        plannedDeviceCount = plan.Devices.Count,
        skippedDeviceCount = plan.Skipped.Count,
        systemType = parsed.SystemTypeName,
        routeMode = parsed.RouteMode,
        startCorner = parsed.StartCorner,
        keepDeviceConnectorsTogether = parsed.KeepDeviceConnectorsTogether,
        skipAlreadyCircuitedConnectors = parsed.SkipAlreadyCircuitedConnectors,
        maxCircuitsPerPanel = parsed.MaxCircuitsPerPanel,
        connectorRules = parsed.Rules.Select(r => new { typeNameRegex = r.TypeNameRegex, connectorsToUse = r.ConnectorsToUse }),
        panelUtilization = plan.Panels.Select(p => new
        {
            panelName = p.PanelName,
            panelElementId = p.PanelElementId,
            capacity = p.Capacity,
            existingCircuits = p.ExistingCircuits,
            plannedNewCircuits = p.PlannedNewCircuits,
            finalTotal = p.FinalTotal,
            spare = p.Spare
        }),
        devices = plan.Devices.Select(d => new
        {
            elementId = d.ElementId,
            typeName = d.TypeName,
            sortIndex = d.SortIndex,
            connectorIds = d.ConnectorIds,
            panelName = d.PanelName,
            panelElementId = d.PanelElementId
        }),
        skipped = plan.Skipped.Select(s => new { elementId = s.ElementId, typeName = s.TypeName, reason = s.Reason }),
        validationErrors = plan.Errors
    };
}
