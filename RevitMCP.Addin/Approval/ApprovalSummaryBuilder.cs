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
            "revit_set_circuit_parameter" => BuildSetCircuitParameter(request),
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
        var replace = ToolArguments.GetBool(request.Arguments, "replaceSelection", true);
        var zoom = ToolArguments.GetBool(request.Arguments, "zoomToSelection");
        return $"Select {ids.Length} element{(ids.Length == 1 ? "" : "s")} in Revit UI. " +
               $"Replace selection: {(replace ? "yes" : "no")}. Zoom: {(zoom ? "yes" : "no")}";
    }

    private static string BuildSelectByQuery(McpToolRequest request)
    {
        var category = ToolArguments.GetString(request.Arguments, "category");
        var filters = ToolArguments.GetFiltersWithWarnings(request.Arguments);
        var replace = ToolArguments.GetBool(request.Arguments, "replaceSelection", true);
        var zoom = ToolArguments.GetBool(request.Arguments, "zoomToSelection");
        var limit = ToolArguments.GetInt(request.Arguments, "limit", 500);

        var catDesc = !string.IsNullOrWhiteSpace(category) ? $"'{category}'" : "elements";
        var filterDesc = filters.Items.Count > 0
            ? $" with {filters.Items.Count} filter{(filters.Items.Count == 1 ? "" : "s")}"
            : string.Empty;
        return $"Select elements by query in category {catDesc}{filterDesc}. " +
               $"Replace: {(replace ? "yes" : "no")}. Zoom: {(zoom ? "yes" : "no")}. Limit: {limit}";
    }

    private static string BuildSetParameter(McpToolRequest request)
    {
        var paramName = ToolArguments.GetString(request.Arguments, "parameterName");
        var value = ToolArguments.GetString(request.Arguments, "value");
        var useSelection = ToolArguments.GetBool(request.Arguments, "useSelection");
        var category = ToolArguments.GetString(request.Arguments, "category");
        var elementIds = ToolArguments.GetLongArray(request.Arguments, "elementIds");
        var limit = ToolArguments.GetInt(request.Arguments, "limit", 500);

        var target = useSelection ? "current selection"
            : elementIds.Length > 0 ? $"{elementIds.Length} explicit element{(elementIds.Length == 1 ? "" : "s")}"
            : !string.IsNullOrWhiteSpace(category) ? $"category '{category}'"
            : "specified elements";

        var displayValue = value.Length > 30 ? value[..27] + "..." : value;
        return $"Set parameter '{paramName}' to \"{displayValue}\" on {target}. Limit: {limit}";
    }

    private static string BuildSetCircuitParameter(McpToolRequest request)
    {
        var paramName = ToolArguments.GetString(request.Arguments, "parameterName");
        var value = ToolArguments.GetString(request.Arguments, "value");
        var circuitIds = ToolArguments.GetLongArray(request.Arguments, "circuitIds");

        var displayValue = value.Length > 30 ? value[..27] + "..." : value;
        var circuitDesc = circuitIds.Length == 1 ? "1 circuit" : $"{circuitIds.Length} circuits";
        return $"Set circuit parameter '{paramName}' to \"{displayValue}\" on {circuitDesc}";
    }
}
