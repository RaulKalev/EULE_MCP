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

    [McpServerTool(Name = "revit_find_elements_by_parameter", ReadOnly = true),
     Description("Finds model elements matching one or more parameter filters. Each filter specifies: parameterName (partial match), operator (equals/contains/startsWith/isEmpty/greaterThan/lessThan/notEquals/notContains/endsWith/isNotEmpty), value, matchMode (Contains/ContainsNormalized/Exact/ExactNormalized), scope (InstanceAndType/Instance/Type). Also accepts category, useSelection, elementIds, returnParameters, includeInstanceParameters, includeTypeParameters, limit.")]
    public async Task<string> FindElementsByParameter(
        [Description("JSON array of filter objects: [{parameterName, operator, value, matchMode, scope}]")] string? filters = null,
        [Description("Optional category name to restrict search (e.g. 'Fire Alarm Devices')")] string? category = null,
        [Description("Optional list of parameter names to include in returned elements")] string[]? returnParameters = null,
        [Description("Max elements to return (default 500)")] int limit = 500,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["category"] = category ?? string.Empty,
            ["filters"] = filters != null ? Newtonsoft.Json.JsonConvert.DeserializeObject(filters) : new object[] { },
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
        var args = new Dictionary<string, object?>
        {
            ["useSelection"] = useSelection,
            ["elementIds"] = elementIds ?? [],
            ["category"] = category ?? string.Empty,
            ["filters"] = filters != null ? Newtonsoft.Json.JsonConvert.DeserializeObject(filters) : new object[] { },
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
        var args = new Dictionary<string, object?>
        {
            ["groupBy"] = Newtonsoft.Json.JsonConvert.DeserializeObject(groupBy) ?? new object[] { },
            ["category"] = category ?? string.Empty,
            ["filters"] = filters != null ? Newtonsoft.Json.JsonConvert.DeserializeObject(filters) : new object[] { },
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
        var args = new Dictionary<string, object?>
        {
            ["category"] = category ?? string.Empty,
            ["filters"] = filters != null ? Newtonsoft.Json.JsonConvert.DeserializeObject(filters) : new object[] { },
            ["groupBy"] = groupBy != null ? Newtonsoft.Json.JsonConvert.DeserializeObject(groupBy) : new object[] { },
            ["parameters"] = parameters ?? [],
            ["outputMode"] = outputMode,
            ["fileName"] = fileName,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_export_query_to_excel", args, cancellationToken);
        return FormatResult(result);
    }

    private static string FormatResult(McpToolResult result)
    {
        var response = new
        {
            success = result.Success,
            message = result.Message,
            durationMs = result.DurationMs,
            data = result.Data,
            warnings = result.Warnings,
            errors = result.Errors
        };
        return JsonConvert.SerializeObject(response, Formatting.Indented);
    }
}
