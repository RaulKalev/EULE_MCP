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
            "revit_select_circuit_elements" => BuildSelectCircuitElements(request),
            "revit_select_uncircuited_elements" => BuildSelectUncircuitedElements(request),
            "revit_apply_circuit_numbering" => BuildApplyCircuitNumbering(request),
            "revit_apply_circuit_load_names" => BuildApplyCircuitLoadNames(request),
            "revit_set_circuit_parameters_bulk" => BuildSetCircuitParametersBulk(request),
            // Documentation tools
            "revit_place_views_on_sheets"       => BuildPlaceViewsOnSheets(request),
            "revit_duplicate_sheets"            => BuildDuplicateSheets(request),
            "revit_duplicate_views"             => BuildDuplicateViews(request),
            "revit_apply_view_template"         => BuildApplyViewTemplate(request),
            "revit_rename_views"                => BuildRenameViews(request),
            "revit_rename_sheets"               => BuildRenameSheets(request),
            "revit_set_sheet_parameters_bulk"   => BuildSetSheetParametersBulk(request),
            "revit_set_view_parameters_bulk"    => BuildSetViewParametersBulk(request),
            "revit_create_sheets_from_table"    => BuildCreateSheetsFromTable(request),
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

    private static string BuildSelectCircuitElements(McpToolRequest request)
    {
        var circuitId = ToolArguments.GetLong(request.Arguments, "circuitId");
        var replace = ToolArguments.GetBool(request.Arguments, "replaceSelection", true);
        var zoom = ToolArguments.GetBool(request.Arguments, "zoomToSelection");
        return $"Select all elements on circuit ID:{circuitId} in Revit UI. " +
               $"Replace selection: {(replace ? "yes" : "no")}. Zoom: {(zoom ? "yes" : "no")}";
    }

    private static string BuildSelectUncircuitedElements(McpToolRequest request)
    {
        var categories = ToolArguments.GetStringArray(request.Arguments, "categories");
        var replace = ToolArguments.GetBool(request.Arguments, "replaceSelection", true);
        var zoom = ToolArguments.GetBool(request.Arguments, "zoomToSelection");
        var limit = ToolArguments.GetInt(request.Arguments, "limit", 500);
        var catDesc = categories.Length > 0 ? string.Join(", ", categories) : "all electrical categories";
        return $"Select uncircuited elements in {catDesc}. " +
               $"Replace: {(replace ? "yes" : "no")}. Zoom: {(zoom ? "yes" : "no")}. Limit: {limit}";
    }

    private static string BuildApplyCircuitNumbering(McpToolRequest request)
    {
        var changes = ToolArguments.GetString(request.Arguments, "changes");
        int count = 0;
        try { count = Newtonsoft.Json.Linq.JArray.Parse(changes).Count; } catch { }
        return $"Apply circuit number changes to {count} circuit{(count == 1 ? "" : "s")}. Runs in transaction, supports Undo.";
    }

    private static string BuildApplyCircuitLoadNames(McpToolRequest request)
    {
        var changes = ToolArguments.GetString(request.Arguments, "changes");
        int count = 0;
        try { count = Newtonsoft.Json.Linq.JArray.Parse(changes).Count; } catch { }
        return $"Apply load name changes to {count} circuit{(count == 1 ? "" : "s")}. Runs in transaction, supports Undo.";
    }

    private static string BuildSetCircuitParametersBulk(McpToolRequest request)
    {
        var circuitIds = ToolArguments.GetLongArray(request.Arguments, "circuitIds");
        var panelName = ToolArguments.GetString(request.Arguments, "panelName");
        var parameters = ToolArguments.GetString(request.Arguments, "parameters");
        int paramCount = 0;
        try { paramCount = Newtonsoft.Json.Linq.JArray.Parse(parameters).Count; } catch { }
        var target = circuitIds.Length > 0
            ? $"{circuitIds.Length} circuit{(circuitIds.Length == 1 ? "" : "s")}"
            : !string.IsNullOrWhiteSpace(panelName) ? $"all circuits on panel '{panelName}'"
            : "specified circuits";
        return $"Set {paramCount} parameter{(paramCount == 1 ? "" : "s")} on {target}. Runs in transaction, supports Undo.";
    }

    // ── Documentation summaries ────────────────────────────────────────────

    private static string BuildPlaceViewsOnSheets(McpToolRequest request)
    {
        var viewIds = ToolArguments.GetLongArray(request.Arguments, "viewIds");
        var targetSheetId = ToolArguments.GetLong(request.Arguments, "targetSheetId", 0L);
        var matchMode = ToolArguments.GetString(request.Arguments, "matchMode", "Contains");
        if (targetSheetId > 0)
            return $"Place {viewIds.Length} view{(viewIds.Length == 1 ? "" : "s")} directly on sheet ID:{targetSheetId}.";
        return $"Place {viewIds.Length} view{(viewIds.Length == 1 ? "" : "s")} on matched sheets. Match mode: {matchMode}.";
    }

    private static string BuildDuplicateSheets(McpToolRequest request)
    {
        var sourceIds  = ToolArguments.GetLongArray(request.Arguments, "sourceSheetIds");
        var sourceNums = ToolArguments.GetStringArray(request.Arguments, "sourceSheetNumbers");
        var numSuffix  = ToolArguments.GetString(request.Arguments, "newNumberSuffix", "_COPY");
        var count = sourceIds.Length > 0 ? sourceIds.Length : sourceNums.Length;
        var desc  = sourceNums.Length > 0 ? string.Join(", ", sourceNums) : $"{count} sheet{(count == 1 ? "" : "s")}";
        return $"Duplicate {count} sheet{(count == 1 ? "" : "s")} ({desc}) with suffix '{numSuffix}'.";
    }

    private static string BuildDuplicateViews(McpToolRequest request)
    {
        var viewIds = ToolArguments.GetLongArray(request.Arguments, "viewIds");
        var option  = ToolArguments.GetString(request.Arguments, "duplicateOption", "DuplicateWithDetailing");
        return $"Duplicate {viewIds.Length} view{(viewIds.Length == 1 ? "" : "s")} with option '{option}'.";
    }

    private static string BuildApplyViewTemplate(McpToolRequest request)
    {
        var viewIds      = ToolArguments.GetLongArray(request.Arguments, "viewIds");
        var templateName = ToolArguments.GetString(request.Arguments, "templateName");
        return $"Apply view template '{templateName}' to {viewIds.Length} view{(viewIds.Length == 1 ? "" : "s")}.";
    }

    private static string BuildRenameViews(McpToolRequest request)
    {
        var viewIds = ToolArguments.GetLongArray(request.Arguments, "viewIds");
        var mode    = ToolArguments.GetString(request.Arguments, "mode");
        return $"Rename {viewIds.Length} view{(viewIds.Length == 1 ? "" : "s")} using mode '{mode}'.";
    }

    private static string BuildRenameSheets(McpToolRequest request)
    {
        var sheetIds = ToolArguments.GetLongArray(request.Arguments, "sheetIds");
        var mode     = ToolArguments.GetString(request.Arguments, "mode");
        return $"Rename {sheetIds.Length} sheet{(sheetIds.Length == 1 ? "" : "s")} using mode '{mode}'.";
    }

    private static string BuildSetSheetParametersBulk(McpToolRequest request)
    {
        var sheetIds  = ToolArguments.GetLongArray(request.Arguments, "sheetIds");
        var sheetNums = ToolArguments.GetStringArray(request.Arguments, "sheetNumbers");
        var count = sheetIds.Length > 0 ? sheetIds.Length : sheetNums.Length;
        return $"Set parameters on {count} sheet{(count == 1 ? "" : "s")}.";
    }

    private static string BuildSetViewParametersBulk(McpToolRequest request)
    {
        var viewIds = ToolArguments.GetLongArray(request.Arguments, "viewIds");
        return $"Set parameters on {viewIds.Length} view{(viewIds.Length == 1 ? "" : "s")}.";
    }

    private static string BuildCreateSheetsFromTable(McpToolRequest request)
    {
        var rows = ToolArguments.GetString(request.Arguments, "rows");
        int rowCount = 0;
        try { rowCount = Newtonsoft.Json.Linq.JArray.Parse(rows).Count; } catch { }
        return $"Create {rowCount} sheet{(rowCount == 1 ? "" : "s")} from table.";
    }
}
