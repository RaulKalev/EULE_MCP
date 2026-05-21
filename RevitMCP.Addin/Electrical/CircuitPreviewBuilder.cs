using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Electrical;

/// <summary>
/// Generates human-readable approval preview text for electrical circuit mutation tools.
/// Summaries are shown in the MCP window Pending tab.
/// </summary>
public static class CircuitPreviewBuilder
{
    public static string BuildCreateCircuit(McpToolRequest request)
    {
        var systemType = ToolArguments.GetString(request.Arguments, "systemType", "PowerCircuit");
        var panelName = ToolArguments.GetString(request.Arguments, "panelName");
        var panelId = ToolArguments.GetLong(request.Arguments, "panelElementId");
        var wireType = ToolArguments.GetString(request.Arguments, "wireTypeName");
        var useSelection = ToolArguments.GetBool(request.Arguments, "useSelection");
        var elementIds = ToolArguments.GetLongArray(request.Arguments, "elementIds");
        var category = ToolArguments.GetString(request.Arguments, "category");
        var filters = ToolArguments.GetFiltersWithWarnings(request.Arguments);
        var limit = ToolArguments.GetInt(request.Arguments, "limit", 500);

        var source = useSelection ? "current selection"
            : elementIds.Length > 0 ? $"{elementIds.Length} element(s)"
            : !string.IsNullOrWhiteSpace(category)
                ? filters.Items.Count > 0
                    ? $"'{category}' with {filters.Items.Count} filter(s)"
                    : $"category '{category}'"
            : "query results";

        var panel = panelId > 0 ? $"ID:{panelId}"
            : !string.IsNullOrWhiteSpace(panelName) ? $"'{panelName}'"
            : "unassigned";

        var parts = new List<string>
        {
            $"Create {systemType} circuit from {source}",
            $"panel: {panel}"
        };
        if (!string.IsNullOrWhiteSpace(wireType))
            parts.Add($"wire type: '{wireType}'");
        parts.Add($"limit: {limit}");

        return string.Join(", ", parts);
    }

    public static string BuildAddElements(McpToolRequest request)
    {
        var circuitId = ToolArguments.GetLong(request.Arguments, "targetCircuitId");
        var useSelection = ToolArguments.GetBool(request.Arguments, "useSelection");
        var elementIds = ToolArguments.GetLongArray(request.Arguments, "elementIds");
        var category = ToolArguments.GetString(request.Arguments, "category");
        var filters = ToolArguments.GetFiltersWithWarnings(request.Arguments);
        var limit = ToolArguments.GetInt(request.Arguments, "limit", 500);

        if (useSelection)
            return $"Add current selection to circuit {circuitId}";

        if (elementIds.Length > 0)
            return $"Add {elementIds.Length} element(s) to circuit {circuitId}";

        var filterDesc = filters.Items.Count > 0
            ? $" with {filters.Items.Count} filter(s)"
            : string.Empty;
        var sourceDesc = !string.IsNullOrWhiteSpace(category)
            ? $"elements in category '{category}'{filterDesc}"
            : $"query results{filterDesc}";

        return $"Add {sourceDesc} to circuit {circuitId}. Limit: {limit}";
    }

    public static string BuildReassignPanel(McpToolRequest request)
    {
        var circuitId = ToolArguments.GetLong(request.Arguments, "circuitId");
        var panelId = ToolArguments.GetLong(request.Arguments, "targetPanelElementId");
        var panelName = ToolArguments.GetString(request.Arguments, "targetPanelName");
        var target = panelId > 0 ? $"ID:{panelId}"
            : !string.IsNullOrWhiteSpace(panelName) ? $"'{panelName}'"
            : "unknown";
        return $"Reassign circuit {circuitId} to panel {target}";
    }

    public static string BuildChangeWireType(McpToolRequest request)
    {
        var circuitId = ToolArguments.GetLong(request.Arguments, "circuitId");
        var cableType = ToolArguments.GetString(request.Arguments, "cableTypeName");
        var wireType = ToolArguments.GetString(request.Arguments, "wireTypeName");
        var preferCable = ToolArguments.GetBool(request.Arguments, "preferCableType", true);
        var fallback = ToolArguments.GetBool(request.Arguments, "fallbackToWireType", true);

        var typeName = !string.IsNullOrWhiteSpace(cableType) ? cableType : wireType;
        var parts = new List<string>
        {
            $"Change circuit {circuitId} cable/wire type to '{typeName}'"
        };
        if (!string.IsNullOrWhiteSpace(cableType) && !string.IsNullOrWhiteSpace(wireType))
            parts.Add($"cable: '{cableType}', wire: '{wireType}'");
        parts.Add($"prefer cable: {(preferCable ? "yes" : "no")}");
        parts.Add($"fallback to wire: {(fallback ? "yes" : "no")}");

        return string.Join(". ", parts);
    }
}
