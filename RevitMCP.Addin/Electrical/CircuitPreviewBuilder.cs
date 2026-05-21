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
        var panelId = ToolArguments.GetInt(request.Arguments, "panelElementId", 0);
        var wireType = ToolArguments.GetString(request.Arguments, "wireTypeName");
        var useSelection = ToolArguments.GetBool(request.Arguments, "useSelection");
        var elementIds = ToolArguments.GetLongArray(request.Arguments, "elementIds");

        var source = useSelection ? "current selection"
            : elementIds.Length > 0 ? $"{elementIds.Length} element(s)"
            : "query results";

        var panel = panelId > 0 ? $"ID:{panelId}"
            : !string.IsNullOrWhiteSpace(panelName) ? panelName
            : "unassigned";

        var parts = new List<string>
        {
            $"Create {systemType} circuit from {source}",
            $"Panel: {panel}"
        };
        if (!string.IsNullOrWhiteSpace(wireType))
            parts.Add($"Wire type: {wireType}");

        return string.Join(" | ", parts);
    }

    public static string BuildAddElements(McpToolRequest request)
    {
        var circuitId = ToolArguments.GetInt(request.Arguments, "targetCircuitId", 0);
        var useSelection = ToolArguments.GetBool(request.Arguments, "useSelection");
        var elementIds = ToolArguments.GetLongArray(request.Arguments, "elementIds");

        var source = useSelection ? "current selection"
            : elementIds.Length > 0 ? $"{elementIds.Length} element(s)"
            : "query results";

        return $"Add elements from {source} to circuit ID:{circuitId}";
    }

    public static string BuildReassignPanel(McpToolRequest request)
    {
        var circuitId = ToolArguments.GetInt(request.Arguments, "circuitId", 0);
        var panelId = ToolArguments.GetInt(request.Arguments, "targetPanelElementId", 0);
        var panelName = ToolArguments.GetString(request.Arguments, "targetPanelName");
        var target = panelId > 0 ? $"ID:{panelId}" : panelName;
        return $"Reassign circuit ID:{circuitId} to panel '{target}'";
    }

    public static string BuildChangeWireType(McpToolRequest request)
    {
        var circuitId = ToolArguments.GetInt(request.Arguments, "circuitId", 0);
        var cableType = ToolArguments.GetString(request.Arguments, "cableTypeName");
        var wireType = ToolArguments.GetString(request.Arguments, "wireTypeName");
        var parts = new List<string> { $"Change circuit ID:{circuitId} cable/wire type" };
        if (!string.IsNullOrWhiteSpace(cableType)) parts.Add($"Cable: {cableType}");
        if (!string.IsNullOrWhiteSpace(wireType)) parts.Add($"Wire: {wireType}");
        return string.Join(" | ", parts);
    }
}
