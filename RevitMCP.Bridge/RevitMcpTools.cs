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
        var result = await pipeClient.SendAsync("revit_get_connection_status", [], "Claude Code", cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_get_selected_elements", ReadOnly = true),
     Description("Returns the currently selected elements from the active Revit document with category, family, type, level, location, and bounding box.")]
    public async Task<string> GetSelectedElements(CancellationToken cancellationToken)
    {
        var result = await pipeClient.SendAsync("revit_get_selected_elements", [], "Claude Code", cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_list_views", ReadOnly = true),
     Description("Lists all views in the active Revit document with type, template status, sheet placement, scale, and discipline.")]
    public async Task<string> ListViews(CancellationToken cancellationToken)
    {
        var result = await pipeClient.SendAsync("revit_list_views", [], "Claude Code", cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_list_sheets", ReadOnly = true),
     Description("Lists all sheets in the active Revit document with sheet number, name, and the views placed on each sheet.")]
    public async Task<string> ListSheets(CancellationToken cancellationToken)
    {
        var result = await pipeClient.SendAsync("revit_list_sheets", [], "Claude Code", cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_list_schedules", ReadOnly = true),
     Description("Lists all schedules in the active Revit document with name, category, and field names.")]
    public async Task<string> ListSchedules(CancellationToken cancellationToken)
    {
        var result = await pipeClient.SendAsync("revit_list_schedules", [], "Claude Code", cancellationToken);
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
        var result = await pipeClient.SendAsync("revit_get_element_parameters", args, "Claude Code", cancellationToken);
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
        var result = await pipeClient.SendAsync("revit_count_elements", args, "Claude Code", cancellationToken);
        return FormatResult(result);
    }

    private static string FormatResult(McpToolResult result)
    {
        if (result.Success && result.Data != null)
        {
            var json = JsonConvert.SerializeObject(result.Data, Formatting.Indented);
            if (result.Warnings.Count > 0)
                json += "\n\n// Warnings:\n" + string.Join("\n", result.Warnings.Select(w => $"// {w}"));
            return json;
        }

        var error = new { success = false, message = result.Message, errors = result.Errors };
        return JsonConvert.SerializeObject(error, Formatting.Indented);
    }
}
