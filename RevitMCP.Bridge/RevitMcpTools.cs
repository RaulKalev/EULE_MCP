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

    [McpServerTool(Name = "revit_inspect_selected_elements", ReadOnly = true),
     Description("Returns detailed inspection data for the selected Revit elements: structured bounding box (mm), location (mm), geometry summary (solid/mesh/curve counts, volume), and parameter values.")]
    public async Task<string> InspectSelectedElements(
        [Description("If true, include a preview of all element parameters. Default true.")] bool includeParameters = true,
        [Description("Subset of parameter names to return. Leave empty to include all. Case-insensitive.")] string[]? parameterNames = null,
        [Description("If true, include a geometry summary (solid/mesh/curve counts, estimated volume). Default true.")] bool includeGeometrySummary = true,
        [Description("Maximum number of elements to process. Default 50.")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["includeParameters"]    = includeParameters,
            ["parameterNames"]       = parameterNames ?? [],
            ["includeGeometrySummary"] = includeGeometrySummary,
            ["limit"]                = limit
        };
        var result = await pipeClient.SendAsync("revit_inspect_selected_elements", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_list_views", ReadOnly = true),
     Description("Lists views in the active Revit document. Supports viewTypes, includeTemplates, nameFilter, includePlacedStatus, returnParameters, and limit.")]
    public async Task<string> ListViews(
        [Description("Filter by Revit view type names, e.g. FloorPlan, CeilingPlan, Section, Elevation, ThreeD, DraftingView")]
        string[]? viewTypes = null,
        [Description("Include view templates. Default false.")]
        bool includeTemplates = false,
        [Description("Optional substring filter for view name.")]
        string? nameFilter = null,
        [Description("Include sheet placement status and sheet info. Default true.")]
        bool includePlacedStatus = true,
        [Description("Additional view parameter names to return. Partial matching is supported by the add-in.")]
        string[]? returnParameters = null,
        [Description("Maximum views to return. 0 means all.")]
        int limit = 0,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["viewTypes"] = viewTypes ?? [],
            ["includeTemplates"] = includeTemplates,
            ["nameFilter"] = nameFilter ?? string.Empty,
            ["includePlacedStatus"] = includePlacedStatus,
            ["returnParameters"] = returnParameters ?? [],
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_list_views", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_list_sheets", ReadOnly = true),
     Description("Lists sheets in the active Revit document. Supports nameFilter, numberFilter, returnParameters, includeViewports, and limit.")]
    public async Task<string> ListSheets(
        [Description("Optional substring filter for sheet name.")]
        string? nameFilter = null,
        [Description("Optional substring filter for sheet number.")]
        string? numberFilter = null,
        [Description("Sheet parameter names to return. Use [\"default\"] for the standard EULE/Revit sheet parameters.")]
        string[]? returnParameters = null,
        [Description("Include viewport details per sheet. Default false.")]
        bool includeViewports = false,
        [Description("Maximum sheets to return. 0 means all.")]
        int limit = 0,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["nameFilter"] = nameFilter ?? string.Empty,
            ["numberFilter"] = numberFilter ?? string.Empty,
            ["returnParameters"] = returnParameters ?? [],
            ["includeViewports"] = includeViewports,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_list_sheets", args, cancellationToken);
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

    /// <summary>
    /// Converts a value that may be a <see cref="System.Text.Json.JsonElement"/> (as received
    /// from the MCP SDK which uses System.Text.Json) into a Newtonsoft.Json JToken so the
    /// request serialises correctly when sent through the named-pipe.
    /// Without this, JsonElement structs are serialised with their .NET properties (ValueKind,
    /// etc.) instead of the actual JSON content they wrap.
    /// </summary>
    private static object? ToJToken(object? value)
    {
        if (value is null) return null;
        if (value is System.Text.Json.JsonElement je)
            return Newtonsoft.Json.Linq.JToken.Parse(je.GetRawText());
        if (value is Newtonsoft.Json.Linq.JToken jt)
            return jt;
        if (value is object[] arr)
        {
            var ja = new Newtonsoft.Json.Linq.JArray();
            foreach (var item in arr)
            {
                if (item is null) { ja.Add(Newtonsoft.Json.Linq.JValue.CreateNull()); continue; }
                var converted = ToJToken(item);
                ja.Add(converted is Newtonsoft.Json.Linq.JToken token ? token : Newtonsoft.Json.Linq.JToken.FromObject(item));
            }
            return ja;
        }
        return Newtonsoft.Json.Linq.JToken.FromObject(value);
    }

    // ── Issue Reports ─────────────────────────────────────────────────────────

    [McpServerTool(Name = "revit_export_issues_json"),
     Description("Exports an issue report (passed as JSON in reportJson) to a .json file. Writes a file to disk and requires approval in the Revit add-in. Returns filePath, totalIssues, runId.")]
    public async Task<string> ExportIssuesJson(
        [Description("The full IssueReportDto serialised as a JSON string.")] string reportJson,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["reportJson"] = reportJson };
        var result = await pipeClient.SendAsync("revit_export_issues_json", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_export_issues_excel"),
     Description("Exports an issue report (passed as JSON in reportJson) to a formatted Excel (.xlsx) file with Summary and Issues sheets. Writes a file to disk and requires approval in the Revit add-in. Returns filePath, totalIssues, runId.")]
    public async Task<string> ExportIssuesExcel(
        [Description("The full IssueReportDto serialised as a JSON string.")] string reportJson,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["reportJson"] = reportJson };
        var result = await pipeClient.SendAsync("revit_export_issues_excel", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_export_issues_markdown"),
     Description("Exports an issue report (passed as JSON in reportJson) to a Markdown (.md) file. Writes a file to disk and requires approval in the Revit add-in. Returns filePath, totalIssues, runId.")]
    public async Task<string> ExportIssuesMarkdown(
        [Description("The full IssueReportDto serialised as a JSON string.")] string reportJson,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["reportJson"] = reportJson };
        var result = await pipeClient.SendAsync("revit_export_issues_markdown", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_merge_issue_reports", ReadOnly = true),
     Description("Merges multiple issue reports into one consolidated report. Pass reportJsonArray (array of IssueReportDto JSON strings) and optional title. Returns mergedReport JSON, runId, and summary counts.")]
    public async Task<string> MergeIssueReports(
        [Description("Array of IssueReportDto JSON strings to merge.")] string[] reportJsonArray,
        [Description("Title for the merged report. Default: 'Merged Issue Report'.")] string title = "Merged Issue Report",
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["reportJsonArray"] = reportJsonArray,
            ["title"] = title
        };
        var result = await pipeClient.SendAsync("revit_merge_issue_reports", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_export_issues_html_dashboard"),
     Description("Exports an issue report as a standalone offline HTML dashboard with filtering, sorting and severity cards. Pass the IssueReportDto serialised as JSON. Returns filePath.")]
    public async Task<string> ExportIssuesHtmlDashboard(
        [Description("The full IssueReportDto serialised as a JSON string.")] string reportJson,
        [Description("Optional output file name (without path). Defaults to auto-generated name.")] string? fileName = null,
        [Description("If true, embeds the raw JSON data inside the HTML file for re-import. Default true.")] bool includeEmbeddedJson = true,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["reportJson"] = reportJson, ["fileName"] = fileName, ["includeEmbeddedJson"] = includeEmbeddedJson };
        var result = await pipeClient.SendAsync("revit_export_issues_html_dashboard", args, cancellationToken);
        return FormatResult(result);
    }

    // ── Standards ─────────────────────────────────────────────────────────────

    [McpServerTool(Name = "standards_list_sources", ReadOnly = true),
     Description("Lists all company standards sources configured in StandardsSources.json, with enabled/disabled status and file counts.")]
    public async Task<string> StandardsListSources(CancellationToken cancellationToken = default)
    {
        var result = await pipeClient.SendAsync("standards_list_sources", new Dictionary<string, object?>(), cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "standards_index_sources", ReadOnly = true),
     Description("Indexes company standards documents for search. Can target a specific source or all enabled sources. Use force=true to rebuild stale indexes.")]
    public async Task<string> StandardsIndexSources(
        [Description("Source ID to index. Leave null to index all enabled sources.")] string? sourceId = null,
        [Description("Force full re-index even if files are unchanged.")] bool force = false,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["sourceId"] = sourceId, ["force"] = force };
        var result = await pipeClient.SendAsync("standards_index_sources", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "standards_search", ReadOnly = true),
     Description("Searches indexed company standards documents. Returns relevant chunks with source info, heading and score. Best effort — run standards_index_sources first if results are stale.")]
    public async Task<string> StandardsSearch(
        [Description("The search query (natural language or keywords).")] string query,
        [Description("Maximum number of results to return (1-50). Default 10.")] int maxResults = 10,
        [Description("Limit search to a specific source ID.")] string? sourceId = null,
        [Description("Discipline hint to boost relevance (e.g. 'electrical', 'hvac').")] string? discipline = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["query"] = query, ["maxResults"] = maxResults, ["sourceId"] = sourceId, ["discipline"] = discipline };
        var result = await pipeClient.SendAsync("standards_search", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "standards_get_document_chunk", ReadOnly = true),
     Description("Returns a specific indexed standards document chunk by its chunk ID, with optional surrounding context chunks (contextBefore/contextAfter, 0-5). Use chunk IDs from standards_search results.")]
    public async Task<string> StandardsGetDocumentChunk(
        [Description("Chunk ID to retrieve (from standards_search results).")] string chunkId,
        [Description("Source ID to search within. Leave null to search all indexed sources.")] string? sourceId = null,
        [Description("Number of context chunks before the target (0-5). Default 1.")] int contextBefore = 1,
        [Description("Number of context chunks after the target (0-5). Default 1.")] int contextAfter = 1,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["chunkId"] = chunkId, ["sourceId"] = sourceId, ["contextBefore"] = contextBefore, ["contextAfter"] = contextAfter };
        var result = await pipeClient.SendAsync("standards_get_document_chunk", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "standards_validate_source_config", ReadOnly = true),
     Description("Validates the StandardsSources.json configuration. Reports missing paths, misconfigured sources, and creates an example config if none exists.")]
    public async Task<string> StandardsValidateSourceConfig(CancellationToken cancellationToken = default)
    {
        var result = await pipeClient.SendAsync("standards_validate_source_config", new Dictionary<string, object?>(), cancellationToken);
        return FormatResult(result);
    }

    // ── Skill Admin ───────────────────────────────────────────────────────────

    [McpServerTool(Name = "revit_compare_skill_override_to_master", ReadOnly = true),
     Description("Compares a project skill override against the current company master. Returns a diff of changed task settings, enabled/disabled tasks, new tasks in master, and version mismatches.")]
    public async Task<string> CompareSkillOverrideToMaster(
        [Description("The skill ID to compare (e.g. 'company.delivery.check').")] string skillId,
        [Description("The project ID whose override to load.")] string projectId,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["skillId"] = skillId, ["projectId"] = projectId };
        var result = await pipeClient.SendAsync("revit_compare_skill_override_to_master", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_propose_master_skill_update", ReadOnly = true),
     Description("Proposes a company master skill update based on a project override. Writes a proposal JSON to the local proposals folder only — NEVER modifies company master files.")]
    public async Task<string> ProposeSkillMasterUpdate(
        [Description("The skill ID to propose an update for.")] string skillId,
        [Description("The project ID whose override contains the proposed changes.")] string projectId,
        [Description("Optional notes describing the reason for the proposal.")] string? notes = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["skillId"] = skillId, ["projectId"] = projectId, ["notes"] = notes };
        var result = await pipeClient.SendAsync("revit_propose_master_skill_update", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_export_skill_override_diff_markdown", ReadOnly = true),
     Description("Exports a Markdown diff report comparing a project skill override to the current company master. Saves to the exports folder. Does not modify any skill files.")]
    public async Task<string> ExportSkillOverrideDiffMarkdown(
        [Description("The skill ID to diff (e.g. 'company.delivery.check').")] string skillId,
        [Description("The project ID whose override to compare.")] string projectId,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["skillId"] = skillId, ["projectId"] = projectId };
        var result = await pipeClient.SendAsync("revit_export_skill_override_diff_markdown", args, cancellationToken);
        return FormatResult(result);
    }

    // ── File System Tools ─────────────────────────────────────────────────────

    [McpServerTool(Name = "file_read_text", ReadOnly = true),
     Description("Reads a UTF-8 text file from a local path. Returns file content and metadata. Default max size 1 MB. Returns an error for missing files, oversized files, or paths outside allowed roots.")]
    public async Task<string> FileReadText(
        [Description("Absolute local path to the file to read.")] string filePath,
        [Description("Maximum file size in bytes to read. 0 uses the default (1 MB).")] int maxBytes = 0,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["filePath"] = filePath,
            ["maxBytes"] = maxBytes
        };
        var result = await pipeClient.SendAsync("file_read_text", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "file_write_text"),
     Description("Writes a UTF-8 text file to disk. Requires user approval. Will not overwrite an existing file unless overwrite=true. Creates parent directories when createDirectories=true. When overwrite=true, creates a timestamped backup by default (backupBeforeOverwrite=true).")]
    public async Task<string> FileWriteText(
        [Description("Absolute local path to write the file to.")] string filePath,
        [Description("Text content to write to the file.")] string content,
        [Description("If true, overwrite the file if it already exists. Default false.")] bool overwrite = false,
        [Description("If true, create missing parent directories automatically. Default true.")] bool createDirectories = true,
        [Description("If true and overwrite=true, create a timestamped backup before overwriting. Default true when overwrite=true.")] bool backupBeforeOverwrite = true,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["filePath"] = filePath,
            ["content"] = content,
            ["overwrite"] = overwrite,
            ["createDirectories"] = createDirectories,
            ["backupBeforeOverwrite"] = backupBeforeOverwrite
        };
        var result = await pipeClient.SendAsync("file_write_text", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "file_inspect", ReadOnly = true),
     Description("Inspects a file or folder: returns existence, type, size, timestamps, attributes and optional SHA-256 hash. Read-only — does not modify any files.")]
    public async Task<string> FileInspect(
        [Description("Absolute local path to the file or folder to inspect.")] string filePath,
        [Description("If true, compute and return the SHA-256 hash of the file. Skipped for files over 100 MB. Default false.")] bool includeHash = false,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["filePath"] = filePath,
            ["includeHash"] = includeHash
        };
        var result = await pipeClient.SendAsync("file_inspect", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "file_copy"),
     Description("Copies a file to a destination path. Requires user approval. Will not overwrite an existing destination unless overwrite=true.")]
    public async Task<string> FileCopy(
        [Description("Absolute local path of the source file.")] string sourcePath,
        [Description("Absolute local path for the copy destination.")] string destinationPath,
        [Description("If true, overwrite the destination if it already exists. Default false.")] bool overwrite = false,
        [Description("If true, create missing destination directories automatically. Default true.")] bool createDirectories = true,
        [Description("If true, preserve source file creation/modification timestamps on the copy. Default true.")] bool preserveTimestamps = true,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["sourcePath"] = sourcePath,
            ["destinationPath"] = destinationPath,
            ["overwrite"] = overwrite,
            ["createDirectories"] = createDirectories,
            ["preserveTimestamps"] = preserveTimestamps
        };
        var result = await pipeClient.SendAsync("file_copy", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "file_backup"),
     Description("Creates a timestamped backup copy of a file. Requires user approval. Backup name format: <stem>_<suffix>_<yyyy-MM-dd_HHmmss><ext>. Default suffix is 'backup'.")]
    public async Task<string> FileBackup(
        [Description("Absolute local path of the file to back up.")] string filePath,
        [Description("Directory to write the backup into. Defaults to the same directory as the source file.")] string backupDirectory = "",
        [Description("Suffix to include in the backup file name, e.g. 'pre-import'. Default 'backup'.")] string suffix = "backup",
        [Description("If true, preserve source file creation/modification timestamps on the backup. Default true.")] bool preserveTimestamps = true,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["filePath"] = filePath,
            ["backupDirectory"] = backupDirectory,
            ["suffix"] = suffix,
            ["preserveTimestamps"] = preserveTimestamps
        };
        var result = await pipeClient.SendAsync("file_backup", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "file_list_directory", ReadOnly = true),
     Description("Lists files and folders in a local directory. Supports glob-style searchPattern (e.g. *.xlsx), optional recursive traversal, and a maxResults cap.")]
    public async Task<string> FileListDirectory(
        [Description("Absolute local path to the directory to list.")] string folderPath,
        [Description("File search pattern, e.g. *.xlsx or *. Default '*'.")] string searchPattern = "*",
        [Description("If true, include all subdirectory contents recursively. Default false.")] bool recursive = false,
        [Description("Maximum number of entries to return. Default 500.")] int maxResults = 500,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["folderPath"] = folderPath,
            ["searchPattern"] = searchPattern,
            ["recursive"] = recursive,
            ["maxResults"] = maxResults
        };
        var result = await pipeClient.SendAsync("file_list_directory", args, cancellationToken);
        return FormatResult(result);
    }

    // ── Excel Tools ───────────────────────────────────────────────────────────

    [McpServerTool(Name = "excel_inspect_workbook", ReadOnly = true),
     Description("Reads Excel workbook metadata — sheet names, used ranges, detected headers — without modifying the file. Optionally returns preview rows.")]
    public async Task<string> ExcelInspectWorkbook(
        [Description("Absolute path to the .xlsx or .xlsm file.")] string filePath,
        [Description("If true, include preview data rows from each sheet. Default false.")] bool includePreviewRows = false,
        [Description("Number of preview data rows to return per sheet. Default 10.")] int previewRowCount = 10,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["filePath"] = filePath,
            ["includePreviewRows"] = includePreviewRows,
            ["previewRowCount"] = previewRowCount
        };
        var result = await pipeClient.SendAsync("excel_inspect_workbook", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "excel_read_range", ReadOnly = true),
     Description("Reads a specific cell range from an Excel worksheet. Returns cell values, optional formulas, and data types. Read-only.")]
    public async Task<string> ExcelReadRange(
        [Description("Absolute path to the .xlsx or .xlsm file.")] string filePath,
        [Description("Exact worksheet name.")] string worksheetName,
        [Description("Cell range address, e.g. A1:H20.")] string rangeAddress,
        [Description("If true, include formula strings in addition to computed values. Default false.")] bool includeFormulas = false,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["filePath"] = filePath,
            ["worksheetName"] = worksheetName,
            ["rangeAddress"] = rangeAddress,
            ["includeFormulas"] = includeFormulas
        };
        var result = await pipeClient.SendAsync("excel_read_range", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "excel_update_cells"),
     Description("Updates specific cells in an existing Excel file without changing workbook formatting. Requires user approval. Creates a timestamped backup by default. Set dryRun=true to preview changes without saving.")]
    public async Task<string> ExcelUpdateCells(
        [Description("Absolute path to the .xlsx file.")] string filePath,
        [Description("Exact worksheet name.")] string worksheetName,
        [Description("Array of cell updates: [{\"cell\": \"B12\", \"value\": \"New text\"}, ...]")] object[] updates,
        [Description("If true, create a timestamped backup copy before saving. Default true.")] bool backupBeforeSave = true,
        [Description("If true, preview changes without saving or creating a backup. Default false.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["filePath"] = filePath,
            ["worksheetName"] = worksheetName,
            ["updates"] = ToJToken(updates),
            ["backupBeforeSave"] = backupBeforeSave,
            ["dryRun"] = dryRun
        };
        var result = await pipeClient.SendAsync("excel_update_cells", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "excel_insert_rows"),
     Description("Inserts rows at a given row number in an Excel worksheet, copying styles from a template row. Row values are keyed by column letter (A, B, C…). Requires user approval. Set dryRun=true to preview without modifying.")]
    public async Task<string> ExcelInsertRows(
        [Description("Absolute path to the .xlsx file.")] string filePath,
        [Description("Exact worksheet name.")] string worksheetName,
        [Description("1-based row number to insert before.")] int insertAtRow,
        [Description("1-based row number to copy styles from. Defaults to the row above insertAtRow.")] int copyStyleFromRow = 0,
        [Description("Rows to insert as objects keyed by column letter: [{\"A\": \"val1\", \"B\": \"val2\"}, ...]")] object[]? rows = null,
        [Description("If true, create a timestamped backup before saving. Default true.")] bool backupBeforeSave = true,
        [Description("If true, preview the insert without saving or creating a backup. Default false.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["filePath"] = filePath,
            ["worksheetName"] = worksheetName,
            ["insertAtRow"] = insertAtRow,
            ["copyStyleFromRow"] = copyStyleFromRow > 0 ? copyStyleFromRow : (insertAtRow > 1 ? insertAtRow - 1 : 1),
            ["rows"] = ToJToken(rows ?? []),
            ["backupBeforeSave"] = backupBeforeSave,
            ["dryRun"] = dryRun
        };
        var result = await pipeClient.SendAsync("excel_insert_rows", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "excel_append_table_rows"),
     Description("Appends rows after the last data row in an Excel worksheet, matching values to columns by header name. Optionally targets a named Excel table. Requires user approval. Set dryRun=true to preview without modifying.")]
    public async Task<string> ExcelAppendTableRows(
        [Description("Absolute path to the .xlsx file.")] string filePath,
        [Description("Exact worksheet name.")] string worksheetName,
        [Description("Named Excel table to extend. Leave empty to auto-detect the header region.")] string tableName = "",
        [Description("If true, match row keys to column headers by name (case-insensitive). Default true.")] bool matchHeaders = true,
        [Description("Rows to append as objects keyed by header name: [{\"Dokumendi nr\": \"1626_EL\", \"Nimetus\": \"Plaan\"}, ...]")] object[]? rows = null,
        [Description("If true, create a timestamped backup before saving. Default true.")] bool backupBeforeSave = true,
        [Description("If true, preview the append without saving or creating a backup. Default false.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["filePath"] = filePath,
            ["worksheetName"] = worksheetName,
            ["tableName"] = tableName,
            ["matchHeaders"] = matchHeaders,
            ["rows"] = ToJToken(rows ?? []),
            ["backupBeforeSave"] = backupBeforeSave,
            ["dryRun"] = dryRun
        };
        var result = await pipeClient.SendAsync("excel_append_table_rows", args, cancellationToken);
        return FormatResult(result);
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

    [McpServerTool(Name = "revit_set_circuit_path_mode"),
     Description(
         "Sets the path mode of electrical circuits to 'All Devices'. Skips circuits with a user-defined " +
         "custom path. Scope: useSelection=true (circuits containing selected elements), circuitIds (explicit list), " +
         "or all circuits in the document when neither is provided. Requires approval.")]
    public async Task<string> SetCircuitPathMode(
        [Description("Circuit element IDs to target (optional — omit for all circuits)")] long[]? circuitIds = null,
        [Description("Use current Revit selection to determine target circuits")] bool useSelection = false,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["circuitIds"] = circuitIds ?? [],
            ["useSelection"] = useSelection
        };
        var result = await pipeClient.SendAsync("revit_set_circuit_path_mode", args, cancellationToken);
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

    // ── View / Sheet / Documentation — Phase 1 Discovery ────────────────────

    [McpServerTool(Name = "revit_list_titleblocks", ReadOnly = true),
     Description("Lists all title block family symbols loaded in the active document. Returns familySymbolId, familyName, typeName, isInUse. Use familySymbolId when creating sheets.")]
    public async Task<string> ListTitleBlocks(CancellationToken cancellationToken = default)
    {
        var result = await pipeClient.SendAsync("revit_list_titleblocks", [], cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_list_view_templates", ReadOnly = true),
     Description("Lists all view templates in the active document. Optional: viewType (string e.g. \"FloorPlan\"). Returns elementId, name, viewType, assignedViewCount.")]
    public async Task<string> ListViewTemplates(
        [Description("Filter by view type (e.g. FloorPlan, Section, Elevation)")] string? viewType = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["viewType"] = viewType ?? "" };
        var result = await pipeClient.SendAsync("revit_list_view_templates", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_list_revisions", ReadOnly = true),
     Description("Lists all revisions defined in the active document. Returns elementId, sequenceNumber, revisionDate, description, issuedBy, issuedTo, revisionNumber, visibility, numbering.")]
    public async Task<string> ListRevisions(CancellationToken cancellationToken = default)
    {
        var result = await pipeClient.SendAsync("revit_list_revisions", [], cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_get_sheet_viewports", ReadOnly = true),
     Description("Returns detailed viewport information for one or more sheets. Provide sheetIds (long[]) or sheetNumbers (string[]). Returns viewportId, viewId, viewName, viewType, sheetPosition, detailNumber per viewport.")]
    public async Task<string> GetSheetViewports(
        [Description("Element IDs of target sheets")] long[]? sheetIds = null,
        [Description("Sheet numbers of target sheets (e.g. [\"E-01\", \"E-02\"])")] string[]? sheetNumbers = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["sheetIds"] = sheetIds ?? [], ["sheetNumbers"] = sheetNumbers ?? [] };
        var result = await pipeClient.SendAsync("revit_get_sheet_viewports", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_find_unplaced_views", ReadOnly = true),
     Description("Finds views not placed on any sheet. Optional: viewTypes (string[]), nameFilter (string), includeTemplates (bool), limit (int).")]
    public async Task<string> FindUnplacedViews(
        [Description("Filter by view types (e.g. [\"FloorPlan\",\"Section\"])")] string[]? viewTypes = null,
        [Description("Filter by name substring")] string? nameFilter = null,
        [Description("Include view templates (default false)")] bool includeTemplates = false,
        [Description("Max results (0 = all)")] int limit = 0,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["viewTypes"] = viewTypes ?? [],
            ["nameFilter"] = nameFilter ?? "",
            ["includeTemplates"] = includeTemplates,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_find_unplaced_views", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_get_view_sheet_summary", ReadOnly = true),
     Description("Returns a high-level summary: total sheets/views, placed vs unplaced, template coverage, title block coverage.")]
    public async Task<string> GetViewSheetSummary(CancellationToken cancellationToken = default)
    {
        var result = await pipeClient.SendAsync("revit_get_view_sheet_summary", [], cancellationToken);
        return FormatResult(result);
    }

    // ── View / Sheet / Documentation — Phase 2 Preview ───────────────────────

    [McpServerTool(Name = "revit_preview_place_views_on_sheets", ReadOnly = true),
     Description("Previews which views would be placed on which sheets WITHOUT making changes. Required: viewIds. Optional: sheetIds, allSheets, matchMode (ExactName|Contains|Fuzzy|SheetNumberPrefix|SheetNumberSuffix|CustomParameter), fuzzyThreshold, customParamName, skipAlreadyPlaced.")]
    public async Task<string> PreviewPlaceViewsOnSheets(
        [Description("View element IDs to place")] long[] viewIds,
        [Description("Target sheet IDs (omit or use allSheets=true for all sheets)")] long[]? sheetIds = null,
        [Description("Match against all sheets in document")] bool allSheets = true,
        [Description("Match mode: ExactName|Contains|Fuzzy|SheetNumberPrefix|SheetNumberSuffix|CustomParameter")] string matchMode = "Contains",
        [Description("Fuzzy similarity threshold 0-1 (default 0.6)")] double fuzzyThreshold = 0.6,
        [Description("Parameter name for CustomParameter match mode")] string? customParamName = null,
        [Description("Skip views already placed on sheets (default true)")] bool skipAlreadyPlaced = true,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["viewIds"] = viewIds,
            ["sheetIds"] = sheetIds ?? [],
            ["allSheets"] = allSheets,
            ["matchMode"] = matchMode,
            ["fuzzyThreshold"] = fuzzyThreshold,
            ["customParamName"] = customParamName ?? "",
            ["skipAlreadyPlaced"] = skipAlreadyPlaced
        };
        var result = await pipeClient.SendAsync("revit_preview_place_views_on_sheets", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_preview_duplicate_sheets", ReadOnly = true),
     Description("Previews sheet duplication WITHOUT changes. Required: sourceSheetIds or sourceSheetNumbers. Optional: newNumberSuffix (default \"_COPY\"), newNameSuffix (default \" - Copy\"), keepTitleBlock, copyParameters.")]
    public async Task<string> PreviewDuplicateSheets(
        [Description("Source sheet element IDs")] long[]? sourceSheetIds = null,
        [Description("Source sheet numbers")] string[]? sourceSheetNumbers = null,
        [Description("Suffix appended to sheet number (default _COPY)")] string newNumberSuffix = "_COPY",
        [Description("Suffix appended to sheet name (default ' - Copy')")] string newNameSuffix = " - Copy",
        [Description("Keep same title block (default true)")] bool keepTitleBlock = true,
        [Description("Copy instance parameters (default true)")] bool copyParameters = true,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["sourceSheetIds"] = sourceSheetIds ?? [],
            ["sourceSheetNumbers"] = sourceSheetNumbers ?? [],
            ["newNumberSuffix"] = newNumberSuffix,
            ["newNameSuffix"] = newNameSuffix,
            ["keepTitleBlock"] = keepTitleBlock,
            ["copyParameters"] = copyParameters
        };
        var result = await pipeClient.SendAsync("revit_preview_duplicate_sheets", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_preview_create_sheets_from_table", ReadOnly = true),
     Description("Previews sheet creation from a table WITHOUT changes. Required: rows (array of {sheetNumber, sheetName, ...params}), titleBlockId (use revit_list_titleblocks). Returns valid, conflict, issues per row.")]
    public async Task<string> PreviewCreateSheetsFromTable(
        [Description("Array of row objects, each with sheetNumber, sheetName, and optional parameter key-values")] object[] rows,
        [Description("Title block family symbol element ID")] long titleBlockId,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["rows"] = ToJToken(rows), ["titleBlockId"] = titleBlockId };
        var result = await pipeClient.SendAsync("revit_preview_create_sheets_from_table", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_preview_duplicate_views", ReadOnly = true),
     Description("Previews view duplication WITHOUT changes. Required: viewIds. Optional: duplicateOption (Duplicate|DuplicateWithDetailing|AsDependent), nameSuffix, namePrefix.")]
    public async Task<string> PreviewDuplicateViews(
        [Description("View element IDs to duplicate")] long[] viewIds,
        [Description("Duplicate option: Duplicate|DuplicateWithDetailing|AsDependent")] string duplicateOption = "DuplicateWithDetailing",
        [Description("Suffix for new view name (default ' - Copy')")] string nameSuffix = " - Copy",
        [Description("Prefix for new view name")] string namePrefix = "",
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["viewIds"] = viewIds,
            ["duplicateOption"] = duplicateOption,
            ["nameSuffix"] = nameSuffix,
            ["namePrefix"] = namePrefix
        };
        var result = await pipeClient.SendAsync("revit_preview_duplicate_views", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_preview_rename_views", ReadOnly = true),
     Description("Previews view renames WITHOUT changes. Required: mode (FindReplace|PrefixSuffix|Template|RegexFindReplace). Mode params: find/replace, prefix/suffix, template with {Name}. Optional: viewIds, viewTypes, nameFilter.")]
    public async Task<string> PreviewRenameViews(
        [Description("Rename mode: FindReplace|PrefixSuffix|Template|RegexFindReplace")] string mode,
        [Description("View element IDs (or use viewTypes+nameFilter)")] long[]? viewIds = null,
        [Description("Filter by view types")] string[]? viewTypes = null,
        [Description("Filter by name substring")] string? nameFilter = null,
        [Description("Text to find (FindReplace/Regex modes)")] string? find = null,
        [Description("Replacement text")] string? replace = null,
        [Description("Prefix to add (PrefixSuffix mode)")] string? prefix = null,
        [Description("Suffix to add (PrefixSuffix mode)")] string? suffix = null,
        [Description("Template pattern using {Name} (Template mode)")] string? template = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["mode"] = mode, ["viewIds"] = viewIds ?? [], ["viewTypes"] = viewTypes ?? [],
            ["nameFilter"] = nameFilter ?? "", ["find"] = find ?? "", ["replace"] = replace ?? "",
            ["prefix"] = prefix ?? "", ["suffix"] = suffix ?? "", ["template"] = template ?? ""
        };
        var result = await pipeClient.SendAsync("revit_preview_rename_views", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_preview_rename_sheets", ReadOnly = true),
     Description("Previews sheet renames WITHOUT changes. Required: mode (FindReplace|PrefixSuffix|Template|RegexFindReplace), target (Name|Number|Both). Optional: sheetIds, nameFilter, numberFilter, plus mode params.")]
    public async Task<string> PreviewRenameSheets(
        [Description("Rename mode: FindReplace|PrefixSuffix|Template|RegexFindReplace")] string mode,
        [Description("Target field: Name|Number|Both (default Name)")] string target = "Name",
        [Description("Sheet element IDs (or use nameFilter/numberFilter)")] long[]? sheetIds = null,
        [Description("Filter sheets by name substring")] string? nameFilter = null,
        [Description("Filter sheets by number substring")] string? numberFilter = null,
        [Description("Text to find")] string? find = null,
        [Description("Replacement text")] string? replace = null,
        [Description("Prefix to add")] string? prefix = null,
        [Description("Suffix to add")] string? suffix = null,
        [Description("Template with {Name} or {Number}")] string? template = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["mode"] = mode, ["target"] = target, ["sheetIds"] = sheetIds ?? [],
            ["nameFilter"] = nameFilter ?? "", ["numberFilter"] = numberFilter ?? "",
            ["find"] = find ?? "", ["replace"] = replace ?? "",
            ["prefix"] = prefix ?? "", ["suffix"] = suffix ?? "", ["template"] = template ?? ""
        };
        var result = await pipeClient.SendAsync("revit_preview_rename_sheets", args, cancellationToken);
        return FormatResult(result);
    }

    // ── View / Sheet / Documentation — Phase 3 Write ─────────────────────────

    [McpServerTool(Name = "revit_place_views_on_sheets"),
     Description("Places views on matched sheets. Requires approval. Required: viewIds. " +
                 "Option A (direct): targetSheetId — places ALL views on one specific sheet, no matching needed. " +
                 "Option B (matching): same parameters as revit_preview_place_views_on_sheets. Run preview first.")]
    public async Task<string> PlaceViewsOnSheets(
        [Description("View element IDs to place")] long[] viewIds,
        [Description("Direct target sheet ID — bypasses matching; all views go to this sheet")] long? targetSheetId = null,
        [Description("Target sheet IDs for matching (Option B)")] long[]? sheetIds = null,
        [Description("Match against all sheets (Option B)")] bool allSheets = true,
        [Description("Match mode: ExactName|Contains|Fuzzy|SheetNumberPrefix|SheetNumberSuffix|CustomParameter")] string matchMode = "Contains",
        [Description("Fuzzy threshold 0-1")] double fuzzyThreshold = 0.6,
        [Description("Parameter name for CustomParameter mode")] string? customParamName = null,
        [Description("Skip views already on sheets (default true)")] bool skipAlreadyPlaced = true,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["viewIds"] = viewIds, ["targetSheetId"] = targetSheetId ?? 0L,
            ["sheetIds"] = sheetIds ?? [], ["allSheets"] = allSheets,
            ["matchMode"] = matchMode, ["fuzzyThreshold"] = fuzzyThreshold,
            ["customParamName"] = customParamName ?? "", ["skipAlreadyPlaced"] = skipAlreadyPlaced
        };
        var result = await pipeClient.SendAsync("revit_place_views_on_sheets", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_duplicate_sheets"),
     Description("Duplicates sheets (empty shell with same titleblock + copied parameters). Requires approval. Required: sourceSheetIds or sourceSheetNumbers. Run revit_preview_duplicate_sheets first.")]
    public async Task<string> DuplicateSheets(
        [Description("Source sheet element IDs")] long[]? sourceSheetIds = null,
        [Description("Source sheet numbers")] string[]? sourceSheetNumbers = null,
        [Description("Suffix for new sheet number (default _COPY)")] string newNumberSuffix = "_COPY",
        [Description("Suffix for new sheet name")] string newNameSuffix = " - Copy",
        [Description("Keep same title block")] bool keepTitleBlock = true,
        [Description("Copy instance parameters")] bool copyParameters = true,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["sourceSheetIds"] = sourceSheetIds ?? [], ["sourceSheetNumbers"] = sourceSheetNumbers ?? [],
            ["newNumberSuffix"] = newNumberSuffix, ["newNameSuffix"] = newNameSuffix,
            ["keepTitleBlock"] = keepTitleBlock, ["copyParameters"] = copyParameters
        };
        var result = await pipeClient.SendAsync("revit_duplicate_sheets", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_create_sheets_from_table"),
     Description("Creates multiple sheets from a table. Requires approval. Required: rows (array of {sheetNumber, sheetName, ...params}), titleBlockId. Run revit_preview_create_sheets_from_table first.")]
    public async Task<string> CreateSheetsFromTable(
        [Description("Row objects each with sheetNumber, sheetName, and optional parameter values")] object[] rows,
        [Description("Title block family symbol element ID (from revit_list_titleblocks)")] long titleBlockId,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["rows"] = ToJToken(rows), ["titleBlockId"] = titleBlockId };
        var result = await pipeClient.SendAsync("revit_create_sheets_from_table", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_duplicate_views"),
     Description("Duplicates views. Requires approval. Required: viewIds. Optional: duplicateOption, nameSuffix, namePrefix. Run revit_preview_duplicate_views first.")]
    public async Task<string> DuplicateViews(
        [Description("View element IDs to duplicate")] long[] viewIds,
        [Description("Duplicate option: Duplicate|DuplicateWithDetailing|AsDependent")] string duplicateOption = "DuplicateWithDetailing",
        [Description("Suffix for new view name")] string nameSuffix = " - Copy",
        [Description("Prefix for new view name")] string namePrefix = "",
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["viewIds"] = viewIds, ["duplicateOption"] = duplicateOption,
            ["nameSuffix"] = nameSuffix, ["namePrefix"] = namePrefix
        };
        var result = await pipeClient.SendAsync("revit_duplicate_views", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_apply_view_template"),
     Description("Applies a view template to one or more views. Requires approval. Required: viewTemplateId (from revit_list_view_templates), viewIds or (viewTypes + nameFilter).")]
    public async Task<string> ApplyViewTemplate(
        [Description("View template element ID")] long viewTemplateId,
        [Description("View element IDs")] long[]? viewIds = null,
        [Description("Filter by view types")] string[]? viewTypes = null,
        [Description("Filter by name substring")] string? nameFilter = null,
        [Description("Max views to update (default 500)")] int limit = 500,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["viewTemplateId"] = viewTemplateId, ["viewIds"] = viewIds ?? [],
            ["viewTypes"] = viewTypes ?? [], ["nameFilter"] = nameFilter ?? "", ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_apply_view_template", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_set_sheet_parameters_bulk"),
     Description("Sets parameters on multiple sheets in one transaction. Requires approval. Required: sheetIds or sheetNumbers, parameters (object with paramName:value). Optional: nameFilter.")]
    public async Task<string> SetSheetParametersBulk(
        [Description("Sheet element IDs")] long[]? sheetIds = null,
        [Description("Sheet numbers")] string[]? sheetNumbers = null,
        [Description("Filter by name substring")] string? nameFilter = null,
        [Description("Parameter name→value map (e.g. {\"Märkus\": \"Rev A\"})")] object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["sheetIds"] = sheetIds ?? [], ["sheetNumbers"] = sheetNumbers ?? [],
            ["nameFilter"] = nameFilter ?? "", ["parameters"] = ToJToken(parameters)
        };
        var result = await pipeClient.SendAsync("revit_set_sheet_parameters_bulk", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_set_view_parameters_bulk"),
     Description("Sets parameters on multiple views in one transaction. Requires approval. Required: viewIds or (viewTypes+nameFilter), parameters (object with paramName:value). Optional: includeTemplates, limit.")]
    public async Task<string> SetViewParametersBulk(
        [Description("View element IDs")] long[]? viewIds = null,
        [Description("Filter by view types")] string[]? viewTypes = null,
        [Description("Filter by name substring")] string? nameFilter = null,
        [Description("Include view templates (default false)")] bool includeTemplates = false,
        [Description("Max views to update (default 500)")] int limit = 500,
        [Description("Parameter name→value map")] object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["viewIds"] = viewIds ?? [], ["viewTypes"] = viewTypes ?? [],
            ["nameFilter"] = nameFilter ?? "", ["includeTemplates"] = includeTemplates,
            ["limit"] = limit, ["parameters"] = ToJToken(parameters)
        };
        var result = await pipeClient.SendAsync("revit_set_view_parameters_bulk", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_rename_views"),
     Description("Renames views. Requires approval. Required: mode (FindReplace|PrefixSuffix|Template|RegexFindReplace), viewIds or viewTypes+nameFilter. Run revit_preview_rename_views first.")]
    public async Task<string> RenameViews(
        [Description("Rename mode")] string mode,
        [Description("View element IDs")] long[]? viewIds = null,
        [Description("Filter by view types")] string[]? viewTypes = null,
        [Description("Filter by name substring")] string? nameFilter = null,
        [Description("Find text (FindReplace/Regex)")] string? find = null,
        [Description("Replace text")] string? replace = null,
        [Description("Prefix (PrefixSuffix)")] string? prefix = null,
        [Description("Suffix (PrefixSuffix)")] string? suffix = null,
        [Description("Template with {Name} (Template)")] string? template = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["mode"] = mode, ["viewIds"] = viewIds ?? [], ["viewTypes"] = viewTypes ?? [],
            ["nameFilter"] = nameFilter ?? "", ["find"] = find ?? "", ["replace"] = replace ?? "",
            ["prefix"] = prefix ?? "", ["suffix"] = suffix ?? "", ["template"] = template ?? ""
        };
        var result = await pipeClient.SendAsync("revit_rename_views", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_rename_sheets"),
     Description("Renames sheets. Requires approval. Required: mode, target (Name|Number|Both). Optional: sheetIds, nameFilter, numberFilter. Run revit_preview_rename_sheets first.")]
    public async Task<string> RenameSheets(
        [Description("Rename mode")] string mode,
        [Description("Target field: Name|Number|Both")] string target = "Name",
        [Description("Sheet element IDs")] long[]? sheetIds = null,
        [Description("Filter by name substring")] string? nameFilter = null,
        [Description("Filter by number substring")] string? numberFilter = null,
        [Description("Find text")] string? find = null,
        [Description("Replace text")] string? replace = null,
        [Description("Prefix")] string? prefix = null,
        [Description("Suffix")] string? suffix = null,
        [Description("Template")] string? template = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["mode"] = mode, ["target"] = target, ["sheetIds"] = sheetIds ?? [],
            ["nameFilter"] = nameFilter ?? "", ["numberFilter"] = numberFilter ?? "",
            ["find"] = find ?? "", ["replace"] = replace ?? "",
            ["prefix"] = prefix ?? "", ["suffix"] = suffix ?? "", ["template"] = template ?? ""
        };
        var result = await pipeClient.SendAsync("revit_rename_sheets", args, cancellationToken);
        return FormatResult(result);
    }

    // ── View / Sheet / Documentation — Phase 4 Destructive ───────────────────

    [McpServerTool(Name = "revit_preview_delete_views", ReadOnly = true),
     Description("Previews which views would be deleted WITHOUT changes. Required: viewIds or (viewTypes+nameFilter). Optional: skipPlacedOnSheets (bool, default true).")]
    public async Task<string> PreviewDeleteViews(
        [Description("View element IDs")] long[]? viewIds = null,
        [Description("Filter by view types")] string[]? viewTypes = null,
        [Description("Filter by name substring")] string? nameFilter = null,
        [Description("Skip views placed on sheets (default true)")] bool skipPlacedOnSheets = true,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["viewIds"] = viewIds ?? [], ["viewTypes"] = viewTypes ?? [],
            ["nameFilter"] = nameFilter ?? "", ["skipPlacedOnSheets"] = skipPlacedOnSheets
        };
        var result = await pipeClient.SendAsync("revit_preview_delete_views", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_delete_views"),
     Description("DESTRUCTIVE: Permanently deletes views. Always requires manual approval — cannot be bypassed by Direct Edit. Required: viewIds. Optional: skipPlacedOnSheets (default true). Run preview first.")]
    public async Task<string> DeleteViews(
        [Description("View element IDs to delete")] long[] viewIds,
        [Description("Skip views placed on sheets (default true)")] bool skipPlacedOnSheets = true,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["viewIds"] = viewIds, ["skipPlacedOnSheets"] = skipPlacedOnSheets
        };
        var result = await pipeClient.SendAsync("revit_delete_views", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_preview_delete_sheets", ReadOnly = true),
     Description("Previews which sheets would be deleted WITHOUT changes. Required: sheetIds, sheetNumbers, or nameFilter. Optional: skipSheetsWithViews (bool, default true).")]
    public async Task<string> PreviewDeleteSheets(
        [Description("Sheet element IDs")] long[]? sheetIds = null,
        [Description("Sheet numbers")] string[]? sheetNumbers = null,
        [Description("Filter by name substring")] string? nameFilter = null,
        [Description("Skip sheets with placed views (default true)")] bool skipSheetsWithViews = true,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["sheetIds"] = sheetIds ?? [], ["sheetNumbers"] = sheetNumbers ?? [],
            ["nameFilter"] = nameFilter ?? "", ["skipSheetsWithViews"] = skipSheetsWithViews
        };
        var result = await pipeClient.SendAsync("revit_preview_delete_sheets", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_delete_sheets"),
     Description("DESTRUCTIVE: Permanently deletes sheets. Always requires manual approval — cannot be bypassed by Direct Edit. Required: sheetIds or sheetNumbers. Optional: skipSheetsWithViews (default true). Run preview first.")]
    public async Task<string> DeleteSheets(
        [Description("Sheet element IDs to delete")] long[]? sheetIds = null,
        [Description("Sheet numbers to delete")] string[]? sheetNumbers = null,
        [Description("Skip sheets that have placed views (default true)")] bool skipSheetsWithViews = true,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["sheetIds"] = sheetIds ?? [], ["sheetNumbers"] = sheetNumbers ?? [],
            ["skipSheetsWithViews"] = skipSheetsWithViews
        };
        var result = await pipeClient.SendAsync("revit_delete_sheets", args, cancellationToken);
        return FormatResult(result);
    }

    // -----------------------------------------------------------------------
    // Revision Numbering Sequences
    // -----------------------------------------------------------------------

    [McpServerTool(Name = "revit_list_revision_numbering_sequences", ReadOnly = true),
     Description("Lists revision numbering sequences defined in the active document. Returns sequenceId, name, numberingType, prefix, suffix, minimumDigits. Projects without custom sequences return an empty list.")]
    public async Task<string> ListRevisionNumberingSequences(CancellationToken cancellationToken = default)
    {
        var result = await pipeClient.SendAsync("revit_list_revision_numbering_sequences", [], cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_get_sheet_revisions", ReadOnly = true),
     Description("Returns revisions visible/assigned on one or more sheets. Accepts sheetIds (long[]) or sheetNumbers (string[]). Returns per-sheet: sheetNumber, sheetName, revisionCount, revisions (revisionId, sequenceNumber, revisionNumber, revisionDate, description, issuedBy, issuedTo).")]
    public async Task<string> GetSheetRevisions(
        [Description("Element IDs of target sheets")] long[]? sheetIds = null,
        [Description("Sheet numbers of target sheets, e.g. [\"A-01\", \"S-02\"]")] string[]? sheetNumbers = null,
        [Description("Include full revision detail per sheet (default true)")] bool includeRevisionDetails = true,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["sheetIds"]               = sheetIds ?? [],
            ["sheetNumbers"]           = sheetNumbers ?? [],
            ["includeRevisionDetails"] = includeRevisionDetails
        };
        var result = await pipeClient.SendAsync("revit_get_sheet_revisions", args, cancellationToken);
        return FormatResult(result);
    }

    // -----------------------------------------------------------------------
    // PlaceViews / Sheet Manager Preset Tools
    // -----------------------------------------------------------------------

    [McpServerTool(Name = "revit_list_view_sheet_presets", ReadOnly = true),
     Description("Lists available PlaceViews / Sheet Manager preset JSON files from the RK Tools preset folder. Returns fileName, detectedType, sizeBytes, modifiedUtc. Optional: overrideFolderPath.")]
    public async Task<string> ListViewSheetPresets(
        [Description("Override the default preset folder path (optional)")] string? overrideFolderPath = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["overrideFolderPath"] = overrideFolderPath ?? string.Empty
        };
        var result = await pipeClient.SendAsync("revit_list_view_sheet_presets", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_get_view_sheet_preset", ReadOnly = true),
     Description("Reads and returns the contents of a named PlaceViews / Sheet Manager preset JSON file. Accepts: presetName (filename with or without .json). Returns: fileName, workflowType, parsedContent.")]
    public async Task<string> GetViewSheetPreset(
        [Description("Preset filename (with or without .json extension)")] string presetName = "",
        [Description("Override the default preset folder path (optional)")] string? overrideFolderPath = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["presetName"]         = presetName,
            ["overrideFolderPath"] = overrideFolderPath ?? string.Empty
        };
        var result = await pipeClient.SendAsync("revit_get_view_sheet_preset", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_validate_view_sheet_preset", ReadOnly = true),
     Description("Validates the structure of a PlaceViews / Sheet Manager preset JSON file. Returns: isValid, workflowType, errors[], suggestions[]. Does not modify the model.")]
    public async Task<string> ValidateViewSheetPreset(
        [Description("Preset filename (with or without .json extension)")] string presetName = "",
        [Description("Override the default preset folder path (optional)")] string? overrideFolderPath = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["presetName"]         = presetName,
            ["overrideFolderPath"] = overrideFolderPath ?? string.Empty
        };
        var result = await pipeClient.SendAsync("revit_validate_view_sheet_preset", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_run_view_sheet_workflow_preset", ReadOnly = true),
     Description("Plans a view/sheet workflow from a preset — returns a structured preview of what the workflow would do without modifying the model. Returns: workflowType, stepCount, steps[], notes[]. Execute steps with revit_duplicate_sheets, revit_place_views_on_sheets, etc.")]
    public async Task<string> RunViewSheetWorkflowPreset(
        [Description("Preset filename (with or without .json extension)")] string presetName = "",
        [Description("Override the default preset folder path (optional)")] string? overrideFolderPath = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["presetName"]         = presetName,
            ["overrideFolderPath"] = overrideFolderPath ?? string.Empty
        };
        var result = await pipeClient.SendAsync("revit_run_view_sheet_workflow_preset", args, cancellationToken);
        return FormatResult(result);
    }

    // ── Coordination / Clash Detection ──────────────────────────────────────────

    [McpServerTool(Name = "revit_list_clashable_categories", ReadOnly = true),
     Description("Lists all element categories available for clash detection in the active document and loaded links, with element counts. Use before running detect or candidate tools.")]
    public async Task<string> ListClashableCategories(
        [Description("Include linked models (default true)")] bool includeLinks = true,
        [Description("Include Generic Models category (default true)")] bool includeGenericModels = true,
        [Description("Include imported geometry (DWG/DXF) (default true)")] bool includeImportedGeometry = true,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["includeLinks"] = includeLinks,
            ["includeGenericModels"] = includeGenericModels,
            ["includeImportedGeometry"] = includeImportedGeometry
        };
        var result = await pipeClient.SendAsync("revit_list_clashable_categories", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_list_clashable_links", ReadOnly = true),
     Description("Lists all Revit link instances and imported geometry in the active document that can participate in clash detection, including load status.")]
    public async Task<string> ListClashableLinks(CancellationToken cancellationToken = default)
    {
        var result = await pipeClient.SendAsync("revit_list_clashable_links", [], cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_get_clash_candidates", ReadOnly = true),
     Description("Returns candidate element counts for a clash check WITHOUT running detection. Use to estimate scope before committing to a full run.")]
    public async Task<string> GetClashCandidates(
        [Description("Source element categories to check (e.g. 'Cable Trays', 'Conduits')")] string[] sourceCategories,
        [Description("Target element categories to check against (e.g. 'Ducts', 'Pipes')")] string[] targetCategories,
        [Description("Include linked models (default true)")] bool includeLinks = true,
        [Description("Include Generic Models (default true)")] bool includeGenericModels = true,
        [Description("Include imported geometry (default true)")] bool includeImportedGeometry = true,
        [Description("Filter by link name substrings (optional)")] string[]? linkNameFilters = null,
        [Description("Max candidates per set (0 = unlimited, default 5000)")] int limit = 5000,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["sourceCategories"] = sourceCategories, ["targetCategories"] = targetCategories,
            ["includeLinks"] = includeLinks, ["includeGenericModels"] = includeGenericModels,
            ["includeImportedGeometry"] = includeImportedGeometry,
            ["linkNameFilters"] = linkNameFilters ?? [], ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_get_clash_candidates", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_detect_hard_clashes", ReadOnly = true),
     Description("Detects hard (physical intersection) clashes between two sets of element categories. " +
                 "By default, only reports clashes confirmed by solid-geometry intersection (Confidence=High). " +
                 "Set allowBoundingBoxFallback=true to also return low-confidence bbox-only results when solids cannot be extracted. " +
                 "Saves results as last run by default.")]
    public async Task<string> DetectHardClashes(
        [Description("Source element categories")] string[] sourceCategories,
        [Description("Target element categories")] string[] targetCategories,
        [Description("Include linked models (default true)")] bool includeLinks = true,
        [Description("Include Generic Models (default true)")] bool includeGenericModels = true,
        [Description("Include imported geometry (default true)")] bool includeImportedGeometry = true,
        [Description("Filter by link name substrings")] string[]? linkNameFilters = null,
        [Description("Minimum intersection volume tolerance in mm³ (default 5)")] double toleranceMm = 5,
        [Description("Maximum clashes to return (default 1000)")] int limit = 1000,
        [Description("Stop testing after this many element pairs (default 100000)")] int maxPairs = 100000,
        [Description("Save as last run for navigation tools (default true)")] bool saveAsLastRun = true,
        [Description("Rule name label for results (default 'Ad-hoc Hard Clash')")] string ruleName = "Ad-hoc Hard Clash",
        [Description("Severity: Low | Medium | High | Critical (default Medium)")] string severity = "Medium",
        [Description("Allow low-confidence bounding-box fallback results when solids cannot be extracted or boolean intersection fails. Default false.")]
        bool allowBoundingBoxFallback = false,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["sourceCategories"] = sourceCategories, ["targetCategories"] = targetCategories,
            ["includeLinks"] = includeLinks, ["includeGenericModels"] = includeGenericModels,
            ["includeImportedGeometry"] = includeImportedGeometry,
            ["linkNameFilters"] = linkNameFilters ?? [], ["toleranceMm"] = toleranceMm,
            ["limit"] = limit, ["maxPairs"] = maxPairs, ["saveAsLastRun"] = saveAsLastRun,
            ["ruleName"] = ruleName, ["severity"] = severity,
            ["allowBoundingBoxFallback"] = allowBoundingBoxFallback
        };
        var result = await pipeClient.SendAsync("revit_detect_hard_clashes", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_detect_clearance_clashes", ReadOnly = true),
     Description("Detects clearance violations between two sets of element categories using expanded bounding-box approximation. Reported distances are conservative estimates, not true surface-to-surface measurements. Results should be visually reviewed in the Revit clash review view.")]
    public async Task<string> DetectClearanceClashes(
        [Description("Source element categories")] string[] sourceCategories,
        [Description("Target element categories")] string[] targetCategories,
        [Description("Required clearance in mm (default 50)")] double clearanceMm = 50,
        [Description("Include linked models (default true)")] bool includeLinks = true,
        [Description("Include Generic Models (default true)")] bool includeGenericModels = true,
        [Description("Include imported geometry (default true)")] bool includeImportedGeometry = true,
        [Description("Filter by link name substrings")] string[]? linkNameFilters = null,
        [Description("Maximum results (default 1000)")] int limit = 1000,
        [Description("Stop after this many element pairs (default 100000)")] int maxPairs = 100000,
        [Description("Save as last run (default true)")] bool saveAsLastRun = true,
        [Description("Rule name label")] string ruleName = "Ad-hoc Clearance",
        [Description("Severity: Low | Medium | High | Critical (default Medium)")] string severity = "Medium",
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["sourceCategories"] = sourceCategories, ["targetCategories"] = targetCategories,
            ["clearanceMm"] = clearanceMm, ["includeLinks"] = includeLinks,
            ["includeGenericModels"] = includeGenericModels, ["includeImportedGeometry"] = includeImportedGeometry,
            ["linkNameFilters"] = linkNameFilters ?? [], ["limit"] = limit,
            ["maxPairs"] = maxPairs, ["saveAsLastRun"] = saveAsLastRun,
            ["ruleName"] = ruleName, ["severity"] = severity
        };
        var result = await pipeClient.SendAsync("revit_detect_clearance_clashes", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_get_clash_summary", ReadOnly = true),
     Description("Returns a grouped summary of clash results from the last run or from provided JSON. Groups by Rule, Level, LinkedModel, CategoryPair, and/or Severity.")]
    public async Task<string> GetClashSummary(
        [Description("Use results from last detection run (default true)")] bool useLastRun = true,
        [Description("Raw ClashRunResultDto JSON string (used when useLastRun=false)")] string clashesJson = "",
        [Description("Group-by fields: Rule, Level, LinkedModel, CategoryPair, Severity")] string[]? groupBy = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["useLastRun"] = useLastRun, ["clashesJson"] = clashesJson,
            ["groupBy"] = groupBy ?? []
        };
        var result = await pipeClient.SendAsync("revit_get_clash_summary", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_list_clash_presets", ReadOnly = true),
     Description("Lists all available clash detection presets including names, descriptions, and rule counts.")]
    public async Task<string> ListClashPresets(CancellationToken cancellationToken = default)
    {
        var result = await pipeClient.SendAsync("revit_list_clash_presets", [], cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_get_clash_preset", ReadOnly = true),
     Description("Returns the full definition of a named clash detection preset including all rules and parameters.")]
    public async Task<string> GetClashPreset(
        [Description("Preset name (case-insensitive)")] string presetName,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["presetName"] = presetName };
        var result = await pipeClient.SendAsync("revit_get_clash_preset", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_validate_clash_preset", ReadOnly = true),
     Description("Validates a named clash detection preset and returns any validation errors.")]
    public async Task<string> ValidateClashPreset(
        [Description("Preset name to validate")] string presetName,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["presetName"] = presetName };
        var result = await pipeClient.SendAsync("revit_validate_clash_preset", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_run_clash_preset", ReadOnly = true),
     Description("Runs all rules in a named clash detection preset and returns merged results with per-rule clash counts. " +
                 "Hard clash rules use strict solid-intersection by default (Confidence=High). " +
                 "Set allowBoundingBoxFallback=true to also return low-confidence bbox-only results.")]
    public async Task<string> RunClashPreset(
        [Description("Preset name to run")] string presetName,
        [Description("Include linked models (default true)")] bool includeLinks = true,
        [Description("Include Generic Models (default true)")] bool includeGenericModels = true,
        [Description("Include imported geometry (default true)")] bool includeImportedGeometry = true,
        [Description("Max results per rule (default 1000)")] int limit = 1000,
        [Description("Max pairs per rule (default 100000)")] int maxPairs = 100000,
        [Description("Save merged result as last run (default true)")] bool saveAsLastRun = true,
        [Description("Allow low-confidence bounding-box fallback for hard clash rules when solids are unavailable. Default false.")]
        bool allowBoundingBoxFallback = false,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["presetName"] = presetName, ["includeLinks"] = includeLinks,
            ["includeGenericModels"] = includeGenericModels, ["includeImportedGeometry"] = includeImportedGeometry,
            ["limit"] = limit, ["maxPairs"] = maxPairs, ["saveAsLastRun"] = saveAsLastRun,
            ["allowBoundingBoxFallback"] = allowBoundingBoxFallback
        };
        var result = await pipeClient.SendAsync("revit_run_clash_preset", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_export_clash_report_to_excel", ReadOnly = true),
     Description("Exports clash detection results to an Excel workbook with summary, per-rule, per-level, per-linked-model, and per-category-pair sheets.")]
    public async Task<string> ExportClashReportToExcel(
        [Description("Use last detection run (default true)")] bool useLastRun = true,
        [Description("Raw ClashRunResultDto JSON (when useLastRun=false)")] string clashesJson = "",
        [Description("Output filename (default Clash_Report.xlsx)")] string fileName = "Clash_Report.xlsx",
        [Description("Include summary sheet (default true)")] bool includeSummary = true,
        [Description("Include 'By Rule' sheet (default true)")] bool includeByRule = true,
        [Description("Include 'By Level' sheet (default true)")] bool includeByLevel = true,
        [Description("Include 'By Linked Model' sheet (default true)")] bool includeByLinkedModel = true,
        [Description("Include 'By Category Pair' sheet (default true)")] bool includeByCategoryPair = true,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["useLastRun"] = useLastRun, ["clashesJson"] = clashesJson, ["fileName"] = fileName,
            ["includeSummary"] = includeSummary, ["includeByRule"] = includeByRule,
            ["includeByLevel"] = includeByLevel, ["includeByLinkedModel"] = includeByLinkedModel,
            ["includeByCategoryPair"] = includeByCategoryPair
        };
        var result = await pipeClient.SendAsync("revit_export_clash_report_to_excel", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_get_clash_dashboard_summary", ReadOnly = true),
     Description("Returns a rich dashboard summary of clash results grouped by multiple dimensions simultaneously for a quick project health overview.")]
    public async Task<string> GetClashDashboardSummary(
        [Description("Use last detection run (default true)")] bool useLastRun = true,
        [Description("Raw ClashRunResultDto JSON (when useLastRun=false)")] string clashesJson = "",
        [Description("Group-by fields: Rule, Level, LinkedModel, CategoryPair, Severity")] string[]? groupBy = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["useLastRun"] = useLastRun, ["clashesJson"] = clashesJson,
            ["groupBy"] = groupBy ?? []
        };
        var result = await pipeClient.SendAsync("revit_get_clash_dashboard_summary", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_get_next_clash", ReadOnly = true),
     Description("Navigates to the next clash in the last run result. Returns clash details and position. Wraps around from last to first.")]
    public async Task<string> GetNextClash(CancellationToken cancellationToken = default)
    {
        var result = await pipeClient.SendAsync("revit_get_next_clash", [], cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_get_previous_clash", ReadOnly = true),
     Description("Navigates to the previous clash in the last run result. Returns clash details and position. Wraps around from first to last.")]
    public async Task<string> GetPreviousClash(CancellationToken cancellationToken = default)
    {
        var result = await pipeClient.SendAsync("revit_get_previous_clash", [], cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_create_clash_review_view"),
     Description("Creates or reuses the 'MCP Clash Review' 3D view. Optionally scopes the section box to a specific clash by ClashId. Requires approval.")]
    public async Task<string> CreateClashReviewView(
        [Description("Clash ID to focus the section box on (optional)")] string clashId = "",
        [Description("Section box padding in mm (default 1000)")] double sectionBoxPaddingMm = 1000,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["clashId"] = clashId, ["sectionBoxPaddingMm"] = sectionBoxPaddingMm
        };
        var result = await pipeClient.SendAsync("revit_create_clash_review_view", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_focus_clash"),
     Description("Activates the MCP Clash Review view, scopes the section box to the specified clash, and selects the source and target elements. Requires approval.")]
    public async Task<string> FocusClash(
        [Description("Clash ID to focus on (required)")] string clashId,
        [Description("Section box padding in mm (default 1000)")] double sectionBoxPaddingMm = 1000,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["clashId"] = clashId, ["sectionBoxPaddingMm"] = sectionBoxPaddingMm
        };
        var result = await pipeClient.SendAsync("revit_focus_clash", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_select_clash_elements"),
     Description("Selects the source and target elements of a clash in the Revit UI. For linked-model targets, selects the RevitLinkInstance instead. Requires approval.")]
    public async Task<string> SelectClashElements(
        [Description("Clash ID (required)")] string clashId,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["clashId"] = clashId };
        var result = await pipeClient.SendAsync("revit_select_clash_elements", args, cancellationToken);
        return FormatResult(result);
    }

    // ── Family Creation ───────────────────────────────────────────────────────

    [McpServerTool(Name = "revit_create_panel_schematic_symbol_from_dwg"),
     Description(
         "Creates a Detail Item family (.rfa) from a local DWG file using a company preset. " +
         "The family is saved to the output folder and is NOT loaded into the active project. " +
         "Required: dwgPath (full path to .dwg), userDefinedName (used after the Kilp_ prefix). " +
         "Optional: presetName (default \"DefaultPanelSchematicSymbol\"), outputFolder (override). " +
         "If the target file already exists, a _01/_02 version suffix is applied. Requires approval.")]
    public async Task<string> CreatePanelSchematicSymbolFromDwg(
        [Description("Full local path to the source DWG file (must end with .dwg).")]
        string dwgPath,
        [Description("User-defined symbol name appended after the Kilp_ prefix, e.g. \"QF_3P\". Spaces and invalid filename chars are replaced with underscores.")]
        string userDefinedName,
        [Description("Preset name from DwgDetailItemPresets.json. Default: DefaultPanelSchematicSymbol.")]
        string presetName = "DefaultPanelSchematicSymbol",
        [Description("Override output folder. If empty, the preset's configured folder is used.")]
        string? outputFolder = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["dwgPath"]         = dwgPath,
            ["userDefinedName"] = userDefinedName,
            ["presetName"]      = presetName,
            ["outputFolder"]    = outputFolder ?? string.Empty
        };
        var result = await pipeClient.SendAsync(
            "revit_create_panel_schematic_symbol_from_dwg", args, cancellationToken);
        return FormatResult(result);
    }

    // ── Skills ────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "revit_list_skills", ReadOnly = true),
     Description("Lists all available company skills with their IDs, names, versions and task counts. " +
                 "Optional: projectId (used to flag whether a project override exists for each skill).")]
    public async Task<string> ListSkills(
        [Description("Optional project ID used to check for existing overrides")] string? projectId = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["projectId"] = projectId ?? string.Empty };
        var result = await pipeClient.SendAsync("revit_list_skills", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_get_skill_details", ReadOnly = true),
     Description("Returns the full definition for a specific skill. " +
                 "Args: skillId (required), projectId, includeProjectOverride (bool — merge the project override into the response).")]
    public async Task<string> GetSkillDetails(
        [Description("ID of the skill, e.g. company.electrical.qa")] string skillId,
        [Description("Optional project ID for override lookup")] string? projectId = null,
        [Description("Merge the project override into the response")] bool includeProjectOverride = false,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["skillId"] = skillId,
            ["projectId"] = projectId ?? string.Empty,
            ["includeProjectOverride"] = includeProjectOverride
        };
        var result = await pipeClient.SendAsync("revit_get_skill_details", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_preview_skill_run", ReadOnly = true),
     Description("Previews what a skill run will do: task list, which tasks change the model, and whether user confirmation is required. " +
                 "Call this before revit_run_skill to understand the impact. " +
                 "Args: skillId (required), projectId, useProjectOverride (bool).")]
    public async Task<string> PreviewSkillRun(
        [Description("ID of the skill to preview")] string skillId,
        [Description("Optional project ID for override lookup")] string? projectId = null,
        [Description("Apply the project override in the preview")] bool useProjectOverride = false,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["skillId"] = skillId,
            ["projectId"] = projectId ?? string.Empty,
            ["useProjectOverride"] = useProjectOverride
        };
        var result = await pipeClient.SendAsync("revit_preview_skill_run", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_run_skill"),
     Description("Runs all enabled tasks in a company skill. Some tasks may create Revit views — requires approval. " +
                 "Call revit_preview_skill_run first to understand the impact. " +
                 "Args: skillId (required), projectId, useProjectOverride (bool, default false).")]
    public async Task<string> RunSkill(
        [Description("ID of the skill to run, e.g. company.electrical.qa")] string skillId,
        [Description("Optional project ID for override lookup")] string? projectId = null,
        [Description("Apply the project override when running")] bool useProjectOverride = false,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["skillId"] = skillId,
            ["projectId"] = projectId ?? string.Empty,
            ["useProjectOverride"] = useProjectOverride
        };
        var result = await pipeClient.SendAsync("revit_run_skill", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_run_skill_task"),
     Description("Runs a single task within a skill. Useful for re-running or debugging one task. Requires approval. " +
                 "Args: skillId (required), taskId (required), projectId, useProjectOverride (bool).")]
    public async Task<string> RunSkillTask(
        [Description("ID of the skill containing the task")] string skillId,
        [Description("ID of the task to run, e.g. check.cabletray.vs.ducts")] string taskId,
        [Description("Optional project ID for override lookup")] string? projectId = null,
        [Description("Apply the project override when running")] bool useProjectOverride = false,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["skillId"] = skillId,
            ["taskId"] = taskId,
            ["projectId"] = projectId ?? string.Empty,
            ["useProjectOverride"] = useProjectOverride
        };
        var result = await pipeClient.SendAsync("revit_run_skill_task", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_create_project_skill_override", ReadOnly = true),
     Description("Creates a new project-specific override for a company skill. " +
                 "changesJson is a JSON string with structure: " +
                 "{\"tasks\":{\"<taskId>\":{\"enabled\":true,\"settings\":{\"clearanceMm\":100}}}}. " +
                 "Args: skillId (required), projectId (required), projectName, changesJson, note.")]
    public async Task<string> CreateProjectSkillOverride(
        [Description("ID of the skill to override")] string skillId,
        [Description("Project identifier (e.g. job number)")] string projectId,
        [Description("Human-readable project name")] string? projectName = null,
        [Description("JSON string of override data (tasks + settings)")] string? changesJson = null,
        [Description("Optional note describing the reason for the override")] string? note = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["skillId"]     = skillId,
            ["projectId"]   = projectId,
            ["projectName"] = projectName ?? string.Empty,
            ["changesJson"] = changesJson ?? "{}",
            ["note"]        = note ?? string.Empty
        };
        var result = await pipeClient.SendAsync("revit_create_project_skill_override", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_update_project_skill_override", ReadOnly = true),
     Description("Updates an existing project skill override by merging changesJson into it. " +
                 "changesJson uses the same structure as revit_create_project_skill_override. " +
                 "Args: skillId (required), projectId (required), changesJson, note.")]
    public async Task<string> UpdateProjectSkillOverride(
        [Description("ID of the skill")] string skillId,
        [Description("Project identifier")] string projectId,
        [Description("JSON string of override changes to merge in")] string? changesJson = null,
        [Description("Optional note to append to the override history")] string? note = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["skillId"]     = skillId,
            ["projectId"]   = projectId,
            ["changesJson"] = changesJson ?? "{}",
            ["note"]        = note ?? string.Empty
        };
        var result = await pipeClient.SendAsync("revit_update_project_skill_override", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_reset_project_skill_override", ReadOnly = true),
     Description("Deletes the project-specific skill override, reverting to the company master skill. " +
                 "Args: skillId (required), projectId (required).")]
    public async Task<string> ResetProjectSkillOverride(
        [Description("ID of the skill")] string skillId,
        [Description("Project identifier")] string projectId,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["skillId"] = skillId, ["projectId"] = projectId };
        var result = await pipeClient.SendAsync("revit_reset_project_skill_override", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_configure_sheet_naming_skill", ReadOnly = true),
     Description("Convenience tool to configure the sheet naming skill override for a project. " +
                 "Creates or updates the project skill override for 'company.lehed.nimetamise-kontroll', enabling Excel comparison and report export tasks. " +
                 "Args: projectId (required), projectName, excelFilePath, enableExcelComparison, enableExcelReport, enableJsonReport, allowedDisciplines, allowedStages.")]
    public async Task<string> ConfigureSheetNamingSkill(
        [Description("Project identifier (required)")] string projectId,
        [Description("Human-readable project name")] string? projectName = null,
        [Description("Path to the Excel document register used by the sheet naming skill")] string? excelFilePath = null,
        [Description("Enable Excel register comparison task")] bool enableExcelComparison = false,
        [Description("Enable Excel report export task")] bool enableExcelReport = false,
        [Description("Enable JSON report export task")] bool enableJsonReport = false,
        [Description("Allowed discipline codes, e.g. EL, EN, EA")] string[]? allowedDisciplines = null,
        [Description("Allowed stage codes, e.g. EP, TP, PP")] string[]? allowedStages = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["projectId"] = projectId,
            ["projectName"] = projectName ?? projectId,
            ["excelFilePath"] = excelFilePath ?? string.Empty,
            ["enableExcelComparison"] = enableExcelComparison,
            ["enableExcelReport"] = enableExcelReport,
            ["enableJsonReport"] = enableJsonReport,
            ["allowedDisciplines"] = allowedDisciplines ?? [],
            ["allowedStages"] = allowedStages ?? []
        };
        var result = await pipeClient.SendAsync("revit_configure_sheet_naming_skill", args, cancellationToken);
        return FormatResult(result);
    }

    // ── Delivery Tools ────────────────────────────────────────────────────────

    [McpServerTool(Name = "delivery_scan_folder", ReadOnly = true),
     Description("Scans a delivery folder and returns a structured file inventory with parsed EULE drawing file names. " +
                 "Args: folderPath (required), recursive (bool, default true), includeExtensions (string[], default [pdf,dwg,ifc,xlsx]), maxResults (default 5000). " +
                 "Optional policy checks: checkTempFiles, checkOldRevisions, checkSuspiciousExtensions, checkRequiredFolders, " +
                 "requiredFolders, allowedExtraExtensions, requiredProjectFileExtensions, ignoredPatterns. " +
                 "Pass includeExtensions=[\"*\"] to scan all file types.")]
    public async Task<string> DeliveryScanFolder(
        [Description("Path to the delivery folder to scan")] string folderPath,
        [Description("Recurse into subdirectories")] bool recursive = true,
        [Description("File extensions to include, e.g. pdf, dwg, ifc. Defaults to [pdf,dwg,ifc,xlsx]. Pass [\"*\"] to include all files.")] string[]? includeExtensions = null,
        [Description("Maximum file results to return")] int maxResults = 5000,
        [Description("Check for temp/lock/backup files (e.g. ~$*, *.bak)")] bool checkTempFiles = false,
        [Description("Check for multiple revisions of the same sheet")] bool checkOldRevisions = false,
        [Description("Check for files with suspicious (unexpected) extensions")] bool checkSuspiciousExtensions = false,
        [Description("Check that required sub-folders exist in the delivery folder")] bool checkRequiredFolders = false,
        [Description("Sub-folder names that must exist when checkRequiredFolders=true")] string[]? requiredFolders = null,
        [Description("Extra file extensions allowed beyond requiredExtensions (for suspicious-extension check)")] string[]? allowedExtraExtensions = null,
        [Description("At least one file with each of these extensions must be present (e.g. ifc, nwc)")] string[]? requiredProjectFileExtensions = null,
        [Description("File name patterns to ignore during policy checks (e.g. thumbs.db)")] string[]? ignoredPatterns = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["folderPath"] = folderPath,
            ["recursive"] = recursive,
            ["includeExtensions"] = includeExtensions ?? ["pdf", "dwg", "ifc", "xlsx"],
            ["maxResults"] = maxResults,
            ["checkTempFiles"] = checkTempFiles,
            ["checkOldRevisions"] = checkOldRevisions,
            ["checkSuspiciousExtensions"] = checkSuspiciousExtensions,
            ["checkRequiredFolders"] = checkRequiredFolders,
            ["requiredFolders"] = requiredFolders ?? [],
            ["allowedExtraExtensions"] = allowedExtraExtensions ?? [],
            ["requiredProjectFileExtensions"] = requiredProjectFileExtensions ?? [],
            ["ignoredPatterns"] = ignoredPatterns ?? []
        };
        var result = await pipeClient.SendAsync("delivery_scan_folder", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "delivery_check_against_revit_sheets", ReadOnly = true),
     Description("Compares exported files in a delivery folder against Revit sheets. " +
                 "Returns an IssueReportDto with missing files, orphan files, stage/discipline mismatches, and duplicate exports. " +
                 "Args: folderPath (required), requiredExtensions (default [pdf,dwg]), stageFilter, disciplineFilter, sheetNumberFilter, recursive.")]
    public async Task<string> DeliveryCheckAgainstRevitSheets(
        [Description("Path to the delivery folder")] string folderPath,
        [Description("Extensions that must exist per sheet, e.g. pdf, dwg")] string[]? requiredExtensions = null,
        [Description("Filter by stage codes, e.g. TP, EP")] string[]? stageFilter = null,
        [Description("Filter by discipline codes, e.g. EL, EN")] string[]? disciplineFilter = null,
        [Description("Optional substring filter for sheet numbers")] string? sheetNumberFilter = null,
        [Description("Recurse into subdirectories")] bool recursive = true,
        [Description("Maximum file results to return")] int maxResults = 5000,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["folderPath"] = folderPath,
            ["requiredExtensions"] = requiredExtensions ?? [],
            ["stageFilter"] = stageFilter ?? [],
            ["disciplineFilter"] = disciplineFilter ?? [],
            ["sheetNumberFilter"] = sheetNumberFilter ?? string.Empty,
            ["recursive"] = recursive,
            ["maxResults"] = maxResults
        };
        var result = await pipeClient.SendAsync("delivery_check_against_revit_sheets", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "delivery_check_against_excel_register", ReadOnly = true),
     Description("Compares files in a delivery folder against an Excel document register. " +
                 "Returns issues for missing files, missing register rows, and duplicate document numbers. " +
                 "Args: folderPath (required), excelFilePath (required), worksheetName, requiredExtensions, recursive.")]
    public async Task<string> DeliveryCheckAgainstExcelRegister(
        [Description("Path to the delivery folder")] string folderPath,
        [Description("Path to the Excel document register (.xlsx or .xlsm)")] string excelFilePath,
        [Description("Worksheet name; leave empty to use the first visible sheet")] string? worksheetName = null,
        [Description("Extensions to check per register row, e.g. pdf, dwg")] string[]? requiredExtensions = null,
        [Description("Recurse into subdirectories")] bool recursive = true,
        [Description("Maximum file results to return")] int maxResults = 5000,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["folderPath"] = folderPath,
            ["excelFilePath"] = excelFilePath,
            ["worksheetName"] = worksheetName ?? string.Empty,
            ["requiredExtensions"] = requiredExtensions ?? [],
            ["recursive"] = recursive,
            ["maxResults"] = maxResults
        };
        var result = await pipeClient.SendAsync("delivery_check_against_excel_register", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "delivery_run_full_check", ReadOnly = true),
     Description("Runs the full delivery QA workflow: folder scan, Revit sheet comparison, optional Excel register comparison, and optional report export. " +
                 "Args: folderPath (required), excelFilePath (optional), requiredExtensions, stageFilter, disciplineFilter, " +
                 "exportExcelReport (bool), exportMarkdownReport (bool). " +
                 "Optional policy checks: checkTempFiles, checkOldRevisions, checkSuspiciousExtensions, checkRequiredFolders, " +
                 "requiredFolders, allowedExtraExtensions, requiredProjectFileExtensions, ignoredPatterns.")]
    public async Task<string> DeliveryRunFullCheck(
        [Description("Path to the delivery folder")] string folderPath,
        [Description("Path to the Excel document register (optional)")] string? excelFilePath = null,
        [Description("Worksheet name in the Excel register")] string? worksheetName = null,
        [Description("Extensions that must exist per sheet, e.g. pdf, dwg")] string[]? requiredExtensions = null,
        [Description("Filter by stage codes")] string[]? stageFilter = null,
        [Description("Filter by discipline codes")] string[]? disciplineFilter = null,
        [Description("Export an Excel issue report to the delivery folder")] bool exportExcelReport = false,
        [Description("Export a Markdown issue report to the delivery folder")] bool exportMarkdownReport = false,
        [Description("Recurse into subdirectories")] bool recursive = true,
        [Description("Maximum file results to return")] int maxResults = 5000,
        [Description("Check for temp/lock/backup files (e.g. ~$*, *.bak)")] bool checkTempFiles = false,
        [Description("Check for multiple revisions of the same sheet")] bool checkOldRevisions = false,
        [Description("Check for files with suspicious (unexpected) extensions")] bool checkSuspiciousExtensions = false,
        [Description("Check that required sub-folders exist in the delivery folder")] bool checkRequiredFolders = false,
        [Description("Sub-folder names that must exist when checkRequiredFolders=true")] string[]? requiredFolders = null,
        [Description("Extra file extensions allowed beyond requiredExtensions (for suspicious-extension check)")] string[]? allowedExtraExtensions = null,
        [Description("At least one file with each of these extensions must be present (e.g. ifc, nwc)")] string[]? requiredProjectFileExtensions = null,
        [Description("File name patterns to ignore during policy checks (e.g. thumbs.db)")] string[]? ignoredPatterns = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["folderPath"] = folderPath,
            ["excelFilePath"] = excelFilePath ?? string.Empty,
            ["worksheetName"] = worksheetName ?? string.Empty,
            ["requiredExtensions"] = requiredExtensions ?? [],
            ["stageFilter"] = stageFilter ?? [],
            ["disciplineFilter"] = disciplineFilter ?? [],
            ["exportExcelReport"] = exportExcelReport,
            ["exportMarkdownReport"] = exportMarkdownReport,
            ["recursive"] = recursive,
            ["maxResults"] = maxResults,
            ["checkTempFiles"] = checkTempFiles,
            ["checkOldRevisions"] = checkOldRevisions,
            ["checkSuspiciousExtensions"] = checkSuspiciousExtensions,
            ["checkRequiredFolders"] = checkRequiredFolders,
            ["requiredFolders"] = requiredFolders ?? [],
            ["allowedExtraExtensions"] = allowedExtraExtensions ?? [],
            ["requiredProjectFileExtensions"] = requiredProjectFileExtensions ?? [],
            ["ignoredPatterns"] = ignoredPatterns ?? []
        };
        var result = await pipeClient.SendAsync("delivery_run_full_check", args, cancellationToken);
        return FormatResult(result);
    }

    // ── Parameter QA Rule Sets ─────────────────────────────────────────────────

    [McpServerTool(Name = "revit_list_parameter_qa_rule_sets", ReadOnly = true),
     Description("Lists all available parameter QA rule sets. Each rule set defines which parameters must be filled for specific Revit categories. " +
                 "No args required.")]
    public async Task<string> ListParameterQaRuleSets(
        CancellationToken cancellationToken = default)
    {
        var result = await pipeClient.SendAsync("revit_list_parameter_qa_rule_sets", new Dictionary<string, object?>(), cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_run_parameter_qa_rule_set", ReadOnly = true),
     Description("Runs a named parameter QA rule set against the active model. Checks that required parameters are filled for elements in each rule's category. " +
                 "Use revit_list_parameter_qa_rule_sets to discover available rule sets. " +
                 "Args: ruleSetName (required), limitPerRule, returnIssueReport.")]
    public async Task<string> RunParameterQaRuleSet(
        [Description("Name of the rule set to run (see revit_list_parameter_qa_rule_sets)")] string ruleSetName,
        [Description("Maximum elements to check per rule")] int limitPerRule = 5000,
        [Description("Include a full IssueReportDto in the response")] bool returnIssueReport = true,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["ruleSetName"]      = ruleSetName,
            ["limitPerRule"]     = limitPerRule,
            ["returnIssueReport"] = returnIssueReport
        };
        var result = await pipeClient.SendAsync("revit_run_parameter_qa_rule_set", args, cancellationToken);
        return FormatResult(result);
    }

    // ── Configuration / State Tools ───────────────────────────────────────────

    [McpServerTool(Name = "config_read", ReadOnly = true),
     Description("Reads a JSON configuration file for a given scope (company, user, project, tool-state). Read-only. Returns the config as a JSON object.")]
    public async Task<string> ConfigRead(
        [Description("Configuration scope: company | user | project | tool-state")] string scope,
        [Description("Project root directory — required when scope=project.")] string projectRoot = "",
        [Description("If true and the file does not exist, create it with an empty object {}. Default false.")] bool createIfMissing = false,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["scope"] = scope,
            ["projectRoot"] = projectRoot,
            ["createIfMissing"] = createIfMissing
        };
        var result = await pipeClient.SendAsync("config_read", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "config_write"),
     Description("Replaces the entire content of a JSON config file for a given scope. Requires user approval. Creates a timestamped backup by default.")]
    public async Task<string> ConfigWrite(
        [Description("Configuration scope: company | user | project | tool-state")] string scope,
        [Description("Complete JSON object to write (replaces all existing content).")] string jsonContent,
        [Description("Project root directory — required when scope=project.")] string projectRoot = "",
        [Description("If true, create a timestamped backup before overwriting. Default true.")] bool backupBeforeOverwrite = true,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["scope"] = scope,
            ["jsonContent"] = jsonContent,
            ["projectRoot"] = projectRoot,
            ["backupBeforeOverwrite"] = backupBeforeOverwrite
        };
        var result = await pipeClient.SendAsync("config_write", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "config_update"),
     Description("Updates specific properties in a JSON config file using dot-path keys (e.g. '$.excel.defaultBackupBeforeSave'). Requires user approval. Missing intermediate objects are created automatically.")]
    public async Task<string> ConfigUpdate(
        [Description("Configuration scope: company | user | project | tool-state")] string scope,
        [Description("Object mapping dot-path keys to new values, e.g. {\"$.excel.defaultBackupBeforeSave\": \"true\"}")] object updates,
        [Description("Project root directory — required when scope=project.")] string projectRoot = "",
        [Description("If true, create a timestamped backup before saving. Default true.")] bool backupBeforeOverwrite = true,
        [Description("If true, create the file if it does not yet exist. Default false.")] bool createIfMissing = false,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["scope"] = scope,
            ["updates"] = ToJToken(updates),
            ["projectRoot"] = projectRoot,
            ["backupBeforeOverwrite"] = backupBeforeOverwrite,
            ["createIfMissing"] = createIfMissing
        };
        var result = await pipeClient.SendAsync("config_update", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "config_get_project_config", ReadOnly = true),
     Description("Reads the project-scoped MCP config file (.rktools/mcp.project.config.json) inside the specified project root. Read-only.")]
    public async Task<string> ConfigGetProjectConfig(
        [Description("Project root directory (folder that contains the .rktools subfolder).")] string projectRoot,
        [Description("If true and the file does not exist, create it with an empty object {}. Default false.")] bool createIfMissing = false,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["projectRoot"] = projectRoot,
            ["createIfMissing"] = createIfMissing
        };
        var result = await pipeClient.SendAsync("config_get_project_config", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "config_set_project_config"),
     Description("Writes or replaces the project-scoped MCP config file (.rktools/mcp.project.config.json). Requires user approval. Creates a timestamped backup by default.")]
    public async Task<string> ConfigSetProjectConfig(
        [Description("Project root directory (folder that contains or will contain the .rktools subfolder).")] string projectRoot,
        [Description("Complete JSON object to write (replaces all existing content).")] string jsonContent,
        [Description("If true, create a timestamped backup before overwriting. Default true.")] bool backupBeforeOverwrite = true,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["projectRoot"] = projectRoot,
            ["jsonContent"] = jsonContent,
            ["backupBeforeOverwrite"] = backupBeforeOverwrite
        };
        var result = await pipeClient.SendAsync("config_set_project_config", args, cancellationToken);
        return FormatResult(result);
    }
}

