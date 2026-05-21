using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Approval;

/// <summary>
/// Generates human-readable summaries for pending approval requests.
/// These are shown in the MCP window's Pending tab so the user can
/// make an informed approve/reject decision.
/// </summary>
public static class ApprovalSummaryBuilder
{
    public static string Build(McpToolRequest request)
    {
        return request.ToolName switch
        {
            "revit_select_elements" => BuildSelectElements(request),
            "revit_select_elements_by_query" => BuildSelectByQuery(request),
            "revit_set_parameter" => BuildSetParameter(request),
            "revit_create_electrical_circuit" => CircuitPreviewBuilder.BuildCreateCircuit(request),
            "revit_add_elements_to_circuit" => CircuitPreviewBuilder.BuildAddElements(request),
            "revit_reassign_circuit_panel" => CircuitPreviewBuilder.BuildReassignPanel(request),
            "revit_change_circuit_cable_or_wire_type" => CircuitPreviewBuilder.BuildChangeWireType(request),
            _ => $"Execute {request.ToolName}"
        };
    }

    private static string BuildSelectElements(McpToolRequest request)
    {
        var ids = ToolArguments.GetLongArray(request.Arguments, "elementIds");
        return $"Select {ids.Length} element{(ids.Length == 1 ? "" : "s")} by ID";
    }

    private static string BuildSelectByQuery(McpToolRequest request)
    {
        var category = ToolArguments.GetString(request.Arguments, "category");
        var filters = ToolArguments.GetFiltersWithWarnings(request.Arguments);
        var filterDesc = filters.Items.Count > 0
            ? $" matching {filters.Items.Count} filter{(filters.Items.Count == 1 ? "" : "s")}"
            : "";
        var catDesc = !string.IsNullOrWhiteSpace(category) ? category : "elements";
        return $"Select {catDesc}{filterDesc}";
    }

    private static string BuildSetParameter(McpToolRequest request)
    {
        var paramName = ToolArguments.GetString(request.Arguments, "parameterName");
        var value = ToolArguments.GetString(request.Arguments, "value");
        var useSelection = ToolArguments.GetBool(request.Arguments, "useSelection");
        var category = ToolArguments.GetString(request.Arguments, "category");
        var elementIds = ToolArguments.GetLongArray(request.Arguments, "elementIds");

        var target = useSelection ? "current selection"
            : elementIds.Length > 0 ? $"{elementIds.Length} element{(elementIds.Length == 1 ? "" : "s")}"
            : !string.IsNullOrWhiteSpace(category) ? category
            : "specified elements";

        // Truncate long values for display
        var displayValue = value.Length > 30 ? value[..27] + "..." : value;
        return $"Set '{paramName}' to \"{displayValue}\" on {target}";
    }
}
