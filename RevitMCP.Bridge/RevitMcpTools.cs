using System.ComponentModel;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using RevitMCP.Core.Models;

namespace RevitMCP.Bridge;

[McpServerToolType]
internal sealed class RevitMcpTools(RevitPipeClient pipeClient)
{
    [McpServerTool(Name = "revit_get_connection_status", ReadOnly = true),
     Description("Returns current Revit connection and document status including model title, worksharing info, active view, and selected element count.")]
    public async Task<string> GetConnectionStatus(CancellationToken cancellationToken)
    {
        var result = await pipeClient.SendAsync("revit_get_connection_status", [], cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_get_selected_elements", ReadOnly = true),
     Description("Returns the currently selected elements from the active Revit document with category, family, type, level, location, and bounding box.")]
    public async Task<string> GetSelectedElements(CancellationToken cancellationToken)
    {
        var result = await pipeClient.SendAsync("revit_get_selected_elements", [], cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_list_views", ReadOnly = true),
     Description("Lists all views in the active Revit document with type, template status, sheet placement, scale, and discipline.")]
    public async Task<string> ListViews(CancellationToken cancellationToken)
    {
        var result = await pipeClient.SendAsync("revit_list_views", [], cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_list_sheets", ReadOnly = true),
     Description("Lists all sheets in the active Revit document with sheet number, name, and the views placed on each sheet.")]
    public async Task<string> ListSheets(CancellationToken cancellationToken)
    {
        var result = await pipeClient.SendAsync("revit_list_sheets", [], cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_list_schedules", ReadOnly = true),
     Description("Lists all schedules in the active Revit document with name, category, and field names.")]
    public async Task<string> ListSchedules(CancellationToken cancellationToken)
    {
        var result = await pipeClient.SendAsync("revit_list_schedules", [], cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_get_element_parameters", ReadOnly = true),
     Description("Returns all parameters for specified elements or the current selection. Provide elementIds (list of integers) or set useSelection to true.")]
    public async Task<string> GetElementParameters(
        [Description("List of element IDs to get parameters for (integers)")] long[]? elementIds = null,
        [Description("If true, read parameters from the current Revit selection instead of elementIds")] bool useSelection = false,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["elementIds"] = elementIds ?? [],
            ["useSelection"] = useSelection
        };
        var result = await pipeClient.SendAsync("revit_get_element_parameters", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_count_elements", ReadOnly = true),
     Description("Counts model elements grouped by Category or FamilyAndType. Optionally filter to a specific category name.")]
    public async Task<string> CountElements(
        [Description("Optional category name to filter by (e.g. 'Fire Alarm Devices'). Leave empty for all categories.")] string? category = null,
        [Description("Group results by: 'Category' (default) or 'FamilyAndType'")] string groupBy = "Category",
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["category"] = category ?? string.Empty,
            ["groupBy"] = string.IsNullOrEmpty(groupBy) ? "Category" : groupBy
        };
        var result = await pipeClient.SendAsync("revit_count_elements", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_group_by_parameter", ReadOnly = true),
     Description("Groups model elements by a parameter value and returns counts. parameterName supports partial matching (e.g. 'ELENEA_Nimetus' matches 'ELENEA_ÜLD 001_Nimetus'). Optionally filter by category name.")]
    public async Task<string> GroupByParameter(
        [Description("Parameter name or partial name to match (case-insensitive)")] string parameterName,
        [Description("Optional category name to restrict search (e.g. 'Fire Alarm Devices')")] string? category = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["parameterName"] = parameterName,
            ["category"] = category ?? string.Empty
        };
        var result = await pipeClient.SendAsync("revit_group_by_parameter", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_get_available_parameters", ReadOnly = true),
     Description("Discovers available parameters for a category, selection, or element IDs. Returns parameter metadata, fill statistics, and example values.")]
    public async Task<string> GetAvailableParameters(
        [Description("Category name to scan")] string? category = null,
        [Description("If true, scan current selection")] bool useSelection = false,
        [Description("Explicit element IDs to scan")] long[]? elementIds = null,
        [Description("Include instance parameters")] bool includeInstanceParameters = true,
        [Description("Include type parameters")] bool includeTypeParameters = true,
        [Description("Max elements to sample (default 500)")] int sampleLimit = 500,
        [Description("Max example values per parameter (default 5)")] int exampleValueLimit = 5,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["category"] = category ?? string.Empty,
            ["useSelection"] = useSelection,
            ["elementIds"] = elementIds ?? [],
            ["includeInstanceParameters"] = includeInstanceParameters,
            ["includeTypeParameters"] = includeTypeParameters,
            ["sampleLimit"] = sampleLimit,
            ["exampleValueLimit"] = exampleValueLimit
        };
        var result = await pipeClient.SendAsync("revit_get_available_parameters", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_list_query_presets", ReadOnly = true),
     Description("Lists available reusable query presets.")]
    public async Task<string> ListQueryPresets(CancellationToken cancellationToken)
    {
        var result = await pipeClient.SendAsync("revit_list_query_presets", [], cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_run_query_preset", ReadOnly = true),
     Description("Runs a saved query preset by name. Can return JSON results or export to Excel.")]
    public async Task<string> RunQueryPreset(
        [Description("Name of the preset to run")] string presetName,
        [Description("If true, export results to Excel")] bool exportToExcel = false,
        [Description("Output file name for Excel export")] string? fileName = null,
        [Description("Max elements (default 5000)")] int limit = 5000,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["presetName"] = presetName,
            ["exportToExcel"] = exportToExcel,
            ["fileName"] = fileName ?? string.Empty,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_run_query_preset", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_check_parameter_completeness", ReadOnly = true),
     Description("Checks whether required parameters exist and are filled for elements. Useful for model QA.")]
    public async Task<string> CheckParameterCompleteness(
        [Description("Category name")] string? category = null,
        [Description("If true, check current selection")] bool useSelection = false,
        [Description("Explicit element IDs")] long[]? elementIds = null,
        [Description("List of parameter names to check")] string[] requiredParameters = default!,
        [Description("Include instance parameters")] bool includeInstanceParameters = true,
        [Description("Include type parameters")] bool includeTypeParameters = true,
        [Description("Treat whitespace-only values as empty")] bool treatWhitespaceAsEmpty = true,
        [Description("Include problem element details")] bool includeElementIds = true,
        [Description("Max elements to check (default 5000)")] int limit = 5000,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["category"] = category ?? string.Empty,
            ["useSelection"] = useSelection,
            ["elementIds"] = elementIds ?? [],
            ["requiredParameters"] = requiredParameters ?? [],
            ["includeInstanceParameters"] = includeInstanceParameters,
            ["includeTypeParameters"] = includeTypeParameters,
            ["treatWhitespaceAsEmpty"] = treatWhitespaceAsEmpty,
            ["includeElementIds"] = includeElementIds,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_check_parameter_completeness", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_export_view_list_to_excel", ReadOnly = true),
     Description("Exports all views to a formatted .xlsx file.")]
    public async Task<string> ExportViewListToExcel(
        [Description("Include template views")] bool includeTemplates = false,
        [Description("Include views not placed on sheets")] bool includeUnplacedViews = true,
        [Description("Output file name")] string fileName = "Revit_View_List.xlsx",
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["includeTemplates"] = includeTemplates,
            ["includeUnplacedViews"] = includeUnplacedViews,
            ["fileName"] = fileName
        };
        var result = await pipeClient.SendAsync("revit_export_view_list_to_excel", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_export_sheet_list_to_excel", ReadOnly = true),
     Description("Exports all sheets to a formatted .xlsx file.")]
    public async Task<string> ExportSheetListToExcel(
        [Description("Include list of placed views per sheet")] bool includePlacedViews = true,
        [Description("Output file name")] string fileName = "Revit_Sheet_List.xlsx",
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["includePlacedViews"] = includePlacedViews,
            ["fileName"] = fileName
        };
        var result = await pipeClient.SendAsync("revit_export_sheet_list_to_excel", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_export_schedule_list_to_excel", ReadOnly = true),
     Description("Exports all schedules to a formatted .xlsx file.")]
    public async Task<string> ExportScheduleListToExcel(
        [Description("Include field names")] bool includeFields = true,
        [Description("Output file name")] string fileName = "Revit_Schedule_List.xlsx",
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["includeFields"] = includeFields,
            ["fileName"] = fileName
        };
        var result = await pipeClient.SendAsync("revit_export_schedule_list_to_excel", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_select_elements"),
     Description("Selects elements in the active Revit UI by explicit element IDs. Does not modify model data.")]
    public async Task<string> SelectElements(
        [Description("List of element IDs to select")] long[] elementIds,
        [Description("Replace current selection (true) or add to it (false)")] bool replaceSelection = true,
        [Description("Zoom to selected elements")] bool zoomToSelection = false,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["elementIds"] = elementIds ?? [],
            ["replaceSelection"] = replaceSelection,
            ["zoomToSelection"] = zoomToSelection
        };
        var result = await pipeClient.SendAsync("revit_select_elements", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_select_elements_by_query"),
     Description("Selects elements in the active Revit UI based on category and parameter filters.")]
    public async Task<string> SelectElementsByQuery(
        [Description("Category name")] string? category = null,
        [Description("JSON array of parameter filters")] string? filters = null,
        [Description("Replace current selection")] bool replaceSelection = true,
        [Description("Zoom to selected elements")] bool zoomToSelection = false,
        [Description("Max elements to select (default 500)")] int limit = 500,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseJsonArray(filters, "filters", out var parsedFilters, out var filtersError))
            return FormatBridgeError(filtersError!);

        var args = new Dictionary<string, object?>
        {
            ["category"] = category ?? string.Empty,
            ["filters"] = parsedFilters,
            ["replaceSelection"] = replaceSelection,
            ["zoomToSelection"] = zoomToSelection,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_select_elements_by_query", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_set_parameter"),
     Description("Sets a parameter value on elements. Requires approval. Supports String, Integer, Double, and ElementId storage types. ElementId values can be provided as a numeric element ID or exact element/type name. Runs inside a Revit Transaction.")]
    public async Task<string> SetParameter(
        [Description("Parameter name to set (partial match supported)")] string parameterName,
        [Description("Value to set")] string value,
        [Description("Parameter scope: Instance or Type")] string scope = "Instance",
        [Description("If true, modify current selection")] bool useSelection = false,
        [Description("Explicit element IDs")] long[]? elementIds = null,
        [Description("Category name")] string? category = null,
        [Description("JSON array of parameter filters")] string? filters = null,
        [Description("Max elements to modify (default 500)")] int limit = 500,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseJsonArray(filters, "filters", out var parsedFilters, out var filtersError))
            return FormatBridgeError(filtersError!);

        var args = new Dictionary<string, object?>
        {
            ["parameterName"] = parameterName,
            ["value"] = value,
            ["scope"] = scope,
            ["useSelection"] = useSelection,
            ["elementIds"] = elementIds ?? [],
            ["category"] = category ?? string.Empty,
            ["filters"] = parsedFilters,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_set_parameter", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_find_elements_by_parameter", ReadOnly = true),
     Description("Finds model elements matching one or more parameter filters. Each filter specifies: parameterName (partial match), operator (equals/contains/startsWith/isEmpty/greaterThan/lessThan/notEquals/notContains/endsWith/isNotEmpty), value, matchMode (Contains/ContainsNormalized/Exact/ExactNormalized), scope (InstanceAndType/Instance/Type). Also accepts category, useSelection, elementIds, returnParameters, includeInstanceParameters, includeTypeParameters, limit.")]
    public async Task<string> FindElementsByParameter(
        [Description("JSON array of filter objects: [{parameterName, operator, value, matchMode, scope}]")] string? filters = null,
        [Description("Optional category name to restrict search (e.g. 'Fire Alarm Devices')")] string? category = null,
        [Description("Optional list of parameter names to include in returned elements")] string[]? returnParameters = null,
        [Description("Max elements to return (default 500)")] int limit = 500,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseJsonArray(filters, "filters", out var parsedFilters, out var filtersError))
            return FormatBridgeError(filtersError!);

        var args = new Dictionary<string, object?>
        {
            ["category"] = category ?? string.Empty,
            ["filters"] = parsedFilters,
            ["returnParameters"] = returnParameters ?? [],
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_find_elements_by_parameter", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_get_elements_info", ReadOnly = true),
     Description("Returns structured element info and selected parameter values. Accepts: useSelection (bool), elementIds (int[]), category (string), filters (JSON array of {parameterName, operator, value, matchMode, scope}), parameterNames (string[]), includeInstanceParameters (bool), includeTypeParameters (bool), limit (int).")]
    public async Task<string> GetElementsInfo(
        [Description("If true, use current selection")] bool useSelection = false,
        [Description("List of element IDs")] long[]? elementIds = null,
        [Description("Category name filter")] string? category = null,
        [Description("JSON array of parameter filters")] string? filters = null,
        [Description("Parameter names to return (partial match)")] string[]? parameterNames = null,
        [Description("Include instance parameters")] bool includeInstanceParameters = true,
        [Description("Include type parameters")] bool includeTypeParameters = true,
        [Description("Max elements to return (default 500)")] int limit = 500,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseJsonArray(filters, "filters", out var parsedFilters, out var filtersError))
            return FormatBridgeError(filtersError!);

        var args = new Dictionary<string, object?>
        {
            ["useSelection"] = useSelection,
            ["elementIds"] = elementIds ?? [],
            ["category"] = category ?? string.Empty,
            ["filters"] = parsedFilters,
            ["parameterNames"] = parameterNames ?? [],
            ["includeInstanceParameters"] = includeInstanceParameters,
            ["includeTypeParameters"] = includeTypeParameters,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_get_elements_info", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_group_elements", ReadOnly = true),
     Description("Groups model elements by one or more keys: Category, Family, Type, Level, or Parameter. groupBy is a JSON array of {type, parameterName, scope}. Returns flat rows (for Excel) and nested dict (for AI). Also accepts: category, filters, useSelection, elementIds, includeElements (bool), limit.")]
    public async Task<string> GroupElements(
        [Description("JSON array of groupBy keys: [{type, parameterName, scope}]")] string groupBy,
        [Description("Optional category name to restrict search")] string? category = null,
        [Description("JSON array of parameter filters")] string? filters = null,
        [Description("If true, include element IDs in each group")] bool includeElements = false,
        [Description("Max elements to scan (default 5000)")] int limit = 5000,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseJsonArray(groupBy, "groupBy", out var parsedGroupBy, out var groupByError))
            return FormatBridgeError(groupByError!);

        if (!TryParseJsonArray(filters, "filters", out var parsedFilters, out var filtersError))
            return FormatBridgeError(filtersError!);

        var args = new Dictionary<string, object?>
        {
            ["groupBy"] = parsedGroupBy,
            ["category"] = category ?? string.Empty,
            ["filters"] = parsedFilters,
            ["includeElements"] = includeElements,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_group_elements", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_export_query_to_excel", ReadOnly = true),
     Description("Queries model elements and exports results to an .xlsx file. Returns the file path. Accepts: category, filters (JSON array), groupBy (JSON array), parameters (string[] of param names to include), outputMode (Elements/Groups/Both), fileName, useSelection, elementIds, limit.")]
    public async Task<string> ExportQueryToExcel(
        [Description("Optional category name")] string? category = null,
        [Description("JSON array of parameter filters")] string? filters = null,
        [Description("JSON array of groupBy keys")] string? groupBy = null,
        [Description("Parameter names to include as columns")] string[]? parameters = null,
        [Description("What to export: Elements, Groups, or Both")] string outputMode = "Both",
        [Description("Output file name (default RevitMCP_Export.xlsx)")] string fileName = "RevitMCP_Export.xlsx",
        [Description("Max elements (default 5000)")] int limit = 5000,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseJsonArray(filters, "filters", out var parsedFilters, out var filtersError))
            return FormatBridgeError(filtersError!);

        if (!TryParseJsonArray(groupBy, "groupBy", out var parsedGroupBy, out var groupByError))
            return FormatBridgeError(groupByError!);

        var args = new Dictionary<string, object?>
        {
            ["category"] = category ?? string.Empty,
            ["filters"] = parsedFilters,
            ["groupBy"] = parsedGroupBy,
            ["parameters"] = parameters ?? [],
            ["outputMode"] = outputMode,
            ["fileName"] = fileName,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_export_query_to_excel", args, cancellationToken);
        return FormatResult(result);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static bool TryParseJsonArray(
        string? json,
        string argumentName,
        out object parsed,
        out string? error)
    {
        parsed = new object[] { };
        error = null;

        if (string.IsNullOrWhiteSpace(json))
            return true;

        try
        {
            var token = Newtonsoft.Json.Linq.JToken.Parse(json);

            if (token is not Newtonsoft.Json.Linq.JArray)
            {
                error = $"{argumentName} must be a JSON array.";
                return false;
            }

            parsed = token;
            return true;
        }
        catch (Exception ex)
        {
            error = $"{argumentName} could not be parsed as JSON array: {ex.Message}";
            return false;
        }
    }

    private static string FormatBridgeError(string message)
    {
        var response = new
        {
            success = false,
            message,
            durationMs = 0,
            data = (object?)null,
            warnings = Array.Empty<string>(),
            errors = new[] { message }
        };
        return JsonConvert.SerializeObject(response, Formatting.Indented);
    }

    private static string FormatResult(McpToolResult result)
    {
        var response = new
        {
            success = result.Success,
            status = result.Status,
            message = result.Message,
            durationMs = result.DurationMs,
            data = result.Data,
            warnings = result.Warnings,
            errors = result.Errors
        };
        return JsonConvert.SerializeObject(response, Formatting.Indented);
    }

    // ── Electrical Circuit Tools ──────────────────────────────────────────────

    [McpServerTool(Name = "revit_get_electrical_circuits", ReadOnly = true),
     Description("Lists electrical circuits (systems) in the active Revit document. Filter by panelName, circuitNumber, systemType (e.g. PowerCircuit). Options: includeElements (bool), includeParameters (bool), limit (int).")]
    public async Task<string> GetElectricalCircuits(
        [Description("Optional panel name filter (partial match)")] string? panelName = null,
        [Description("Optional circuit number filter (partial match)")] string? circuitNumber = null,
        [Description("Optional system type filter (e.g. PowerCircuit, Data, FireAlarm)")] string? systemType = null,
        [Description("Include connected elements in response")] bool includeElements = true,
        [Description("Include circuit parameters in response")] bool includeParameters = false,
        [Description("Max circuits to return (default 500)")] int limit = 500,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["panelName"] = panelName ?? string.Empty,
            ["circuitNumber"] = circuitNumber ?? string.Empty,
            ["systemType"] = systemType ?? string.Empty,
            ["includeElements"] = includeElements,
            ["includeParameters"] = includeParameters,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_get_electrical_circuits", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_get_circuit_info", ReadOnly = true),
     Description("Returns detailed information for one electrical circuit by element ID.")]
    public async Task<string> GetCircuitInfo(
        [Description("Element ID of the circuit")] long circuitId,
        [Description("Include connected elements")] bool includeElements = true,
        [Description("Include circuit parameters")] bool includeCircuitParameters = true,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["circuitId"] = circuitId,
            ["includeElements"] = includeElements,
            ["includeCircuitParameters"] = includeCircuitParameters
        };
        var result = await pipeClient.SendAsync("revit_get_circuit_info", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_get_available_panels", ReadOnly = true),
     Description("Lists electrical equipment elements (panels/distribution boards) that circuits can be assigned to.")]
    public async Task<string> GetAvailablePanels(
        [Description("Optional name filter (partial match)")] string? nameContains = null,
        [Description("Max panels to return (default 500)")] int limit = 500,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["nameContains"] = nameContains ?? string.Empty,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_get_available_panels", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_get_available_cable_types", ReadOnly = true),
     Description("Lists cable types in the project if available. Returns a warning if cable types are not separately defined — use revit_get_available_wire_types in that case.")]
    public async Task<string> GetAvailableCableTypes(CancellationToken cancellationToken = default)
    {
        var result = await pipeClient.SendAsync("revit_get_available_cable_types", [], cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_get_available_wire_types", ReadOnly = true),
     Description("Lists all wire types available in the active Revit document.")]
    public async Task<string> GetAvailableWireTypes(CancellationToken cancellationToken = default)
    {
        var result = await pipeClient.SendAsync("revit_get_available_wire_types", [], cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_get_circuit_compatible_elements", ReadOnly = true),
     Description("Finds elements and checks whether they can be added to an electrical circuit. Supports useSelection, elementIds, or category+filters query. Optionally validates against a targetCircuitId.")]
    public async Task<string> GetCircuitCompatibleElements(
        [Description("If true, check current Revit selection")] bool useSelection = false,
        [Description("Explicit element IDs to check")] long[]? elementIds = null,
        [Description("Category name for query")] string? category = null,
        [Description("JSON array of parameter filters")] string? filters = null,
        [Description("Target circuit ID to validate membership against (optional)")] long targetCircuitId = 0,
        [Description("Max elements (default 500)")] int limit = 500,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseJsonArray(filters, "filters", out var parsedFilters, out var filtersError))
            return FormatBridgeError(filtersError!);

        var args = new Dictionary<string, object?>
        {
            ["useSelection"] = useSelection,
            ["elementIds"] = elementIds ?? [],
            ["category"] = category ?? string.Empty,
            ["filters"] = parsedFilters,
            ["targetCircuitId"] = targetCircuitId,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_get_circuit_compatible_elements", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_create_electrical_circuit"),
     Description("Creates a new electrical circuit. Requires approval. Source: useSelection, elementIds, or category+filters. Optional: systemType (PowerCircuit/Data/FireAlarm/etc), panelElementId, panelName, wireTypeName.")]
    public async Task<string> CreateElectricalCircuit(
        [Description("If true, use current Revit selection")] bool useSelection = false,
        [Description("Explicit element IDs to add")] long[]? elementIds = null,
        [Description("Category name for query")] string? category = null,
        [Description("JSON array of parameter filters")] string? filters = null,
        [Description("Electrical system type (default PowerCircuit)")] string systemType = "PowerCircuit",
        [Description("Panel element ID (preferred over panelName)")] long panelElementId = 0,
        [Description("Panel name (fallback if panelElementId not provided)")] string? panelName = null,
        [Description("Wire type name to assign to the new circuit")] string? wireTypeName = null,
        [Description("Max elements (default 500)")] int limit = 500,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseJsonArray(filters, "filters", out var parsedFilters, out var filtersError))
            return FormatBridgeError(filtersError!);

        var args = new Dictionary<string, object?>
        {
            ["useSelection"] = useSelection,
            ["elementIds"] = elementIds ?? [],
            ["category"] = category ?? string.Empty,
            ["filters"] = parsedFilters,
            ["systemType"] = systemType,
            ["panelElementId"] = panelElementId,
            ["panelName"] = panelName ?? string.Empty,
            ["wireTypeName"] = wireTypeName ?? string.Empty,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_create_electrical_circuit", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_add_elements_to_circuit"),
     Description("Adds elements to an existing electrical circuit. Requires approval. Provide targetCircuitId and source: useSelection, elementIds, or category+filters.")]
    public async Task<string> AddElementsToCircuit(
        [Description("Target circuit element ID")] long targetCircuitId,
        [Description("If true, use current Revit selection")] bool useSelection = false,
        [Description("Explicit element IDs to add")] long[]? elementIds = null,
        [Description("Category name for query")] string? category = null,
        [Description("JSON array of parameter filters")] string? filters = null,
        [Description("Max elements (default 500)")] int limit = 500,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseJsonArray(filters, "filters", out var parsedFilters, out var filtersError))
            return FormatBridgeError(filtersError!);

        var args = new Dictionary<string, object?>
        {
            ["targetCircuitId"] = targetCircuitId,
            ["useSelection"] = useSelection,
            ["elementIds"] = elementIds ?? [],
            ["category"] = category ?? string.Empty,
            ["filters"] = parsedFilters,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_add_elements_to_circuit", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_reassign_circuit_panel"),
     Description("Reassigns an electrical circuit to another panel. Requires approval. Provide circuitId and targetPanelElementId (preferred) or targetPanelName.")]
    public async Task<string> ReassignCircuitPanel(
        [Description("Circuit element ID")] long circuitId,
        [Description("Target panel element ID (preferred)")] long targetPanelElementId = 0,
        [Description("Target panel name (fallback)")] string? targetPanelName = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["circuitId"] = circuitId,
            ["targetPanelElementId"] = targetPanelElementId,
            ["targetPanelName"] = targetPanelName ?? string.Empty
        };
        var result = await pipeClient.SendAsync("revit_reassign_circuit_panel", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_change_circuit_cable_or_wire_type"),
     Description("Changes the cable/wire type of a circuit. Requires approval. Provide cableTypeName and/or wireTypeName. preferCableType=true tries cable type first and falls back to wire type if fallbackToWireType=true.")]
    public async Task<string> ChangeCircuitCableOrWireType(
        [Description("Circuit element ID")] long circuitId,
        [Description("Cable type name to assign (resolved as WireType)")] string? cableTypeName = null,
        [Description("Wire type name to assign")] string? wireTypeName = null,
        [Description("Try cable type first (default true)")] bool preferCableType = true,
        [Description("Fall back to wire type if cable type not found (default true)")] bool fallbackToWireType = true,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["circuitId"] = circuitId,
            ["cableTypeName"] = cableTypeName ?? string.Empty,
            ["wireTypeName"] = wireTypeName ?? string.Empty,
            ["preferCableType"] = preferCableType,
            ["fallbackToWireType"] = fallbackToWireType
        };
        var result = await pipeClient.SendAsync("revit_change_circuit_cable_or_wire_type", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_set_circuit_parameter"),
     Description(
         "Sets a parameter value on one or more electrical circuits. Handles ALL storage types including " +
         "ElementId (e.g. 'Cable Type' parameters that reference a wire/cable type element). " +
         "'value' accepts: a numeric element ID (as string) for ElementId params, or a literal string/number. " +
         "Requires approval. Transaction-wrapped. Returns per-circuit success/failure detail.")]
    public async Task<string> SetCircuitParameter(
        [Description("Element IDs of the target circuits")] long[] circuitIds,
        [Description("Parameter name to set (partial match supported)")] string parameterName,
        [Description(
            "Value to assign. For ElementId parameters: provide the numeric element ID (e.g. '2518789') " +
            "or the exact element name (e.g. 'XX_EN_IT_Cat6a'). For String/Integer/Double: provide the value directly.")]
        string value,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["circuitIds"] = circuitIds ?? [],
            ["parameterName"] = parameterName,
            ["value"] = value
        };
        var result = await pipeClient.SendAsync("revit_set_circuit_parameter", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_find_uncircuited_elements", ReadOnly = true),
     Description("Finds elements in electrical/lighting/data/fire/security categories that have no electrical circuit assignment. Checks via MEPModel.ElectricalSystems. Accepts: categories (string[], default all electrical), useSelection (bool), filters (JSON array), returnParameters (string[]), limit (int, default 1000).")]
    public async Task<string> FindUncircuitedElements(
        [Description("Category names to scan (empty = all default electrical categories)")] string[]? categories = null,
        [Description("If true, check current Revit selection instead of categories")] bool useSelection = false,
        [Description("JSON array of parameter filters")] string? filters = null,
        [Description("Parameter names to include in each result (partial match supported)")] string[]? returnParameters = null,
        [Description("Max uncircuited elements to return (default 1000)")] int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseJsonArray(filters, "filters", out var parsedFilters, out var filtersError))
            return FormatBridgeError(filtersError!);

        var args = new Dictionary<string, object?>
        {
            ["categories"] = categories ?? Array.Empty<string>(),
            ["useSelection"] = useSelection,
            ["filters"] = parsedFilters,
            ["returnParameters"] = returnParameters ?? Array.Empty<string>(),
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_find_uncircuited_elements", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_check_circuit_health", ReadOnly = true),
     Description("Central circuit QA tool. Configurable checks: MissingPanel, EmptyCircuitNumber, DuplicateCircuitNumbers, MissingCableType, MissingWireType, MissingLoadName, NoConnectedElements. Filter by panelName or systemType. Returns issue details with circuit IDs.")]
    public async Task<string> CheckCircuitHealth(
        [Description("Optional panel name filter (partial match)")] string? panelName = null,
        [Description("Optional system type filter (e.g. PowerCircuit, Data, FireAlarm)")] string? systemType = null,
        [Description("Checks to run — default all: MissingPanel, EmptyCircuitNumber, DuplicateCircuitNumbers, MissingCableType, MissingWireType, MissingLoadName, NoConnectedElements")] string[]? checks = null,
        [Description("Include connected elements for circuits with issues")] bool includeElements = true,
        [Description("Max circuits to check (default 5000)")] int limit = 5000,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["panelName"] = panelName ?? string.Empty,
            ["systemType"] = systemType ?? string.Empty,
            ["checks"] = checks ?? Array.Empty<string>(),
            ["includeElements"] = includeElements,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_check_circuit_health", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_export_panel_circuit_list_to_excel", ReadOnly = true),
     Description("Exports a panel-organized circuit report to .xlsx. Sheets: Summary, Panel Circuits, Circuit Elements (optional), Health Issues (optional). Returns file path. Columns: Panel, Circuit Number, Load Name, Circuit Id, System Type, Elements Count, Apparent Load, Voltage, Poles, Cable/Wire Type, Comments.")]
    public async Task<string> ExportPanelCircuitListToExcel(
        [Description("Optional panel name filter")] string? panelName = null,
        [Description("Optional system type filter")] string? systemType = null,
        [Description("Include Circuit Elements sheet (can be slow for large models)")] bool includeElements = true,
        [Description("Include Health Issues sheet")] bool includeHealthCheck = true,
        [Description("Output file name (default Panel_Circuit_List.xlsx)")] string fileName = "Panel_Circuit_List.xlsx",
        [Description("Max circuits to export (default 5000)")] int limit = 5000,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["panelName"] = panelName ?? string.Empty,
            ["systemType"] = systemType ?? string.Empty,
            ["includeElements"] = includeElements,
            ["includeHealthCheck"] = includeHealthCheck,
            ["fileName"] = fileName,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_export_panel_circuit_list_to_excel", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_find_circuits_by_element_parameter", ReadOnly = true),
     Description("Finds electrical circuits that contain elements matching category and parameter filters. Example uses: find circuits in room 201, find circuits containing devices of type X, find circuits where ELENEA_Osasüsteem = ATS. Returns distinct circuits with matched element IDs.")]
    public async Task<string> FindCircuitsByElementParameter(
        [Description("Category name for element search (e.g. 'Electrical Fixtures', 'Fire Alarm Devices')")] string? elementCategory = null,
        [Description("JSON array of parameter filters on the elements")] string? filters = null,
        [Description("Include matched element IDs in each circuit result")] bool includeElements = true,
        [Description("Max candidate elements to scan (default 500)")] int limit = 500,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseJsonArray(filters, "filters", out var parsedFilters, out var filtersError))
            return FormatBridgeError(filtersError!);

        var args = new Dictionary<string, object?>
        {
            ["elementCategory"] = elementCategory ?? string.Empty,
            ["filters"] = parsedFilters,
            ["includeElements"] = includeElements,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_find_circuits_by_element_parameter", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_trace_circuit", ReadOnly = true),
     Description("Traces an element or circuit back to its panel. From an element (elementId or useSelection=true): finds its circuit(s) and panel(s). From a circuit (circuitId): finds the panel and optionally connected elements. Returns circuit number, load name, wire type, apparent load, panel name, panel element ID.")]
    public async Task<string> TraceCircuit(
        [Description("Element ID to trace (0 = not used)")] long elementId = 0,
        [Description("Circuit element ID to trace directly (0 = not used)")] long circuitId = 0,
        [Description("If true, trace the currently selected element in Revit")] bool useSelection = false,
        [Description("Include connected elements in circuit trace result")] bool includeConnectedElements = true,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["elementId"] = elementId,
            ["circuitId"] = circuitId,
            ["useSelection"] = useSelection,
            ["includeConnectedElements"] = includeConnectedElements
        };
        var result = await pipeClient.SendAsync("revit_trace_circuit", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_check_circuit_parameter_completeness", ReadOnly = true),
     Description("Checks required parameters on electrical circuit elements. Returns per-parameter fill rates and circuit IDs with empty values. requiredParameters defaults to [Circuit Number, Load Name, Cable Type].")]
    public async Task<string> CheckCircuitParameterCompleteness(
        [Description("Optional panel name filter")] string? panelName = null,
        [Description("Optional system type filter")] string? systemType = null,
        [Description("Parameter names to check (default: Circuit Number, Load Name, Cable Type)")] string[]? requiredParameters = null,
        [Description("Treat whitespace-only values as empty (default true)")] bool treatWhitespaceAsEmpty = true,
        [Description("Include circuit IDs in result (default true)")] bool includeCircuitIds = true,
        [Description("Max circuits to check (default 1000)")] int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["panelName"] = panelName ?? "",
            ["systemType"] = systemType ?? "",
            ["requiredParameters"] = requiredParameters ?? [],
            ["treatWhitespaceAsEmpty"] = treatWhitespaceAsEmpty,
            ["includeCircuitIds"] = includeCircuitIds,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_check_circuit_parameter_completeness", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_select_circuit_elements"),
     Description("Selects all elements connected to a circuit in the Revit UI. Requires approval.")]
    public async Task<string> SelectCircuitElements(
        [Description("Element ID of the circuit")] long circuitId,
        [Description("Replace current selection (default true)")] bool replaceSelection = true,
        [Description("Zoom to selection after selecting (default false)")] bool zoomToSelection = false,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["circuitId"] = circuitId,
            ["replaceSelection"] = replaceSelection,
            ["zoomToSelection"] = zoomToSelection
        };
        var result = await pipeClient.SendAsync("revit_select_circuit_elements", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_select_uncircuited_elements"),
     Description("Selects elements not assigned to any electrical circuit in the Revit UI. Requires approval.")]
    public async Task<string> SelectUncircuitedElements(
        [Description("Categories to search (default: all electrical categories)")] string[]? categories = null,
        [Description("Parameter filters as JSON array")] string? filters = null,
        [Description("Replace current selection (default true)")] bool replaceSelection = true,
        [Description("Zoom to selection after selecting (default false)")] bool zoomToSelection = false,
        [Description("Max elements to select (default 500)")] int limit = 500,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["categories"] = categories ?? [],
            ["filters"] = filters ?? "[]",
            ["replaceSelection"] = replaceSelection,
            ["zoomToSelection"] = zoomToSelection,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_select_uncircuited_elements", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_export_circuit_health_to_excel", ReadOnly = true),
     Description("Exports circuit QA health issues (missing panel, duplicate numbers, missing cable type, missing load name) to a formatted .xlsx file. Returns the file path.")]
    public async Task<string> ExportCircuitHealthToExcel(
        [Description("Optional panel name filter")] string? panelName = null,
        [Description("Optional system type filter")] string? systemType = null,
        [Description("Output file name (default: Circuit_Health_Report.xlsx)")] string fileName = "Circuit_Health_Report.xlsx",
        [Description("Max circuits to check (default 5000)")] int limit = 5000,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["panelName"] = panelName ?? "",
            ["systemType"] = systemType ?? "",
            ["fileName"] = fileName,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_export_circuit_health_to_excel", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_export_uncircuited_elements_to_excel", ReadOnly = true),
     Description("Exports elements not assigned to any electrical circuit to a formatted .xlsx file. Returns the file path.")]
    public async Task<string> ExportUncircuitedElementsToExcel(
        [Description("Categories to search (default: all electrical categories)")] string[]? categories = null,
        [Description("Parameter filters as JSON array")] string? filters = null,
        [Description("Additional parameters to include as columns")] string[]? returnParameters = null,
        [Description("Output file name (default: Uncircuited_Elements.xlsx)")] string fileName = "Uncircuited_Elements.xlsx",
        [Description("Max elements to export (default 2000)")] int limit = 2000,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["categories"] = categories ?? [],
            ["filters"] = filters ?? "[]",
            ["returnParameters"] = returnParameters ?? [],
            ["fileName"] = fileName,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_export_uncircuited_elements_to_excel", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_get_circuits_for_selected_elements", ReadOnly = true),
     Description("Returns all electrical circuits for the currently selected Revit elements, de-duplicated across multiple selected elements.")]
    public async Task<string> GetCircuitsForSelectedElements(
        [Description("Include connected elements in response (default true)")] bool includeElements = true,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["includeElements"] = includeElements };
        var result = await pipeClient.SendAsync("revit_get_circuits_for_selected_elements", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_find_elements_on_circuit", ReadOnly = true),
     Description("Lists all elements connected to a specific electrical circuit with category, family, type, level, and optional parameter values.")]
    public async Task<string> FindElementsOnCircuit(
        [Description("Element ID of the circuit")] long circuitId,
        [Description("Parameter names to include in results")] string[]? returnParameters = null,
        [Description("Max elements to return (default 500)")] int limit = 500,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["circuitId"] = circuitId,
            ["returnParameters"] = returnParameters ?? [],
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_find_elements_on_circuit", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_get_circuit_load_summary", ReadOnly = true),
     Description("Summarizes circuit apparent loads grouped by Panel, SystemType, CableType, or WireType.")]
    public async Task<string> GetCircuitLoadSummary(
        [Description("Grouping keys (default: [Panel, SystemType]). Valid: Panel, SystemType, CableType, WireType")] string[]? groupBy = null,
        [Description("Optional panel name filter")] string? panelName = null,
        [Description("Optional system type filter")] string? systemType = null,
        [Description("Include per-circuit details in each group (default false)")] bool includeCircuitDetails = false,
        [Description("Max circuits (default 5000)")] int limit = 5000,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["groupBy"] = groupBy ?? [],
            ["panelName"] = panelName ?? "",
            ["systemType"] = systemType ?? "",
            ["includeCircuitDetails"] = includeCircuitDetails,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_get_circuit_load_summary", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_check_panel_utilization", ReadOnly = true),
     Description("Checks circuit count, total apparent load, and data quality issues per panel. If panelName is empty, checks all panels.")]
    public async Task<string> CheckPanelUtilization(
        [Description("Optional panel name filter (empty = all panels)")] string? panelName = null,
        [Description("Optional system type filter")] string? systemType = null,
        [Description("Include per-circuit details in response (default false)")] bool includeCircuitDetails = false,
        [Description("Max circuits (default 5000)")] int limit = 5000,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["panelName"] = panelName ?? "",
            ["systemType"] = systemType ?? "",
            ["includeCircuitDetails"] = includeCircuitDetails,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_check_panel_utilization", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_preview_circuit_numbering", ReadOnly = true),
     Description("Previews renumbering proposals for panel circuits without modifying the model. Returns old/new circuit number pairs with willChange flag.")]
    public async Task<string> PreviewCircuitNumbering(
        [Description("Panel name (required)")] string panelName,
        [Description("Starting number (default 1)")] int startNumber = 1,
        [Description("Increment between numbers (default 1)")] int increment = 1,
        [Description("Optional system type filter")] string? systemType = null,
        [Description("Sort circuits by: CurrentCircuitNumber (default) or LoadName")] string sortBy = "CurrentCircuitNumber",
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["panelName"] = panelName,
            ["startNumber"] = startNumber,
            ["increment"] = increment,
            ["systemType"] = systemType ?? "",
            ["sortBy"] = sortBy
        };
        var result = await pipeClient.SendAsync("revit_preview_circuit_numbering", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_apply_circuit_numbering"),
     Description("Applies circuit number changes after preview. Requires approval. Runs inside a transaction.")]
    public async Task<string> ApplyCircuitNumbering(
        [Description("JSON array: [{\"circuitId\": 12345, \"newCircuitNumber\": \"5\"}, ...]")] string changes,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["changes"] = changes };
        var result = await pipeClient.SendAsync("revit_apply_circuit_numbering", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_preview_circuit_load_names", ReadOnly = true),
     Description("Previews load name proposals for circuits without modifying the model. Uses a template with {ParameterName} placeholders resolved from connected element or circuit parameters.")]
    public async Task<string> PreviewCircuitLoadNames(
        [Description("Optional panel name filter")] string? panelName = null,
        [Description("Template string with {ParameterName} placeholders, e.g. '{Room Number} {Category}'")] string? template = null,
        [Description("Parameter source: ConnectedElements (default) or CircuitParameters")] string source = "ConnectedElements",
        [Description("Optional system type filter")] string? systemType = null,
        [Description("Max circuits (default 500)")] int limit = 500,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["panelName"] = panelName ?? "",
            ["template"] = template ?? "",
            ["source"] = source,
            ["systemType"] = systemType ?? "",
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_preview_circuit_load_names", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_apply_circuit_load_names"),
     Description("Applies load name changes to circuits after preview. Requires approval. Runs inside a transaction.")]
    public async Task<string> ApplyCircuitLoadNames(
        [Description("JSON array: [{\"circuitId\": 12345, \"newLoadName\": \"201 Sockets\"}, ...]")] string changes,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["changes"] = changes };
        var result = await pipeClient.SendAsync("revit_apply_circuit_load_names", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_set_circuit_parameters_bulk"),
     Description("Sets multiple parameters on multiple circuits in a single transaction. Requires approval. Supports String, Integer, Double, and ElementId storage types.")]
    public async Task<string> SetCircuitParametersBulk(
        [Description("Circuit element IDs to target (optional — provide panelName if omitted)")] long[]? circuitIds = null,
        [Description("Panel name to target all circuits on a panel (used when circuitIds is empty)")] string? panelName = null,
        [Description("JSON array: [{\"parameterName\": \"Comments\", \"value\": \"Checked\"}, ...]")] string parameters = "[]",
        [Description("Max circuits when using panelName (default 1000)")] int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["circuitIds"] = circuitIds ?? [],
            ["panelName"] = panelName ?? "",
            ["parameters"] = parameters,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_set_circuit_parameters_bulk", args, cancellationToken);
        return FormatResult(result);
    }

    // ── Electrical Dashboard (Group A) ────────────────────────────────────────

    [McpServerTool(Name = "revit_get_electrical_dashboard_summary", ReadOnly = true),
     Description("Returns a compact model-wide electrical QA summary: panel/circuit counts, issue breakdown, top problem panels, system type summary, load summary. Accepts: includePanels (bool), includeSystemTypes (bool), includeTopIssues (bool), includeUncircuitedSummary (bool — slower), includeLoadSummary (bool), limit (int).")]
    public async Task<string> GetElectricalDashboardSummary(
        [Description("Include top-issue panels in response (default true)")] bool includePanels = true,
        [Description("Include system type breakdown (default true)")] bool includeSystemTypes = true,
        [Description("Include top-issue breakdown (default true)")] bool includeTopIssues = true,
        [Description("Include uncircuited element summary (slower, default true)")] bool includeUncircuitedSummary = true,
        [Description("Include load summary (default true)")] bool includeLoadSummary = true,
        [Description("Max circuits to process (default 5000)")] int limit = 5000,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["includePanels"] = includePanels,
            ["includeSystemTypes"] = includeSystemTypes,
            ["includeTopIssues"] = includeTopIssues,
            ["includeUncircuitedSummary"] = includeUncircuitedSummary,
            ["includeLoadSummary"] = includeLoadSummary,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_get_electrical_dashboard_summary", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_get_panel_issue_summary", ReadOnly = true),
     Description("Returns electrical QA data grouped by panel: circuit count, issue counts per type, total load. Accepts: panelName (optional partial filter), includeCircuitDetails (bool), includeIssueDetails (bool), limit (int).")]
    public async Task<string> GetPanelIssueSummary(
        [Description("Optional panel name filter (partial match)")] string? panelName = null,
        [Description("Include per-circuit details (default false)")] bool includeCircuitDetails = false,
        [Description("Include issue details (default true)")] bool includeIssueDetails = true,
        [Description("Max circuits to process (default 5000)")] int limit = 5000,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["panelName"] = panelName ?? string.Empty,
            ["includeCircuitDetails"] = includeCircuitDetails,
            ["includeIssueDetails"] = includeIssueDetails,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_get_panel_issue_summary", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_export_electrical_dashboard_to_excel", ReadOnly = true),
     Description("Exports the electrical dashboard summary to an .xlsx file (sheets: Summary, Issue Breakdown, Panel Summary, System Type Summary). Accepts: fileName, includePanelSummary (bool), includeIssueDetails (bool), includeSystemTypeSummary (bool), limit (int).")]
    public async Task<string> ExportElectricalDashboardToExcel(
        [Description("Output file name (default Electrical_Dashboard_Summary.xlsx)")] string fileName = "Electrical_Dashboard_Summary.xlsx",
        [Description("Include panel summary sheet (default true)")] bool includePanelSummary = true,
        [Description("Include issue details sheet (default true)")] bool includeIssueDetails = true,
        [Description("Include system type summary sheet (default true)")] bool includeSystemTypeSummary = true,
        [Description("Max circuits to process (default 5000)")] int limit = 5000,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["fileName"] = fileName,
            ["includePanelSummary"] = includePanelSummary,
            ["includeIssueDetails"] = includeIssueDetails,
            ["includeSystemTypeSummary"] = includeSystemTypeSummary,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_export_electrical_dashboard_to_excel", args, cancellationToken);
        return FormatResult(result);
    }

    // ── Voltage-Drop Preparation (Group B) ───────────────────────────────────

    [McpServerTool(Name = "revit_get_circuit_route_assumptions", ReadOnly = true),
     Description("Returns the data that would be used to estimate a circuit's length: panel and element model locations (meters), and an assumptions/warnings list. Does not estimate length. Accepts: circuitId (required), includeConnectedElements (bool), includeLocations (bool).")]
    public async Task<string> GetCircuitRouteAssumptions(
        [Description("Circuit element ID (required)")] long circuitId,
        [Description("Include connected elements in response (default true)")] bool includeConnectedElements = true,
        [Description("Include element location data (default true)")] bool includeLocations = true,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["circuitId"] = circuitId,
            ["includeConnectedElements"] = includeConnectedElements,
            ["includeLocations"] = includeLocations
        };
        var result = await pipeClient.SendAsync("revit_get_circuit_route_assumptions", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_estimate_circuit_length", ReadOnly = true),
     Description("Estimates the total cable length for a single circuit by computing distances from panel to connected elements in the model. Length method: StraightLineMax, StraightLineSum, ManhattanMax (default), ManhattanSum, NearestNeighborPath. Results are PRELIMINARY. Accepts: circuitId (required), method (string), routingMultiplier (double, default 1.25), includeElementBreakdown (bool).")]
    public async Task<string> EstimateCircuitLength(
        [Description("Circuit element ID (required)")] long circuitId,
        [Description("Length estimation method (default ManhattanMax)")] string method = "ManhattanMax",
        [Description("Multiplier to account for routing overhead (default 1.25)")] double routingMultiplier = 1.25,
        [Description("Include per-element distance breakdown")] bool includeElementBreakdown = false,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["circuitId"] = circuitId,
            ["method"] = method,
            ["routingMultiplier"] = routingMultiplier,
            ["includeElementBreakdown"] = includeElementBreakdown
        };
        var result = await pipeClient.SendAsync("revit_estimate_circuit_length", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_estimate_circuit_lengths", ReadOnly = true),
     Description("Estimates cable lengths for multiple circuits at once. Filter by panelName, systemType, or explicit circuitIds. Returns a row per circuit with raw and routed length estimates. Results are PRELIMINARY. Accepts: panelName, systemType, circuitIds (long[]), method, routingMultiplier, limit.")]
    public async Task<string> EstimateCircuitLengths(
        [Description("Optional panel name filter")] string? panelName = null,
        [Description("Optional system type filter")] string? systemType = null,
        [Description("Explicit circuit element IDs (optional)")] long[]? circuitIds = null,
        [Description("Length estimation method (default ManhattanMax)")] string method = "ManhattanMax",
        [Description("Routing multiplier (default 1.25)")] double routingMultiplier = 1.25,
        [Description("Max circuits (default 1000)")] int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["panelName"] = panelName ?? string.Empty,
            ["systemType"] = systemType ?? string.Empty,
            ["circuitIds"] = circuitIds ?? [],
            ["method"] = method,
            ["routingMultiplier"] = routingMultiplier,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_estimate_circuit_lengths", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_export_voltage_drop_input_to_excel", ReadOnly = true),
     Description("Exports circuit data and estimated lengths for manual voltage-drop calculations to .xlsx (sheets: Summary, Voltage Drop Input, Circuit Elements, Assumptions, Failures). Results are PRELIMINARY. Accepts: circuitIds (long[], optional — overrides other filters), panelName, systemType, method, routingMultiplier (double), fileName, limit.")]
    public async Task<string> ExportVoltageDropInputToExcel(
        [Description("Circuit element IDs to export (optional — when provided, panelName/systemType are ignored)")] long[]? circuitIds = null,
        [Description("Optional panel name filter (used when circuitIds is empty)")] string? panelName = null,
        [Description("Optional system type filter (used when circuitIds is empty)")] string? systemType = null,
        [Description("Length method (default ManhattanMax)")] string method = "ManhattanMax",
        [Description("Routing multiplier (default 1.25)")] double routingMultiplier = 1.25,
        [Description("Output file name (default Voltage_Drop_Input.xlsx)")] string fileName = "Voltage_Drop_Input.xlsx",
        [Description("Max circuits (default 1000)")] int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["circuitIds"] = circuitIds ?? [],
            ["panelName"] = panelName ?? string.Empty,
            ["systemType"] = systemType ?? string.Empty,
            ["method"] = method,
            ["routingMultiplier"] = routingMultiplier,
            ["fileName"] = fileName,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_export_voltage_drop_input_to_excel", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_get_voltage_drop_precheck", ReadOnly = true),
     Description("Reports whether circuits have enough data for voltage-drop calculation. Checks voltage, load, cable type, wire type, and location data. Does not calculate voltage drop. Accepts: circuitIds (long[], preferred for bulk) or circuitId (single long), requireCableType (bool), requireVoltage (bool), requireLoad (bool), requireLength (bool). Returns single result for one circuit, array summary for multiple.")]
    public async Task<string> GetVoltageDropPrecheck(
        [Description("Circuit element IDs (preferred — accepts one or more)")] long[]? circuitIds = null,
        [Description("Single circuit element ID (backward-compatible alternative to circuitIds)")] long circuitId = 0,
        [Description("Require cable type (default true)")] bool requireCableType = true,
        [Description("Require voltage (default true)")] bool requireVoltage = true,
        [Description("Require load (default true)")] bool requireLoad = true,
        [Description("Require estimable length (default true)")] bool requireLength = true,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["circuitIds"] = circuitIds ?? [],
            ["circuitId"] = circuitId,
            ["requireCableType"] = requireCableType,
            ["requireVoltage"] = requireVoltage,
            ["requireLoad"] = requireLoad,
            ["requireLength"] = requireLength
        };
        var result = await pipeClient.SendAsync("revit_get_voltage_drop_precheck", args, cancellationToken);
        return FormatResult(result);
    }

    // ── Fire Alarm / ATS Preset (Group C) ────────────────────────────────────

    [McpServerTool(Name = "revit_run_fire_alarm_circuit_preset", ReadOnly = true),
     Description("Runs the Fire Alarm Devices circuit preset: collects OST_FireAlarmDevices, groups by Ahela nr. (or custom loop parameter), resolves Seadme Nr. and device type, finds connected circuits, and classifies each loop (AddressableLoop, ConventionalSounderLine, ModuleLoop, Unknown). Accepts: panelName, loopParameterName (default 'Ahela nr.'), deviceNumberParameterName (default 'Seadme Nr.'), deviceTypeParameterName (default 'ELENEA_Nimetus'), descriptionParameterName, includeDeviceList (bool), includeCircuitInfo (bool), allowDeviceNumberXXX (bool), limit.")]
    public async Task<string> RunFireAlarmCircuitPreset(
        [Description("Optional panel name filter")] string? panelName = null,
        [Description("Loop/line parameter name (default 'Ahela nr.')")] string loopParameterName = "Ahela nr.",
        [Description("Device number parameter name (default 'Seadme Nr.')")] string deviceNumberParameterName = "Seadme Nr.",
        [Description("Device type parameter name (default 'ELENEA_Nimetus')")] string deviceTypeParameterName = "ELENEA_Nimetus",
        [Description("Description parameter name (default 'Description')")] string descriptionParameterName = "Description",
        [Description("Include device list in response (default true)")] bool includeDeviceList = true,
        [Description("Include circuit info per loop (default true)")] bool includeCircuitInfo = true,
        [Description("Allow device numbers matching 'xxx' prefix (default true)")] bool allowDeviceNumberXXX = true,
        [Description("Max devices to process (default 5000)")] int limit = 5000,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["panelName"] = panelName ?? string.Empty,
            ["loopParameterName"] = loopParameterName,
            ["deviceNumberParameterName"] = deviceNumberParameterName,
            ["deviceTypeParameterName"] = deviceTypeParameterName,
            ["descriptionParameterName"] = descriptionParameterName,
            ["includeDeviceList"] = includeDeviceList,
            ["includeCircuitInfo"] = includeCircuitInfo,
            ["allowDeviceNumberXXX"] = allowDeviceNumberXXX,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_run_fire_alarm_circuit_preset", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_export_fire_alarm_circuit_preset_to_excel", ReadOnly = true),
     Description("Exports fire alarm circuit preset results to .xlsx (sheets: Summary, Loop Summary, Device List, Circuit Info, Voltage Drop Input, Warnings). Accepts: panelName, loopParameterName, deviceTypeParameterName, includeVoltageDropInput (bool), sounderCurrentMilliAmps (double), fallbackResistanceOhmPerMeter (double), fileName, limit.")]
    public async Task<string> ExportFireAlarmCircuitPresetToExcel(
        [Description("Optional panel name filter")] string? panelName = null,
        [Description("Loop parameter name (default 'Ahela nr.')")] string loopParameterName = "Ahela nr.",
        [Description("Device type parameter name (default 'ELENEA_Nimetus')")] string deviceTypeParameterName = "ELENEA_Nimetus",
        [Description("Include voltage drop input sheet (default true)")] bool includeVoltageDropInput = true,
        [Description("Sounder current per device in mA (default 50)")] double sounderCurrentMilliAmps = 50.0,
        [Description("Fallback resistance Ω/m if no profile matches (default 0.035)")] double fallbackResistanceOhmPerMeter = 0.035,
        [Description("Output file name (default FireAlarm_Circuit_Preset.xlsx)")] string fileName = "FireAlarm_Circuit_Preset.xlsx",
        [Description("Max devices (default 5000)")] int limit = 5000,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["panelName"] = panelName ?? string.Empty,
            ["loopParameterName"] = loopParameterName,
            ["deviceTypeParameterName"] = deviceTypeParameterName,
            ["includeVoltageDropInput"] = includeVoltageDropInput,
            ["sounderCurrentMilliAmps"] = sounderCurrentMilliAmps,
            ["fallbackResistanceOhmPerMeter"] = fallbackResistanceOhmPerMeter,
            ["fileName"] = fileName,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_export_fire_alarm_circuit_preset_to_excel", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_get_fire_alarm_visualization_data", ReadOnly = true),
     Description("Returns structured fire alarm data for diagram/spatial visualization, grouped by Ahela nr. Each loop contains device list with element IDs, levels, device types, and model coordinates. Accepts: panelName, loopParameterName (default 'Ahela nr.'), deviceTypeParameterName, limit.")]
    public async Task<string> GetFireAlarmVisualizationData(
        [Description("Optional panel name filter")] string? panelName = null,
        [Description("Loop parameter name (default 'Ahela nr.')")] string loopParameterName = "Ahela nr.",
        [Description("Device type parameter name (default 'ELENEA_Nimetus')")] string deviceTypeParameterName = "ELENEA_Nimetus",
        [Description("Max devices (default 5000)")] int limit = 5000,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["panelName"] = panelName ?? string.Empty,
            ["loopParameterName"] = loopParameterName,
            ["deviceTypeParameterName"] = deviceTypeParameterName,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_get_fire_alarm_visualization_data", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_get_fire_alarm_voltage_drop_summary", ReadOnly = true),
     Description("Returns preliminary voltage-drop (sounder lines) or loop resistance (addressable loops) estimates for fire alarm circuits. Uses cable resistance profiles or fallback. Accepts: panelName, loopParameterName, deviceTypeParameterName, sounderCurrentMilliAmps (default 50), sounderSupplyVoltage (default 24), minimumSounderVoltage (default 18), addressableLoopMaxResistanceOhm (default 120), fallbackResistanceOhmPerMeter (default 0.035), limit.")]
    public async Task<string> GetFireAlarmVoltageDropSummary(
        [Description("Optional panel name filter")] string? panelName = null,
        [Description("Loop parameter name (default 'Ahela nr.')")] string loopParameterName = "Ahela nr.",
        [Description("Device type parameter name (default 'ELENEA_Nimetus')")] string deviceTypeParameterName = "ELENEA_Nimetus",
        [Description("Sounder current per device in mA (default 50)")] double sounderCurrentMilliAmps = 50.0,
        [Description("Supply voltage to sounder line V (default 24)")] double sounderSupplyVoltage = 24.0,
        [Description("Minimum required voltage at last sounder V (default 18)")] double minimumSounderVoltage = 18.0,
        [Description("Addressable loop max resistance Ω (default 120)")] double addressableLoopMaxResistanceOhm = 120.0,
        [Description("Fallback resistance Ω/m if no cable profile matches (default 0.035)")] double fallbackResistanceOhmPerMeter = 0.035,
        [Description("Max devices (default 5000)")] int limit = 5000,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["panelName"] = panelName ?? string.Empty,
            ["loopParameterName"] = loopParameterName,
            ["deviceTypeParameterName"] = deviceTypeParameterName,
            ["sounderCurrentMilliAmps"] = sounderCurrentMilliAmps,
            ["sounderSupplyVoltage"] = sounderSupplyVoltage,
            ["minimumSounderVoltage"] = minimumSounderVoltage,
            ["addressableLoopMaxResistanceOhm"] = addressableLoopMaxResistanceOhm,
            ["fallbackResistanceOhmPerMeter"] = fallbackResistanceOhmPerMeter,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_get_fire_alarm_voltage_drop_summary", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_list_cable_resistance_profiles", ReadOnly = true),
     Description("Lists all configured cable resistance profiles from %AppData%\\RKTools\\RevitMCP\\electrical-cable-profiles.json. Returns profile name, description, and resistance Ω/m. Default profiles are created on first use.")]
    public async Task<string> ListCableResistanceProfiles(CancellationToken cancellationToken = default)
    {
        var result = await pipeClient.SendAsync("revit_list_cable_resistance_profiles", [], cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_get_matching_cable_resistance_profile", ReadOnly = true),
     Description("Returns the cable resistance profile that matches the given cable type name (case-insensitive Contains match), or indicates no match. Accepts: cableTypeName (required).")]
    public async Task<string> GetMatchingCableResistanceProfile(
        [Description("Cable type name to look up")] string cableTypeName,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["cableTypeName"] = cableTypeName };
        var result = await pipeClient.SendAsync("revit_get_matching_cable_resistance_profile", args, cancellationToken);
        return FormatResult(result);
    }
}
