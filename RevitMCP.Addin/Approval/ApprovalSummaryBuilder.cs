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
            "revit_create_revision"             => BuildCreateRevision(request),
            "revit_duplicate_views"             => BuildDuplicateViews(request),
            "revit_set_view_crop_regions"       => BuildSetViewCropRegions(request),
            "revit_apply_view_template"         => BuildApplyViewTemplate(request),
            "revit_rename_views"                => BuildRenameViews(request),
            "revit_rename_sheets"               => BuildRenameSheets(request),
            "revit_apply_sheet_naming"          => BuildApplySheetNaming(request),
            "revit_set_sheet_parameters_bulk"   => BuildSetSheetParametersBulk(request),
            "revit_set_view_parameters_bulk"    => BuildSetViewParametersBulk(request),
            "revit_create_sheets_from_table"    => BuildCreateSheetsFromTable(request),
            "revit_delete_views"                => BuildDeleteViews(request),
            "revit_delete_sheets"               => BuildDeleteSheets(request),
            "revit_delete_elements"             => BuildDeleteElements(request),
            // Skill builder
            "revit_create_skill"                => BuildCreateSkill(request),
            "revit_update_skill"                => BuildUpdateSkill(request),
            // Dimensions
            "revit_place_dimensions"            => BuildPlaceDimensions(request),
            // Coordination tools
            "revit_create_clash_review_view"    => BuildCreateClashReviewView(request),
            "revit_focus_clash"                 => BuildFocusClash(request),
            "revit_select_clash_elements"       => BuildSelectClashElements(request),
            // File System
            "file_write_text"           => BuildFileWriteText(request),
            // Excel Modifier
            "excel_update_cells"        => BuildExcelUpdateCells(request),
            "excel_insert_rows"         => BuildExcelInsertRows(request),
            "excel_append_table_rows"   => BuildExcelAppendTableRows(request),
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
        var copies = ToolArguments.GetInt(request.Arguments, "numberOfCopies", 1);
        var mode = ToolArguments.GetString(request.Arguments, "duplicateMode", "EmptySheet");
        var count = sourceIds.Length > 0 ? sourceIds.Length : sourceNums.Length;
        var total = count * copies;
        var desc  = sourceNums.Length > 0 ? string.Join(", ", sourceNums) : $"{count} source sheet{(count == 1 ? "" : "s")}";
        return $"Create {total} duplicate sheet{(total == 1 ? "" : "s")} from {desc} using mode '{mode}' and suffix '{numSuffix}'.";
    }

    private static string BuildCreateRevision(McpToolRequest request)
    {
        var description = ToolArguments.GetString(request.Arguments, "description");
        var sheetIds = ToolArguments.GetLongArray(request.Arguments, "sheetIds");
        var sheetNumbers = ToolArguments.GetStringArray(request.Arguments, "sheetNumbers");
        var sheetCount = sheetIds.Length > 0 ? sheetIds.Length : sheetNumbers.Length;
        var assignment = sheetCount > 0
            ? $" and assign it to {sheetCount} sheet{(sheetCount == 1 ? "" : "s")}"
            : string.Empty;
        return $"Create revision '{description}'{assignment}.";
    }

    private static string BuildDuplicateViews(McpToolRequest request)
    {
        var viewIds = ToolArguments.GetLongArray(request.Arguments, "viewIds");
        var option  = ToolArguments.GetString(request.Arguments, "duplicateOption", "DuplicateWithDetailing");
        var copies = ToolArguments.GetInt(request.Arguments, "numberOfCopies", 1);
        var total = viewIds.Length * copies;
        return $"Create {total} duplicate view{(total == 1 ? "" : "s")} from {viewIds.Length} source view{(viewIds.Length == 1 ? "" : "s")} with option '{option}'.";
    }

    private static string BuildSetViewCropRegions(McpToolRequest request)
    {
        var referenceViewId = ToolArguments.GetLong(request.Arguments, "referenceViewId", 0L);
        var targetViewIds = ToolArguments.GetLongArray(request.Arguments, "targetViewIds");
        return $"Copy crop region from reference view ID:{referenceViewId} to " +
               $"{targetViewIds.Length} target view{(targetViewIds.Length == 1 ? "" : "s")}.";
    }

    private static string BuildApplyViewTemplate(McpToolRequest request)
    {
        var viewTemplateId = ToolArguments.GetLong(request.Arguments, "viewTemplateId", 0L);
        var viewIds        = ToolArguments.GetLongArray(request.Arguments, "viewIds");
        var viewTypes      = ToolArguments.GetStringArray(request.Arguments, "viewTypes");
        var nameFilter     = ToolArguments.GetString(request.Arguments, "nameFilter");
        var limit          = ToolArguments.GetInt(request.Arguments, "limit", 500);

        var templatePart = viewTemplateId > 0 ? $"template ID:{viewTemplateId}" : "view template";

        string targetPart;
        if (viewIds.Length > 0)
            targetPart = $"{viewIds.Length} view{(viewIds.Length == 1 ? "" : "s")}";
        else
        {
            var parts = new System.Collections.Generic.List<string>();
            if (viewTypes.Length > 0) parts.Add($"types [{string.Join(", ", viewTypes)}]");
            if (!string.IsNullOrWhiteSpace(nameFilter)) parts.Add($"name contains '{nameFilter}'");
            targetPart = parts.Count > 0
                ? $"views matching {string.Join(" + ", parts)} (limit {limit})"
                : $"all views (limit {limit})";
        }

        return $"Apply {templatePart} to {targetPart}.";
    }

    private static string BuildRenameViews(McpToolRequest request)
    {
        var viewIds = ToolArguments.GetLongArray(request.Arguments, "viewIds");
        var mode    = ToolArguments.GetString(request.Arguments, "mode");
        var target = ToolArguments.GetString(request.Arguments, "target", "Name");
        return $"Update {viewIds.Length} view{(viewIds.Length == 1 ? "" : "s")} target '{target}' using mode '{mode}'.";
    }

    private static string BuildRenameSheets(McpToolRequest request)
    {
        var sheetIds = ToolArguments.GetLongArray(request.Arguments, "sheetIds");
        var mode     = ToolArguments.GetString(request.Arguments, "mode");
        return $"Rename {sheetIds.Length} sheet{(sheetIds.Length == 1 ? "" : "s")} using mode '{mode}'.";
    }

    private static string BuildApplySheetNaming(McpToolRequest request)
    {
        var sheetIds = ToolArguments.GetLongArray(request.Arguments, "sheetIds");
        var sheetNumbers = ToolArguments.GetStringArray(request.Arguments, "sheetNumbers");
        var count = sheetIds.Length > 0 ? sheetIds.Length : sheetNumbers.Length;
        var target = ToolArguments.GetString(request.Arguments, "targetParameter");
        var scope = count > 0 ? $"{count} selected sheet{(count == 1 ? "" : "s")}" : "the filtered sheets";
        return $"Apply parameter-driven naming to {scope}; target '{target}'.";
    }

    private static string BuildSetSheetParametersBulk(McpToolRequest request)
    {
        var sheetIds  = ToolArguments.GetLongArray(request.Arguments, "sheetIds");
        var sheetNums = ToolArguments.GetStringArray(request.Arguments, "sheetNumbers");
        var count = sheetIds.Length > 0 ? sheetIds.Length : sheetNums.Length;
        var paramCount = CountDictionary(request.Arguments, "parameters");
        var paramPart = paramCount > 0 ? $", {paramCount} parameter{(paramCount == 1 ? "" : "s")}" : "";
        return $"Set parameters on {count} sheet{(count == 1 ? "" : "s")}{paramPart}.";
    }

    private static string BuildSetViewParametersBulk(McpToolRequest request)
    {
        var viewIds    = ToolArguments.GetLongArray(request.Arguments, "viewIds");
        var paramCount = CountDictionary(request.Arguments, "parameters");
        var paramPart  = paramCount > 0 ? $", {paramCount} parameter{(paramCount == 1 ? "" : "s")}" : "";
        return $"Set parameters on {viewIds.Length} view{(viewIds.Length == 1 ? "" : "s")}{paramPart}.";
    }

    private static string BuildCreateSheetsFromTable(McpToolRequest request)
    {
        var rowCount = CountRows(request.Arguments);
        var titleBlockId = ToolArguments.GetLong(request.Arguments, "titleBlockId");
        var titleBlockPart = titleBlockId > 0 ? $" (titleBlock ID:{titleBlockId})" : "";
        return $"Create {rowCount} sheet{(rowCount == 1 ? "" : "s")} from table{titleBlockPart}.";
    }
    // ── Private helpers ────────────────────────────────────────────────────

    /// <summary>Counts rows from the 'rows' argument — handles JArray, object[], and JSON string.</summary>
    private static int CountRows(Dictionary<string, object?> args)
    {
        if (!args.TryGetValue("rows", out var val)) return 0;
        if (val is Newtonsoft.Json.Linq.JArray ja) return ja.Count;
        if (val is string s) { try { return Newtonsoft.Json.Linq.JArray.Parse(s).Count; } catch { return 0; } }
        if (val is System.Collections.IEnumerable e) { try { return e.Cast<object>().Count(); } catch { return 0; } }
        return 0;
    }

    /// <summary>Counts entries in a named dictionary/object argument — handles JObject and IDictionary.</summary>
    private static int CountDictionary(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var val)) return 0;
        if (val is Newtonsoft.Json.Linq.JObject jo) return jo.Count;
        if (val is System.Collections.IDictionary d) return d.Count;
        return 0;
    }

    /// <summary>Counts items in a named JArray argument (e.g. "updates", "rows").</summary>
    private static int CountArray(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var val)) return 0;
        if (val is Newtonsoft.Json.Linq.JArray ja) return ja.Count;
        if (val is string s) { try { return Newtonsoft.Json.Linq.JArray.Parse(s).Count; } catch { return 0; } }
        if (val is System.Collections.IEnumerable e) { try { return e.Cast<object>().Count(); } catch { return 0; } }
        return 0;
    }

    // ── File System ───────────────────────────────────────────────────────────

    private static string BuildFileWriteText(McpToolRequest request)
    {
        var path = ToolArguments.GetString(request.Arguments, "filePath");
        var overwrite = ToolArguments.GetBool(request.Arguments, "overwrite");
        var content = ToolArguments.GetString(request.Arguments, "content");
        var preview = content.Length > 80 ? content[..80] + "…" : content;
        return $"Write file: {path} ({content.Length:N0} chars). Overwrite={overwrite}.\nPreview: {preview}";
    }

    // ── Excel Modifier ────────────────────────────────────────────────────────

    private static string BuildExcelUpdateCells(McpToolRequest request)
    {
        var path = ToolArguments.GetString(request.Arguments, "filePath");
        var sheet = ToolArguments.GetString(request.Arguments, "worksheetName");
        var count = CountArray(request.Arguments, "updates");
        return $"Update {count} cell(s) in sheet '{sheet}'\nFile: {path}";
    }

    private static string BuildExcelInsertRows(McpToolRequest request)
    {
        var path = ToolArguments.GetString(request.Arguments, "filePath");
        var sheet = ToolArguments.GetString(request.Arguments, "worksheetName");
        var insertAt = ToolArguments.GetInt(request.Arguments, "insertAtRow");
        var count = CountArray(request.Arguments, "rows");
        return $"Insert {count} row(s) at row {insertAt} in sheet '{sheet}'\nFile: {path}";
    }

    private static string BuildExcelAppendTableRows(McpToolRequest request)
    {
        var path = ToolArguments.GetString(request.Arguments, "filePath");
        var sheet = ToolArguments.GetString(request.Arguments, "worksheetName");
        var count = CountArray(request.Arguments, "rows");
        return $"Append {count} row(s) to sheet '{sheet}'\nFile: {path}";
    }

    private static string BuildDeleteViews(McpToolRequest request)
    {
        var viewIds = ToolArguments.GetLongArray(request.Arguments, "viewIds");
        return $"DESTRUCTIVE: Delete {viewIds.Length} view{(viewIds.Length == 1 ? "" : "s")}. Manual approval required.";
    }

    private static string BuildDeleteSheets(McpToolRequest request)
    {
        var sheetIds = ToolArguments.GetLongArray(request.Arguments, "sheetIds");
        return $"DESTRUCTIVE: Delete {sheetIds.Length} sheet{(sheetIds.Length == 1 ? "" : "s")}. Manual approval required.";
    }

    private static string BuildDeleteElements(McpToolRequest request)
    {
        var elementCount = ToolArguments.GetLongArray(request.Arguments, "elementIds").Distinct().Count();
        return $"DESTRUCTIVE: Delete {elementCount} element{(elementCount == 1 ? "" : "s")} plus dependent elements. Manual approval required.";
    }

    private static string BuildCreateSkill(McpToolRequest request)
    {
        var skillId = ToolArguments.GetString(request.Arguments, "skillId");
        var name    = ToolArguments.GetString(request.Arguments, "name");
        var count   = CountArray(request.Arguments, "tasks");
        return $"Create skill '{name}' ({skillId}) with {count} task{(count == 1 ? "" : "s")} in the skill library. Writes a .skill.json file.";
    }

    private static string BuildPlaceDimensions(McpToolRequest request)
    {
        var kind = ToolArguments.GetString(request.Arguments, "kind", "aligned");
        var ids = ToolArguments.GetLongArray(request.Arguments, "elementIds");
        var viewId = ToolArguments.GetLong(request.Arguments, "viewId");
        var viewPart = viewId > 0 ? $" in view ID:{viewId}" : " in the active view";
        return $"Place {kind} dimension(s) referencing {ids.Length} element{(ids.Length == 1 ? "" : "s")}{viewPart}. Runs in transaction, supports Undo.";
    }

    private static string BuildUpdateSkill(McpToolRequest request)
    {
        var skillId = ToolArguments.GetString(request.Arguments, "skillId");
        var fields  = new List<string>();
        foreach (var key in new[] { "name", "description", "version", "author", "tasks", "stopOnCriticalFailure", "requiresUserConfirmationBeforeModelChanges" })
            if (request.Arguments.ContainsKey(key)) fields.Add(key);
        var fieldPart = fields.Count > 0 ? string.Join(", ", fields) : "no fields";
        return $"Update skill '{skillId}' ({fieldPart}). Rewrites its .skill.json file.";
    }

    private static string BuildCreateClashReviewView(McpToolRequest request)
    {
        var clashId = ToolArguments.GetString(request.Arguments, "clashId", "");
        var padding = request.Arguments.TryGetValue("sectionBoxPaddingMm", out var p) ? p?.ToString() ?? "1000" : "1000";
        return string.IsNullOrWhiteSpace(clashId)
            ? $"Create or reuse the 'MCP Clash Review' 3D view (no section box override)."
            : $"Create or reuse the 'MCP Clash Review' 3D view and scope section box to clash '{clashId}' with {padding}mm padding.";
    }

    private static string BuildFocusClash(McpToolRequest request)
    {
        var clashId = ToolArguments.GetString(request.Arguments, "clashId", "?");
        var padding = request.Arguments.TryGetValue("sectionBoxPaddingMm", out var p) ? p?.ToString() ?? "1000" : "1000";
        return $"Activate 'MCP Clash Review' view, frame section box around clash '{clashId}' ({padding}mm padding), and select the clash elements.";
    }

    private static string BuildSelectClashElements(McpToolRequest request)
    {
        var clashId = ToolArguments.GetString(request.Arguments, "clashId", "?");
        return $"Select the source and target elements of clash '{clashId}' in the Revit UI. Linked-model targets will select the link instance.";
    }
}
