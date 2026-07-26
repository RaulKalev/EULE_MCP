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

    [McpServerTool(Name = "revit_list_instances", ReadOnly = true),
     Description("Lists all running Revit instances that host a RevitMCP connector (useful when several Revit projects, e.g. 2024 and 2026, are open at the same time). Shows process id, Revit version, document title, and which instance requests are currently routed to.")]
    public Task<string> ListInstances(CancellationToken cancellationToken)
    {
        var activePid = pipeClient.GetActiveProcessId();
        var instances = pipeClient.DiscoverLiveInstances();
        var response = new
        {
            success = true,
            activeProcessId = activePid,
            routingOrder = "user-selected active instance first, then highest Revit version, then most recently started",
            instances = instances.Select((i, index) => new
            {
                processId = i.ProcessId,
                revitVersion = i.RevitVersion,
                documentTitle = i.DocumentTitle ?? "(unknown)",
                pipeName = i.PipeName,
                isActive = activePid.HasValue && i.ProcessId == activePid.Value,
                isCurrentTarget = index == 0
            }).ToList(),
            message = instances.Count == 0
                ? "No running Revit instances with an active MCP connector were found."
                : $"{instances.Count} Revit instance(s) found. Requests are routed to the first entry."
        };
        return Task.FromResult(JsonConvert.SerializeObject(response, ResultSerializerSettings));
    }

    [McpServerTool(Name = "revit_select_instance"),
     Description("Selects which running Revit instance MCP requests should be routed to, by process id (see revit_list_instances). Use this when multiple Revit projects are open and the wrong one is being targeted.")]
    public Task<string> SelectInstance(
        [Description("Process id of the Revit instance to route requests to.")] int processId,
        CancellationToken cancellationToken)
    {
        var instances = pipeClient.DiscoverLiveInstances();
        var match = instances.FirstOrDefault(i => i.ProcessId == processId);
        if (match == null)
        {
            return Task.FromResult(JsonConvert.SerializeObject(new
            {
                success = false,
                message = $"No running Revit instance with process id {processId} was found. Use revit_list_instances to see available instances."
            }, ResultSerializerSettings));
        }

        var ok = pipeClient.SelectInstance(processId);
        return Task.FromResult(JsonConvert.SerializeObject(new
        {
            success = ok,
            message = ok
                ? $"Requests will now be routed to Revit {match.RevitVersion} (pid {processId}, document: {match.DocumentTitle ?? "unknown"})."
                : "Failed to persist the instance selection."
        }, ResultSerializerSettings));
    }

    [McpServerTool(Name = "revit_get_selected_elements", ReadOnly = true),
     Description("Returns the currently selected elements from the active Revit document with category, family, type, level, location, and bounding box. " +
                 "Elements the user picked inside linked models are reported separately in linkedElements with their linkInstanceId and linked-document element id.")]
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

    [McpServerTool(Name = "revit_list_cad_imports", ReadOnly = true),
     Description("Lists CAD import instances and layers in a view, including visibility, halftone, projection line color, line weight, and line pattern. Returns presetChanges that can be reused with the CAD override tools.")]
    public async Task<string> ListCadImports(
        [Description("View element ID. 0 uses the active view.")] long viewId = 0,
        [Description("Include individual CAD layers. Default true.")] bool includeLayers = true,
        [Description("Read settings from the assigned view template when present. Default true.")] bool useViewTemplate = true,
        [Description("Optional substring filter for CAD import/type name.")] string? importNameFilter = null,
        [Description("Maximum category settings to return. 0 means all.")] int limit = 0,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["viewId"] = viewId,
            ["includeLayers"] = includeLayers,
            ["useViewTemplate"] = useViewTemplate,
            ["importNameFilter"] = importNameFilter ?? string.Empty,
            ["limit"] = limit
        };
        return FormatResult(await pipeClient.SendAsync("revit_list_cad_imports", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_preview_set_cad_overrides", ReadOnly = true),
     Description("Previews CAD visibility/graphics changes. Each changes item selects an import by importInstanceId, importName, or allImports=true. Omit layerName for the import category; use layerName='*' for all layers. Settings: visible, halftone, lineColor (#RRGGBB), lineWeight (1-16), linePatternId/linePatternName, clearGraphics.")]
    public async Task<string> PreviewSetCadOverrides(
        [Description("Change objects, for example [{\"importName\":\"site.dwg\",\"layerName\":\"A-WALL\",\"visible\":false}]")] object[] changes,
        [Description("Single target view ID. 0 uses the active view when viewIds is empty.")] long viewId = 0,
        [Description("Multiple target view IDs.")] long[]? viewIds = null,
        [Description("Modify an assigned view template rather than the project view. Default true; this can affect other views using the template.")] bool useViewTemplate = true,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["changes"] = changes,
            ["viewId"] = viewId,
            ["viewIds"] = viewIds ?? [],
            ["useViewTemplate"] = useViewTemplate
        };
        return FormatResult(await pipeClient.SendAsync("revit_preview_set_cad_overrides", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_set_cad_overrides"),
     Description("Applies CAD import/layer visibility, halftone, projection line color, weight, and pattern changes in a safe Revit transaction. Requires approval. Run revit_preview_set_cad_overrides with identical arguments first.")]
    public async Task<string> SetCadOverrides(
        [Description("Change objects, for example [{\"importName\":\"site.dwg\",\"layerName\":\"A-WALL\",\"visible\":false,\"halftone\":true,\"lineColor\":\"#808080\"}]")] object[] changes,
        [Description("Single target view ID. 0 uses the active view when viewIds is empty.")] long viewId = 0,
        [Description("Multiple target view IDs.")] long[]? viewIds = null,
        [Description("Modify an assigned view template rather than the project view. Default true; this can affect other views using the template.")] bool useViewTemplate = true,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["changes"] = changes,
            ["viewId"] = viewId,
            ["viewIds"] = viewIds ?? [],
            ["useViewTemplate"] = useViewTemplate
        };
        return FormatResult(await pipeClient.SendAsync("revit_set_cad_overrides", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_preview_copy_cad_overrides", ReadOnly = true),
     Description("Previews copying all CAD import/layer visibility and graphic settings from one view to target views. Imports and layers are matched by normalized name, and actual view/template owners are reported.")]
    public async Task<string> PreviewCopyCadOverrides(
        [Description("Source view ID. 0 uses the active view.")] long sourceViewId,
        [Description("Target view element IDs.")] long[] targetViewIds,
        [Description("Include individual CAD layers. Default true.")] bool includeLayers = true,
        [Description("Read source settings from its assigned view template. Default true.")] bool useSourceViewTemplate = true,
        [Description("Write target settings to assigned view templates. Default true; this can affect other views using those templates.")] bool useTargetViewTemplates = true,
        [Description("Optional substring filter for CAD import/type name.")] string? importNameFilter = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["sourceViewId"] = sourceViewId,
            ["targetViewIds"] = targetViewIds,
            ["includeLayers"] = includeLayers,
            ["useSourceViewTemplate"] = useSourceViewTemplate,
            ["useTargetViewTemplates"] = useTargetViewTemplates,
            ["importNameFilter"] = importNameFilter ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync("revit_preview_copy_cad_overrides", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_copy_cad_overrides"),
     Description("Copies all CAD import/layer visibility and graphic settings from one view to target views in a safe Revit transaction. Requires approval. Run revit_preview_copy_cad_overrides with identical arguments first.")]
    public async Task<string> CopyCadOverrides(
        [Description("Source view ID. 0 uses the active view.")] long sourceViewId,
        [Description("Target view element IDs.")] long[] targetViewIds,
        [Description("Include individual CAD layers. Default true.")] bool includeLayers = true,
        [Description("Read source settings from its assigned view template. Default true.")] bool useSourceViewTemplate = true,
        [Description("Write target settings to assigned view templates. Default true; this can affect other views using those templates.")] bool useTargetViewTemplates = true,
        [Description("Optional substring filter for CAD import/type name.")] string? importNameFilter = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["sourceViewId"] = sourceViewId,
            ["targetViewIds"] = targetViewIds,
            ["includeLayers"] = includeLayers,
            ["useSourceViewTemplate"] = useSourceViewTemplate,
            ["useTargetViewTemplates"] = useTargetViewTemplates,
            ["importNameFilter"] = importNameFilter ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync("revit_copy_cad_overrides", args, cancellationToken));
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
     Description("Counts model elements grouped by Category or FamilyAndType. Optionally filter to a specific category name. Cheap even for the whole model (no parameter reads) — good first call to discover what categories exist before running a narrower, detailed query like revit_find_elements_by_parameter.")]
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

    [McpServerTool(Name = "revit_export_list_to_excel", ReadOnly = true),
     Description("Exports a views/sheets/schedules list to .xlsx. Per-kind options: views=includeTemplates,includeUnplacedViews; sheets=includePlacedViews; schedules=includeFields.")]
    public async Task<string> ExportListToExcel(
        [Description("What to export: views | sheets | schedules")] string kind,
        [Description("views only: include template views. Default false.")] bool includeTemplates = false,
        [Description("views only: include views not placed on sheets. Default true.")] bool includeUnplacedViews = true,
        [Description("sheets only: include placed views per sheet. Default true.")] bool includePlacedViews = true,
        [Description("schedules only: include field names. Default true.")] bool includeFields = true,
        [Description("Output file name. Empty = default per kind.")] string? fileName = null,
        CancellationToken cancellationToken = default)
    {
        var (toolName, defaultFile, args) = (kind?.Trim().ToLowerInvariant()) switch
        {
            "views" => ("revit_export_view_list_to_excel", "Revit_View_List.xlsx",
                new Dictionary<string, object?>
                {
                    ["includeTemplates"] = includeTemplates,
                    ["includeUnplacedViews"] = includeUnplacedViews
                }),
            "sheets" => ("revit_export_sheet_list_to_excel", "Revit_Sheet_List.xlsx",
                new Dictionary<string, object?> { ["includePlacedViews"] = includePlacedViews }),
            "schedules" => ("revit_export_schedule_list_to_excel", "Revit_Schedule_List.xlsx",
                new Dictionary<string, object?> { ["includeFields"] = includeFields }),
            _ => (null, null, null)
        };

        if (toolName == null)
            return FormatBridgeError($"Invalid kind '{kind}'. Expected: views, sheets, or schedules.");

        args!["fileName"] = string.IsNullOrWhiteSpace(fileName) ? defaultFile : fileName;
        var result = await pipeClient.SendAsync(toolName, args, cancellationToken);
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

    [McpServerTool(Name = "revit_query_linked_elements", ReadOnly = true),
     Description("Queries elements INSIDE a linked model (Revit link or IFC converted to a Revit link). " +
                 "Required: linkInstanceId (from revit_list_clashable_links or ifc_list_links) plus category and/or nameFilter. " +
                 "Returned elementIds belong to the LINKED document — select them with revit_select_linked_elements.")]
    public async Task<string> QueryLinkedElements(
        [Description("Revit link instance element ID in the host model")] long linkInstanceId,
        [Description("Category name inside the link, e.g. 'Walls'")] string? category = null,
        [Description("Substring matched against element and type names")] string? nameFilter = null,
        [Description("Max elements to return (default 500)")] int limit = 500,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["linkInstanceId"] = linkInstanceId,
            ["category"] = category ?? string.Empty,
            ["nameFilter"] = nameFilter ?? string.Empty,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_query_linked_elements", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_select_linked_elements", ReadOnly = true),
     Description("Selects elements INSIDE a linked model in the host Revit UI — like a user picking linked elements interactively. " +
                 "Required: linkInstanceId, elementIds (ids in the LINKED document, e.g. from revit_query_linked_elements). " +
                 "Does not modify model data.")]
    public async Task<string> SelectLinkedElements(
        [Description("Revit link instance element ID in the host model")] long linkInstanceId,
        [Description("Element IDs inside the linked document")] long[] elementIds,
        [Description("Replace current selection (true) or add to it (false)")] bool replaceSelection = true,
        [Description("Zoom the active view to the selected linked elements")] bool zoomToSelection = false,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["linkInstanceId"] = linkInstanceId,
            ["elementIds"] = elementIds ?? [],
            ["replaceSelection"] = replaceSelection,
            ["zoomToSelection"] = zoomToSelection
        };
        var result = await pipeClient.SendAsync("revit_select_linked_elements", args, cancellationToken);
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
     Description("Finds model elements matching parameter filters. Supports category/selection/IDs, paging, safety caps, summaryOnly, tags, and compact responses. Requires category, filters, elementIds, useSelection, or summaryOnly=true; call revit_count_elements first when model categories are unknown.")]
    public async Task<string> FindElementsByParameter(
        [Description("JSON array of filter objects: [{parameterName, operator, value, matchMode, scope}]")] string? filters = null,
        [Description("Optional category name to restrict search (e.g. 'Fire Alarm Devices')")] string? category = null,
        [Description("If true, scan current Revit selection instead of category/elementIds.")] bool useSelection = false,
        [Description("Explicit element IDs to query.")] long[]? elementIds = null,
        [Description("Optional list of parameter names to include in returned elements")] string[]? returnParameters = null,
        [Description("Include instance parameters. Default true.")] bool includeInstanceParameters = true,
        [Description("Include type parameters. Heavy/repetitive per element; opt-in. Default false.")] bool includeTypeParameters = false,
        [Description("Max elements to scan/return (default 500).")] int limit = 500,
        [Description("Optional page size for paged results. Clamped by QueryLimits.MaxPageSize (500). Defaults to QueryLimits.DefaultPageSize (100) when omitted.")] int pageSize = -1,
        [Description("Zero-based page index for paged results.")] int page = 0,
        [Description("Maximum parameters returned per element. 0 uses the safety default (40).")] int maxParametersPerElement = 0,
        [Description("Maximum string length for parameter values. 0 uses the safety default (500 chars).")] int truncateStringLength = 0,
        [Description("If true, return category/family summary counts only without building element DTOs. Allows broad model scans.")] bool summaryOnly = false,
        [Description("If true, list the annotation tags attached to each returned element (tag text, tag family/type, owner view). An element can carry multiple tags; an empty list means it has none.")] bool includeTags = false,
        [Description("If true, return identity fields and parameter name/value pairs only, omitting verbose parameter metadata. Default false preserves the full response.")] bool compact = false,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseJsonArray(filters, "filters", out var parsedFilters, out var filtersError))
            return FormatBridgeError(filtersError!);

        var args = new Dictionary<string, object?>
        {
            ["category"] = category ?? string.Empty,
            ["filters"] = parsedFilters,
            ["useSelection"] = useSelection,
            ["elementIds"] = elementIds ?? [],
            ["returnParameters"] = returnParameters ?? [],
            ["includeInstanceParameters"] = includeInstanceParameters,
            ["includeTypeParameters"] = includeTypeParameters,
            ["limit"] = limit,
            ["pageSize"] = pageSize,
            ["page"] = page,
            ["maxParametersPerElement"] = maxParametersPerElement,
            ["truncateStringLength"] = truncateStringLength,
            ["summaryOnly"] = summaryOnly,
            ["includeTags"] = includeTags,
            ["compact"] = compact
        };
        var result = await pipeClient.SendAsync("revit_find_elements_by_parameter", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_get_elements_info", ReadOnly = true),
     Description("Returns structured element info and selected parameter values. Requires selection, elementIds, category, or summaryOnly=true. Supports filters, paging, safety caps, tags, and compact responses.")]
    public async Task<string> GetElementsInfo(
        [Description("If true, use current selection.")] bool useSelection = false,
        [Description("List of element IDs to retrieve.")] long[]? elementIds = null,
        [Description("Category name filter (e.g. 'Fire Alarm Devices').")] string? category = null,
        [Description("JSON array of parameter filters: [{parameterName, operator, value, matchMode, scope}]")] string? filters = null,
        [Description("Parameter names to return (partial match). Leave empty for all.")] string[]? parameterNames = null,
        [Description("Include instance parameters. Default true.")] bool includeInstanceParameters = true,
        [Description("Include type parameters. Heavy/repetitive per element; opt-in. Default false.")] bool includeTypeParameters = false,
        [Description("Max elements to scan/return (default 500).")] int limit = 500,
        [Description("Optional page size for paged results. Clamped by QueryLimits.MaxPageSize (500). Defaults to QueryLimits.DefaultPageSize (100) when omitted.")] int pageSize = -1,
        [Description("Zero-based page index for paged results.")] int page = 0,
        [Description("Maximum parameters returned per element. 0 uses the safety default (40).")] int maxParametersPerElement = 0,
        [Description("Maximum string length for parameter values. 0 uses the safety default (500 chars).")] int truncateStringLength = 0,
        [Description("If true, return category/family summary counts only without element DTOs. Allows broad model scans without category/selection scope.")] bool summaryOnly = false,
        [Description("If true, list the annotation tags attached to each returned element (tag text, tag family/type, owner view). An element can carry multiple tags; an empty list means it has none.")] bool includeTags = false,
        [Description("If true, return identity fields and parameter name/value pairs only, omitting verbose parameter metadata. Default false preserves the full response.")] bool compact = false,
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
            ["limit"] = limit,
            ["pageSize"] = pageSize,
            ["page"] = page,
            ["maxParametersPerElement"] = maxParametersPerElement,
            ["truncateStringLength"] = truncateStringLength,
            ["summaryOnly"] = summaryOnly,
            ["includeTags"] = includeTags,
            ["compact"] = compact
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

    private static bool TryParseJsonObject(
        string? json,
        string argumentName,
        out object? parsed,
        out string? error)
    {
        parsed = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
            return true;

        try
        {
            var token = Newtonsoft.Json.Linq.JToken.Parse(json);
            if (token is not Newtonsoft.Json.Linq.JObject)
            {
                error = $"{argumentName} must be a JSON object.";
                return false;
            }
            parsed = token;
            return true;
        }
        catch (Exception ex)
        {
            error =
                $"{argumentName} could not be parsed as JSON object: {ex.Message}";
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

    [McpServerTool(Name = "revit_export_issues"),
     Description("Exports an issue report (IssueReportDto JSON in reportJson) to a file. format: json | excel | markdown | html_dashboard. Writes to disk; requires approval. Returns filePath (+ totalIssues, runId for json/excel/markdown). html_dashboard supports fileName and includeEmbeddedJson.")]
    public async Task<string> ExportIssues(
        [Description("Output format: json | excel | markdown | html_dashboard")] string format,
        [Description("The full IssueReportDto serialised as a JSON string.")] string reportJson,
        [Description("html_dashboard only: output file name (no path). Empty = auto.")] string? fileName = null,
        [Description("html_dashboard only: embed raw JSON for re-import. Default true.")] bool includeEmbeddedJson = true,
        CancellationToken cancellationToken = default)
    {
        var (toolName, args) = (format?.Trim().ToLowerInvariant()) switch
        {
            "json" => ("revit_export_issues_json", new Dictionary<string, object?> { ["reportJson"] = reportJson }),
            "excel" => ("revit_export_issues_excel", new Dictionary<string, object?> { ["reportJson"] = reportJson }),
            "markdown" => ("revit_export_issues_markdown", new Dictionary<string, object?> { ["reportJson"] = reportJson }),
            "html_dashboard" => ("revit_export_issues_html_dashboard", new Dictionary<string, object?>
            {
                ["reportJson"] = reportJson,
                ["fileName"] = fileName,
                ["includeEmbeddedJson"] = includeEmbeddedJson
            }),
            _ => (null, null)
        };

        if (toolName == null)
            return FormatBridgeError($"Invalid format '{format}'. Expected: json, excel, markdown, or html_dashboard.");

        var result = await pipeClient.SendAsync(toolName, args!, cancellationToken);
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

    [McpServerTool(Name = "revit_get_text_notes", ReadOnly = true),
     Description("Returns text note elements (text boxes placed via the Revit Text command) from the active document. Default scope is the active view. Pass viewId=0 for all views, or a specific view element ID. Supports text content filtering and selection-based reading.")]
    public async Task<string> GetTextNotes(
        [Description("Scope: -1 or omit = active view, 0 = all views, >0 = specific view element ID.")] long viewId = -1,
        [Description("Case-insensitive substring filter on text content. Leave empty to return all.")] string? textFilter = null,
        [Description("If true, read text notes from the current Revit selection only.")] bool useSelection = false,
        [Description("Maximum number of text notes to return. Default 200.")] int limit = 200,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["viewId"]      = viewId,
            ["textFilter"]  = textFilter ?? string.Empty,
            ["useSelection"] = useSelection,
            ["limit"]       = limit
        };
        var result = await pipeClient.SendAsync("revit_get_text_notes", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_place_family_instances"),
     Description("Places instances of a loaded family type at given points (millimetres). Handles model components (level-based) and detail items (view-based) automatically from the family's placement type. Pick the type via typeId, or familyName and/or typeName (partial match; ambiguity returns candidates). Optional per-point rotationDegrees, levelName, viewId (detail items), hostElementId (hosted families). Requires approval; reversible via Revit Undo.")]
    public async Task<string> PlaceFamilyInstances(
        [Description("JSON array of placement points in mm: [{x, y, z, rotationDegrees}]")] string placements,
        [Description("Family name to place (partial match).")] string? familyName = null,
        [Description("Type name within the family (partial match).")] string? typeName = null,
        [Description("Exact family type element ID — skips name matching.")] long typeId = 0,
        [Description("Level name for level-based placement. Defaults to the active plan view's level, else the level nearest each point's z.")] string? levelName = null,
        [Description("View element ID for view-based (detail item) placement. Defaults to the active view.")] long viewId = 0,
        [Description("Host element ID for hosted or work-plane-based families (e.g. a wall).")] long hostElementId = 0,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseJsonArray(placements, "placements", out var parsedPlacements, out var placementsError))
            return FormatBridgeError(placementsError!);

        var args = new Dictionary<string, object?>
        {
            ["placements"] = parsedPlacements,
            ["familyName"] = familyName ?? string.Empty,
            ["typeName"] = typeName ?? string.Empty,
            ["typeId"] = typeId,
            ["levelName"] = levelName ?? string.Empty,
            ["viewId"] = viewId,
            ["hostElementId"] = hostElementId
        };
        var result = await pipeClient.SendAsync("revit_place_family_instances", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_list_tag_types", ReadOnly = true),
     Description("Lists loaded IndependentTag family types. Optionally filters by tagCategoryId and resolves Left/Right/Up/Down family-type variants using directionKeyword.")]
    public async Task<string> ListTagTypes(
        [Description("Optional tag category element ID.")] long tagCategoryId = 0,
        [Description("Optional family/type keyword used to find directional Left/Right/Up/Down variants.")] string? directionKeyword = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["tagCategoryId"] = tagCategoryId,
            ["directionKeyword"] = directionKeyword ?? string.Empty
        };
        var result = await pipeClient.SendAsync(
            "revit_list_tag_types",
            args,
            cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_find_managed_tags", ReadOnly = true),
     Description("Finds tags carrying the SmartTags-compatible management marker in a graphical view. Filters by tagIds or referenced host elementIds and returns type, reference, leader, orientation, head, creator, version, and timestamp data.")]
    public async Task<string> FindManagedTags(
        [Description("View element ID. Defaults to the active view.")] long viewId = 0,
        [Description("Optional managed tag element IDs.")] long[]? tagIds = null,
        [Description("Optional referenced host element IDs.")] long[]? elementIds = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["viewId"] = viewId,
            ["tagIds"] = tagIds ?? [],
            ["elementIds"] = elementIds ?? []
        };
        var result = await pipeClient.SendAsync(
            "revit_find_managed_tags",
            args,
            cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_preview_place_tags", ReadOnly = true),
     Description("Read-only preview of the SmartTags-compatible batch placement pipeline. Supports IDs, current selection, or all elements of one category; nine anchors; direction-specific tag types; leaders; rotation; duplicate skipping; and collision avoidance.")]
    public async Task<string> PreviewPlaceTags(
        [Description("Element IDs to tag.")] long[]? elementIds = null,
        [Description("Use the current Revit selection as targets.")] bool useSelection = false,
        [Description("Tag every visible element in categoryId in the target view.")] bool tagAllInView = false,
        [Description("Target model-category element ID; required with tagAllInView.")] long categoryId = 0,
        [Description("View element ID. Defaults to the active view.")] long viewId = 0,
        [Description("Exact tag family type element ID.")] long tagTypeId = 0,
        [Description("Tag family name (partial match).")] string? tagFamilyName = null,
        [Description("Tag type name (partial match).")] string? tagTypeName = null,
        [Description("Right, Left, Up, or Down.")] string direction = "Right",
        [Description("Center, TopLeft, TopCenter, TopRight, LeftCenter, RightCenter, BottomLeft, BottomCenter, or BottomRight.")] string anchorPoint = "Center",
        [Description("Attached leader length in mm, multiplied by view scale.")] double attachedLengthMm = 0,
        [Description("Free leader length in mm, multiplied by view scale.")] double freeLengthMm = 0,
        [Description("Create a visible leader.")] bool addLeader = false,
        [Description("Attached or Free.")] string leaderEndCondition = "Attached",
        [Description("Horizontal or Vertical.")] string orientation = "Horizontal",
        [Description("Base tag rotation in degrees.")] double rotationDegrees = 0,
        [Description("Rotate placement and tag with each host's in-view orientation.")] bool detectElementRotation = false,
        [Description("Enable deterministic two-pass collision avoidance.")] bool enableCollisionDetection = true,
        [Description("Required collision gap in mm.")] double collisionGapMm = 1,
        [Description("Minimum host-to-tag offset in mm.")] double minimumOffsetMm = 300,
        [Description("Skip hosts already tagged in the view.")] bool skipAlreadyTagged = true,
        [Description("Optional Left tag type ID.")] long leftTagTypeId = 0,
        [Description("Optional Right tag type ID.")] long rightTagTypeId = 0,
        [Description("Optional Up tag type ID.")] long upTagTypeId = 0,
        [Description("Optional Down tag type ID.")] long downTagTypeId = 0,
        [Description("Optional keyword for automatic directional type discovery.")] string? directionKeyword = null,
        CancellationToken cancellationToken = default)
    {
        var args = BuildTagPlacementArgs(
            elementIds, useSelection, tagAllInView, categoryId, viewId,
            tagTypeId, tagFamilyName, tagTypeName, direction, anchorPoint,
            attachedLengthMm, freeLengthMm, addLeader, leaderEndCondition,
            orientation, rotationDegrees, detectElementRotation,
            enableCollisionDetection, collisionGapMm, minimumOffsetMm,
            skipAlreadyTagged, leftTagTypeId, rightTagTypeId, upTagTypeId,
            downTagTypeId, directionKeyword);
        var result = await pipeClient.SendAsync(
            "revit_preview_place_tags",
            args,
            cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_place_tags"),
     Description("Places SmartTags-compatible IndependentTags using IDs, current selection, or all visible elements of one category. Supports nine anchors, direction-specific tag types, leader/orientation/rotation settings, host rotation detection, duplicate skipping, two-pass collision avoidance, and managed-tag metadata. Requires approval and is reversible with one Revit Undo.")]
    public async Task<string> PlaceTags(
        [Description("Element IDs to tag.")] long[]? elementIds = null,
        [Description("Use the current Revit selection as targets.")] bool useSelection = false,
        [Description("Tag every visible element in categoryId in the target view.")] bool tagAllInView = false,
        [Description("Target model-category element ID; required with tagAllInView.")] long categoryId = 0,
        [Description("View element ID. Defaults to the active view.")] long viewId = 0,
        [Description("Exact tag family type element ID.")] long tagTypeId = 0,
        [Description("Tag family name (partial match).")] string? tagFamilyName = null,
        [Description("Tag type name (partial match).")] string? tagTypeName = null,
        [Description("Right, Left, Up, or Down.")] string direction = "Right",
        [Description("Center or one of the eight bounding-box edge/corner anchors.")] string anchorPoint = "Center",
        [Description("Attached leader length in mm, multiplied by view scale.")] double attachedLengthMm = 0,
        [Description("Free leader length in mm, multiplied by view scale.")] double freeLengthMm = 0,
        [Description("Create a visible leader.")] bool addLeader = false,
        [Description("Attached or Free.")] string leaderEndCondition = "Attached",
        [Description("Horizontal or Vertical.")] string orientation = "Horizontal",
        [Description("Base tag rotation in degrees.")] double rotationDegrees = 0,
        [Description("Rotate placement and tag with each host's in-view orientation.")] bool detectElementRotation = false,
        [Description("Enable deterministic two-pass collision avoidance.")] bool enableCollisionDetection = true,
        [Description("Required collision gap in mm.")] double collisionGapMm = 1,
        [Description("Minimum host-to-tag offset in mm.")] double minimumOffsetMm = 300,
        [Description("Skip hosts already tagged in the view.")] bool skipAlreadyTagged = true,
        [Description("Optional Left tag type ID.")] long leftTagTypeId = 0,
        [Description("Optional Right tag type ID.")] long rightTagTypeId = 0,
        [Description("Optional Up tag type ID.")] long upTagTypeId = 0,
        [Description("Optional Down tag type ID.")] long downTagTypeId = 0,
        [Description("Optional keyword for automatic directional type discovery.")] string? directionKeyword = null,
        CancellationToken cancellationToken = default)
    {
        var args = BuildTagPlacementArgs(
            elementIds, useSelection, tagAllInView, categoryId, viewId,
            tagTypeId, tagFamilyName, tagTypeName, direction, anchorPoint,
            attachedLengthMm, freeLengthMm, addLeader, leaderEndCondition,
            orientation, rotationDegrees, detectElementRotation,
            enableCollisionDetection, collisionGapMm, minimumOffsetMm,
            skipAlreadyTagged, leftTagTypeId, rightTagTypeId, upTagTypeId,
            downTagTypeId, directionKeyword);
        var result = await pipeClient.SendAsync(
            "revit_place_tags",
            args,
            cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_analyze_selected_tag_template", ReadOnly = true),
     Description("Analyzes one selected example IndependentTag and its referenced FamilyInstance without changing Revit. Learns exact tag type, host-local right/front offsets, placement side/distance, anchor, rotation behavior, orientation, and leader geometry; previews matching targets with counts, skip reasons, warnings, and pagination. Use before revit_apply_selected_tag_template.")]
    public async Task<string> AnalyzeSelectedTagTemplate(
        [Description("Optional explicit source tag ID; normally leave 0 and select one example tag in Revit.")] long sourceTagId = 0,
        [Description("sameFamily (default), sameFamilyAndType, sameCategory, selection, or explicitElementIds.")] string scope = "sameFamily",
        [Description("Targets for scope=explicitElementIds.")] long[]? explicitElementIds = null,
        [Description("SmartTagCenter (default), LocationPoint, or ViewBoundingBoxCenter.")] string? anchorMode = null,
        [Description("Include the source host as a target. Default false.")] bool includeSourceHost = false,
        [Description("Skip targets with the same tag type in the source view. Default true.")] bool skipAlreadyTagged = true,
        [Description("Include every type in the source family for sameFamily. Default true.")] bool includeAllHostTypes = true,
        [Description("Optional learned local-right offset override in mm.")] double? localRightOffsetMm = null,
        [Description("Optional learned local-front offset override in mm.")] double? localFrontOffsetMm = null,
        [Description("Optional KeepViewAligned, FollowHost, or RelativeToHost override.")] string? rotationMode = null,
        [Description("Optional relative tag-to-host rotation override in degrees.")] double? relativeRotationDegrees = null,
        [Description("Optional Horizontal or Vertical orientation override.")] string? orientation = null,
        [Description("Optional leader on/off override.")] bool? hasLeader = null,
        [Description("1-based target-preview page.")] int page = 1,
        [Description("Target-preview page size, 1-500.")] int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        var args = BuildSelectedTagTemplateArgs(
            sourceTagId, scope, explicitElementIds, anchorMode,
            includeSourceHost, skipAlreadyTagged, false,
            includeAllHostTypes, false, 1, 0, localRightOffsetMm,
            localFrontOffsetMm, rotationMode, relativeRotationDegrees,
            orientation, hasLeader, null, page, pageSize);
        var result = await pipeClient.SendAsync(
            "revit_analyze_selected_tag_template",
            args,
            cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_apply_selected_tag_template"),
     Description("Tags matching FamilyInstances like one selected example IndependentTag. Re-analyzes the live source, validates optional analyzedTemplateJson, and reconstructs head/leader positions from host-local right/front offsets so rotated, flipped, and mirrored targets preserve the visual rule. Uses the exact example tag type. Existing matching tags are skipped by default. Requires approval; successful placements are one Revit Undo operation.")]
    public async Task<string> ApplySelectedTagTemplate(
        [Description("Optional explicit source tag ID; normally leave 0 and keep the analyzed example tag selected.")] long sourceTagId = 0,
        [Description("sameFamily (default), sameFamilyAndType, sameCategory, selection, or explicitElementIds.")] string scope = "sameFamily",
        [Description("Targets for scope=explicitElementIds.")] long[]? explicitElementIds = null,
        [Description("SmartTagCenter, LocationPoint, or ViewBoundingBoxCenter. Omit to use the analyzed/default anchor.")] string? anchorMode = null,
        [Description("Include the source host as a target. Default false.")] bool includeSourceHost = false,
        [Description("Skip targets with the same tag type in the source view. Default true.")] bool skipAlreadyTagged = true,
        [Description("Replacement/deletion is intentionally unsupported; must remain false.")] bool replaceExistingTags = false,
        [Description("Include every type in the source family for sameFamily. Default true.")] bool includeAllHostTypes = true,
        [Description("Enable collision avoidance after reproducing the learned rule. Default false.")] bool enableCollisionDetection = false,
        [Description("Required collision gap in mm.")] double collisionGapMm = 1,
        [Description("Optional minimum host-to-tag offset during collision adjustment, in mm.")] double minimumOffsetMm = 0,
        [Description("Optional learned local-right offset override in mm.")] double? localRightOffsetMm = null,
        [Description("Optional learned local-front offset override in mm.")] double? localFrontOffsetMm = null,
        [Description("Optional KeepViewAligned, FollowHost, or RelativeToHost override.")] string? rotationMode = null,
        [Description("Optional relative tag-to-host rotation override in degrees.")] double? relativeRotationDegrees = null,
        [Description("Optional Horizontal or Vertical orientation override.")] string? orientation = null,
        [Description("Optional leader on/off override.")] bool? hasLeader = null,
        [Description("Optional JSON object copied from the analysis response (source + inferredRule). Identity fields are revalidated before writing.")] string? analyzedTemplateJson = null,
        CancellationToken cancellationToken = default)
    {
        if (replaceExistingTags)
            return FormatBridgeError(
                "replaceExistingTags=true is not supported. This workflow never deletes existing tags.");
        if (!TryParseJsonObject(
                analyzedTemplateJson,
                "analyzedTemplateJson",
                out var analyzedTemplate,
                out var templateError))
            return FormatBridgeError(templateError!);

        var args = BuildSelectedTagTemplateArgs(
            sourceTagId, scope, explicitElementIds, anchorMode,
            includeSourceHost, skipAlreadyTagged, replaceExistingTags,
            includeAllHostTypes, enableCollisionDetection, collisionGapMm,
            minimumOffsetMm, localRightOffsetMm, localFrontOffsetMm,
            rotationMode, relativeRotationDegrees, orientation, hasLeader,
            analyzedTemplate, 1, 100);
        var result = await pipeClient.SendAsync(
            "revit_apply_selected_tag_template",
            args,
            cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_preview_retag", ReadOnly = true),
     Description("Previews SmartTags-compatible retag/normalize adjustments for managed tags without changing Revit. Filter by viewId, tagIds, or referenced elementIds; with no filters, previews every managed tag in the view.")]
    public async Task<string> PreviewRetag(
        [Description("View ID. Defaults to active view.")] long viewId = 0,
        [Description("Managed tag IDs.")] long[]? tagIds = null,
        [Description("Referenced host IDs.")] long[]? elementIds = null,
        [Description("Right, Left, Up, or Down.")] string direction = "Right",
        [Description("Smart Tags anchor position.")] string anchorPoint = "Center",
        [Description("Attached length in mm.")] double attachedLengthMm = 0,
        [Description("Free length in mm.")] double freeLengthMm = 0,
        [Description("Use leaders.")] bool addLeader = false,
        [Description("Attached or Free.")] string leaderEndCondition = "Attached",
        [Description("Horizontal or Vertical.")] string orientation = "Horizontal",
        [Description("Rotation in degrees.")] double rotationDegrees = 0,
        [Description("Follow host rotation.")] bool detectElementRotation = false,
        [Description("Enable collision avoidance.")] bool enableCollisionDetection = true,
        [Description("Collision gap in mm.")] double collisionGapMm = 1,
        [Description("Minimum offset in mm.")] double minimumOffsetMm = 300,
        CancellationToken cancellationToken = default)
    {
        var args = BuildRetagArgs(
            viewId, tagIds, elementIds, direction, anchorPoint,
            attachedLengthMm, freeLengthMm, addLeader, leaderEndCondition,
            orientation, rotationDegrees, detectElementRotation,
            enableCollisionDetection, collisionGapMm, minimumOffsetMm);
        var result = await pipeClient.SendAsync(
            "revit_preview_retag",
            args,
            cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_retag"),
     Description("Applies SmartTags-compatible retag/normalize adjustments to managed tags. Filter by viewId, tagIds, or referenced elementIds; with no filters, adjusts every managed tag in the view. Requires approval and supports Revit Undo. Use revit_preview_retag first.")]
    public async Task<string> Retag(
        [Description("View ID. Defaults to active view.")] long viewId = 0,
        [Description("Managed tag IDs.")] long[]? tagIds = null,
        [Description("Referenced host IDs.")] long[]? elementIds = null,
        [Description("Right, Left, Up, or Down.")] string direction = "Right",
        [Description("Smart Tags anchor position.")] string anchorPoint = "Center",
        [Description("Attached length in mm.")] double attachedLengthMm = 0,
        [Description("Free length in mm.")] double freeLengthMm = 0,
        [Description("Use leaders.")] bool addLeader = false,
        [Description("Attached or Free.")] string leaderEndCondition = "Attached",
        [Description("Horizontal or Vertical.")] string orientation = "Horizontal",
        [Description("Rotation in degrees.")] double rotationDegrees = 0,
        [Description("Follow host rotation.")] bool detectElementRotation = false,
        [Description("Enable collision avoidance.")] bool enableCollisionDetection = true,
        [Description("Collision gap in mm.")] double collisionGapMm = 1,
        [Description("Minimum offset in mm.")] double minimumOffsetMm = 300,
        CancellationToken cancellationToken = default)
    {
        var args = BuildRetagArgs(
            viewId, tagIds, elementIds, direction, anchorPoint,
            attachedLengthMm, freeLengthMm, addLeader, leaderEndCondition,
            orientation, rotationDegrees, detectElementRotation,
            enableCollisionDetection, collisionGapMm, minimumOffsetMm);
        var result = await pipeClient.SendAsync(
            "revit_retag",
            args,
            cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_annotate_detail_lines"),
     Description("Places one loaded Detail Items family type at detail-line midpoints. Targets come from detailLineIds, current selection, or all detail lines in a view. Supports offset direction and optional alignment to each line. Requires approval and is reversible.")]
    public async Task<string> AnnotateDetailLines(
        [Description("Loaded Detail Items FamilySymbol ID.")] long detailItemTypeId,
        [Description("DetailCurve element IDs.")] long[]? detailLineIds = null,
        [Description("Use selected DetailCurve elements.")] bool useSelection = false,
        [Description("Annotate every DetailCurve in the target view.")] bool annotateAllInView = false,
        [Description("View ID. Defaults to active view.")] long viewId = 0,
        [Description("Offset from line midpoint in mm.")] double offsetMm = 0,
        [Description("Right, Left, Up, or Down.")] string direction = "Right",
        [Description("Rotate each detail item to the line direction.")] bool alignToLineDirection = false,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["detailItemTypeId"] = detailItemTypeId,
            ["detailLineIds"] = detailLineIds ?? [],
            ["useSelection"] = useSelection,
            ["annotateAllInView"] = annotateAllInView,
            ["viewId"] = viewId,
            ["offsetMm"] = offsetMm,
            ["direction"] = direction,
            ["alignToLineDirection"] = alignToLineDirection
        };
        var result = await pipeClient.SendAsync(
            "revit_annotate_detail_lines",
            args,
            cancellationToken);
        return FormatResult(result);
    }

    private static Dictionary<string, object?> BuildTagPlacementArgs(
        long[]? elementIds,
        bool useSelection,
        bool tagAllInView,
        long categoryId,
        long viewId,
        long tagTypeId,
        string? tagFamilyName,
        string? tagTypeName,
        string direction,
        string anchorPoint,
        double attachedLengthMm,
        double freeLengthMm,
        bool addLeader,
        string leaderEndCondition,
        string orientation,
        double rotationDegrees,
        bool detectElementRotation,
        bool enableCollisionDetection,
        double collisionGapMm,
        double minimumOffsetMm,
        bool skipAlreadyTagged,
        long leftTagTypeId,
        long rightTagTypeId,
        long upTagTypeId,
        long downTagTypeId,
        string? directionKeyword)
    {
        return new Dictionary<string, object?>
        {
            ["elementIds"] = elementIds ?? [],
            ["useSelection"] = useSelection,
            ["tagAllInView"] = tagAllInView,
            ["categoryId"] = categoryId,
            ["viewId"] = viewId,
            ["tagTypeId"] = tagTypeId,
            ["tagFamilyName"] = tagFamilyName ?? string.Empty,
            ["tagTypeName"] = tagTypeName ?? string.Empty,
            ["direction"] = direction,
            ["anchorPoint"] = anchorPoint,
            ["attachedLengthMm"] = attachedLengthMm,
            ["freeLengthMm"] = freeLengthMm,
            ["addLeader"] = addLeader,
            ["leaderEndCondition"] = leaderEndCondition,
            ["orientation"] = orientation,
            ["rotationDegrees"] = rotationDegrees,
            ["detectElementRotation"] = detectElementRotation,
            ["enableCollisionDetection"] = enableCollisionDetection,
            ["collisionGapMm"] = collisionGapMm,
            ["minimumOffsetMm"] = minimumOffsetMm,
            ["skipAlreadyTagged"] = skipAlreadyTagged,
            ["leftTagTypeId"] = leftTagTypeId,
            ["rightTagTypeId"] = rightTagTypeId,
            ["upTagTypeId"] = upTagTypeId,
            ["downTagTypeId"] = downTagTypeId,
            ["directionKeyword"] = directionKeyword ?? string.Empty
        };
    }

    private static Dictionary<string, object?> BuildSelectedTagTemplateArgs(
        long sourceTagId,
        string scope,
        long[]? explicitElementIds,
        string? anchorMode,
        bool includeSourceHost,
        bool skipAlreadyTagged,
        bool replaceExistingTags,
        bool includeAllHostTypes,
        bool enableCollisionDetection,
        double collisionGapMm,
        double minimumOffsetMm,
        double? localRightOffsetMm,
        double? localFrontOffsetMm,
        string? rotationMode,
        double? relativeRotationDegrees,
        string? orientation,
        bool? hasLeader,
        object? analyzedTemplate,
        int page,
        int pageSize)
    {
        var args = new Dictionary<string, object?>
        {
            ["sourceTagId"] = sourceTagId,
            ["scope"] = scope,
            ["explicitElementIds"] = explicitElementIds ?? [],
            ["includeSourceHost"] = includeSourceHost,
            ["skipAlreadyTagged"] = skipAlreadyTagged,
            ["replaceExistingTags"] = replaceExistingTags,
            ["includeAllHostTypes"] = includeAllHostTypes,
            ["enableCollisionDetection"] = enableCollisionDetection,
            ["collisionGapMm"] = collisionGapMm,
            ["minimumOffsetMm"] = minimumOffsetMm,
            ["page"] = page,
            ["pageSize"] = pageSize,
            // Approval binds the write to the exact selection analyzed.
            ["useSelection"] = true
        };
        if (!string.IsNullOrWhiteSpace(anchorMode))
            args["anchorMode"] = anchorMode;
        if (localRightOffsetMm.HasValue)
            args["localRightOffsetMm"] = localRightOffsetMm.Value;
        if (localFrontOffsetMm.HasValue)
            args["localFrontOffsetMm"] = localFrontOffsetMm.Value;
        if (!string.IsNullOrWhiteSpace(rotationMode))
            args["rotationMode"] = rotationMode;
        if (relativeRotationDegrees.HasValue)
            args["relativeRotationDegrees"] =
                relativeRotationDegrees.Value;
        if (!string.IsNullOrWhiteSpace(orientation))
            args["orientation"] = orientation;
        if (hasLeader.HasValue)
            args["hasLeader"] = hasLeader.Value;
        if (analyzedTemplate != null)
            args["analyzedTemplate"] = analyzedTemplate;
        return args;
    }

    private static Dictionary<string, object?> BuildRetagArgs(
        long viewId,
        long[]? tagIds,
        long[]? elementIds,
        string direction,
        string anchorPoint,
        double attachedLengthMm,
        double freeLengthMm,
        bool addLeader,
        string leaderEndCondition,
        string orientation,
        double rotationDegrees,
        bool detectElementRotation,
        bool enableCollisionDetection,
        double collisionGapMm,
        double minimumOffsetMm)
    {
        return new Dictionary<string, object?>
        {
            ["viewId"] = viewId,
            ["tagIds"] = tagIds ?? [],
            ["elementIds"] = elementIds ?? [],
            ["direction"] = direction,
            ["anchorPoint"] = anchorPoint,
            ["attachedLengthMm"] = attachedLengthMm,
            ["freeLengthMm"] = freeLengthMm,
            ["addLeader"] = addLeader,
            ["leaderEndCondition"] = leaderEndCondition,
            ["orientation"] = orientation,
            ["rotationDegrees"] = rotationDegrees,
            ["detectElementRotation"] = detectElementRotation,
            ["enableCollisionDetection"] = enableCollisionDetection,
            ["collisionGapMm"] = collisionGapMm,
            ["minimumOffsetMm"] = minimumOffsetMm
        };
    }

    [McpServerTool(Name = "revit_create_text_notes"),
     Description("Creates text notes in a view at given positions (millimetres, model coordinates). Uses the view's default text note type unless typeId or typeName is given. Requires approval; reversible via Revit Undo.")]
    public async Task<string> CreateTextNotes(
        [Description("JSON array of notes: [{text, x, y, z, widthMm, rotationDegrees}] — widthMm/rotationDegrees optional.")] string notes,
        [Description("View element ID. Defaults to the active view.")] long viewId = 0,
        [Description("Text note type element ID.")] long typeId = 0,
        [Description("Text note type name (partial match), e.g. '2.5mm Arial'.")] string? typeName = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseJsonArray(notes, "notes", out var parsedNotes, out var notesError))
            return FormatBridgeError(notesError!);

        var args = new Dictionary<string, object?>
        {
            ["notes"] = parsedNotes,
            ["viewId"] = viewId,
            ["typeId"] = typeId,
            ["typeName"] = typeName ?? string.Empty
        };
        var result = await pipeClient.SendAsync("revit_create_text_notes", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_create_lines"),
     Description("Creates straight lines from segments given in millimetres. kind='detail' draws view-specific detail lines in a view (viewId, default active); kind='model' draws model lines in 3D space — each segment automatically gets a sketch plane that contains it. Optional lineStyle name applied to all created lines. Requires approval; reversible via Revit Undo.")]
    public async Task<string> CreateLines(
        [Description("JSON array of segments in mm: [{x1, y1, z1, x2, y2, z2}]")] string lines,
        [Description("'detail' (view-specific) or 'model' (3D). Default 'detail'.")] string kind = "detail",
        [Description("View element ID for detail lines. Defaults to the active view.")] long viewId = 0,
        [Description("Line style name to apply (e.g. 'Hidden Lines'). Default style when omitted or not found.")] string? lineStyle = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseJsonArray(lines, "lines", out var parsedLines, out var linesError))
            return FormatBridgeError(linesError!);

        var args = new Dictionary<string, object?>
        {
            ["lines"] = parsedLines,
            ["kind"] = kind,
            ["viewId"] = viewId,
            ["lineStyle"] = lineStyle ?? string.Empty
        };
        var result = await pipeClient.SendAsync("revit_create_lines", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_list_dimension_types", ReadOnly = true),
     Description("Lists all dimension types in the model with their style (Linear, Angular, Radial, Diameter, ArcLength, SpotElevation, SpotCoordinate, SpotSlope). Use typeId as dimensionTypeId in revit_place_dimensions. Optional styleFilter narrows by style name.")]
    public async Task<string> ListDimensionTypes(
        [Description("Optional style filter, e.g. 'Linear' or 'Spot'")] string? styleFilter = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["styleFilter"] = styleFilter ?? string.Empty };
        var result = await pipeClient.SendAsync("revit_list_dimension_types", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_place_dimensions"),
     Description("Places dimensions in a view. Requires approval; reversible via Revit Undo. " +
                 "kind: aligned | horizontal | vertical (2+ elementIds → one dimension across all) | angular (exactly 2 linear elements) | " +
                 "radial | diameter | arcLength (arc elements, one dimension each; Revit 2025+) | spotElevation | spotCoordinate (one spot per element, with leader). " +
                 "References are auto-extracted from walls, grids, levels, reference planes, model/detail lines, and family instances with reference planes. " +
                 "offsetMm sets the distance of the dimension line / leader bend from the elements; leaderLengthMm sets the spot-dimension leader segment length.")]
    public async Task<string> PlaceDimensions(
        [Description("Dimension kind: aligned | horizontal | vertical | angular | radial | diameter | arcLength | spotElevation | spotCoordinate")] string kind,
        [Description("Element IDs to dimension")] long[] elementIds,
        [Description("View element ID. Defaults to the active view.")] long viewId = 0,
        [Description("Dimension type ID (from revit_list_dimension_types). Default type when omitted.")] long dimensionTypeId = 0,
        [Description("Distance of the dimension line / leader bend from the elements in mm (default 1000; sign flips the side)")] double offsetMm = 1000,
        [Description("Spot dimensions: horizontal leader segment length in mm (default 600)")] double leaderLengthMm = 600,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["kind"] = kind,
            ["elementIds"] = elementIds ?? [],
            ["viewId"] = viewId,
            ["dimensionTypeId"] = dimensionTypeId,
            ["offsetMm"] = offsetMm,
            ["leaderLengthMm"] = leaderLengthMm
        };
        var result = await pipeClient.SendAsync("revit_place_dimensions", args, cancellationToken);
        return FormatResult(result);
    }

    private static readonly JsonSerializerSettings ResultSerializerSettings = new()
    {
        Formatting = Formatting.None,
        NullValueHandling = NullValueHandling.Ignore
    };

    private static string FormatResult(McpToolResult result)
    {
        var response = new
        {
            success = result.Success,
            status = result.Status,
            message = result.Message,
            durationMs = result.DurationMs,
            data = result.Data,
            warnings = result.Warnings is { Count: > 0 } ? result.Warnings : null,
            errors = result.Errors is { Count: > 0 } ? result.Errors : null
        };
        return JsonConvert.SerializeObject(response, ResultSerializerSettings);
    }

    // ── Electrical Circuit Tools ──────────────────────────────────────────────

    [McpServerTool(Name = "revit_get_electrical_circuits", ReadOnly = true),
     Description("Lists electrical circuits (systems) in the active Revit document. Filter by panelName, circuitNumber, systemType (e.g. PowerCircuit). Options: includeElements (bool), includeParameters (bool), limit (int).")]
    public async Task<string> GetElectricalCircuits(
        [Description("Optional panel name filter (partial match)")] string? panelName = null,
        [Description("Optional circuit number filter (partial match)")] string? circuitNumber = null,
        [Description("Optional system type filter (e.g. PowerCircuit, Data, FireAlarm)")] string? systemType = null,
        [Description("Include connected elements per circuit. Heavy on large models; opt-in. Default false.")] bool includeElements = false,
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
     Description("Creates a new electrical circuit. Requires approval. Source: useSelection, elementIds, or category+filters. Optional: systemType (PowerCircuit/Data/FireAlarm/etc), panelElementId, panelName, wireTypeName. For a family with several electrical connectors (e.g. 2xRJ45), pass ONE element id plus connectorId to circuit a specific connector.")]
    public async Task<string> CreateElectricalCircuit(
        [Description("If true, use current Revit selection")] bool useSelection = false,
        [Description("Explicit element IDs to add")] long[]? elementIds = null,
        [Description("Category name for query")] string? category = null,
        [Description("JSON array of parameter filters")] string? filters = null,
        [Description("Electrical system type (default PowerCircuit)")] string systemType = "PowerCircuit",
        [Description("Panel element ID (preferred over panelName)")] long panelElementId = 0,
        [Description("Panel name (fallback if panelElementId not provided)")] string? panelName = null,
        [Description("Wire type name to assign to the new circuit")] string? wireTypeName = null,
        [Description("Specific electrical connector id on a single element (for multi-connector families); 0 = let Revit choose")] int connectorId = 0,
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
            ["connectorId"] = connectorId,
            ["limit"] = limit
        };
        var result = await pipeClient.SendAsync("revit_create_electrical_circuit", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_preview_assign_data_devices_to_patch_panels", ReadOnly = true),
     Description("Read-only preview of bulk data-device → patch-panel circuit assignment: sorts data devices clockwise around the floor, applies per-type connector rules (default '1 x RJ45'=1, '2 x RJ45'=2), and plans one Data circuit per connector onto the given panels without exceeding each panel's 'Maximum Amount of Circuits'. Returns the full plan, per-panel utilization and validation report. Run before revit_assign_data_devices_to_patch_panels.")]
    public async Task<string> PreviewAssignDataDevicesToPatchPanels(
        [Description("Level name to collect Data Devices from (e.g. '10.korrus'); ignored when elementIds given")] string? levelName = null,
        [Description("Explicit data device element IDs (overrides levelName)")] long[]? elementIds = null,
        [Description("Target panel element IDs, in allocation order")] long[]? panelElementIds = null,
        [Description("Target panel names, in allocation order (e.g. ['FD10.1-01','FD10.1-02'])")] string[]? panelNames = null,
        [Description("Electrical system type (default Data)")] string systemType = "Data",
        [Description("Device route order (default ClockwisePerimeter)")] string routeMode = "ClockwisePerimeter",
        [Description("Route start corner: TopLeft|TopRight|BottomRight|BottomLeft (default TopLeft)")] string startCorner = "TopLeft",
        [Description("Override every panel's capacity; 0 = read each panel's 'Maximum Amount of Circuits' parameter")] int maxCircuitsPerPanel = 0,
        [Description("Keep all circuits of one device on the same panel (default true)")] bool keepDeviceConnectorsTogether = true,
        [Description("JSON array of {typeNameRegex, connectorsToUse}; default RJ45 rules")] string? connectorRules = null,
        [Description("Treat already-circuited connectors as satisfying the rule quota (default true; makes reruns idempotent)")] bool skipAlreadyCircuitedConnectors = true,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseJsonArray(connectorRules, "connectorRules", out var parsedRules, out var rulesError))
            return FormatBridgeError(rulesError!);

        var args = new Dictionary<string, object?>
        {
            ["levelName"] = levelName ?? string.Empty,
            ["elementIds"] = elementIds ?? [],
            ["panelElementIds"] = panelElementIds ?? [],
            ["panelNames"] = panelNames ?? [],
            ["systemType"] = systemType,
            ["routeMode"] = routeMode,
            ["startCorner"] = startCorner,
            ["maxCircuitsPerPanel"] = maxCircuitsPerPanel,
            ["keepDeviceConnectorsTogether"] = keepDeviceConnectorsTogether,
            ["connectorRules"] = parsedRules,
            ["skipAlreadyCircuitedConnectors"] = skipAlreadyCircuitedConnectors
        };
        var result = await pipeClient.SendAsync("revit_preview_assign_data_devices_to_patch_panels", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_assign_data_devices_to_patch_panels"),
     Description("Executes the data-device → patch-panel assignment previewed by revit_preview_assign_data_devices_to_patch_panels (pass the same arguments — the plan is rebuilt and re-validated; reruns never duplicate existing circuits). dryRun=true (default) only reports; atomic=true (default) rolls back everything on any failure. Requires approval in Revit.")]
    public async Task<string> AssignDataDevicesToPatchPanels(
        [Description("Level name to collect Data Devices from (e.g. '10.korrus'); ignored when elementIds given")] string? levelName = null,
        [Description("Explicit data device element IDs (overrides levelName)")] long[]? elementIds = null,
        [Description("Target panel element IDs, in allocation order")] long[]? panelElementIds = null,
        [Description("Target panel names, in allocation order (e.g. ['FD10.1-01','FD10.1-02'])")] string[]? panelNames = null,
        [Description("Electrical system type (default Data)")] string systemType = "Data",
        [Description("Device route order (default ClockwisePerimeter)")] string routeMode = "ClockwisePerimeter",
        [Description("Route start corner: TopLeft|TopRight|BottomRight|BottomLeft (default TopLeft)")] string startCorner = "TopLeft",
        [Description("Override every panel's capacity; 0 = read each panel's 'Maximum Amount of Circuits' parameter")] int maxCircuitsPerPanel = 0,
        [Description("Keep all circuits of one device on the same panel (default true)")] bool keepDeviceConnectorsTogether = true,
        [Description("JSON array of {typeNameRegex, connectorsToUse}; default RJ45 rules")] string? connectorRules = null,
        [Description("Treat already-circuited connectors as satisfying the rule quota (default true; makes reruns idempotent)")] bool skipAlreadyCircuitedConnectors = true,
        [Description("If true, roll back the entire operation when any device fails (default true)")] bool atomic = true,
        [Description("If true (default), make no model changes — report what would happen")] bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseJsonArray(connectorRules, "connectorRules", out var parsedRules, out var rulesError))
            return FormatBridgeError(rulesError!);

        var args = new Dictionary<string, object?>
        {
            ["levelName"] = levelName ?? string.Empty,
            ["elementIds"] = elementIds ?? [],
            ["panelElementIds"] = panelElementIds ?? [],
            ["panelNames"] = panelNames ?? [],
            ["systemType"] = systemType,
            ["routeMode"] = routeMode,
            ["startCorner"] = startCorner,
            ["maxCircuitsPerPanel"] = maxCircuitsPerPanel,
            ["keepDeviceConnectorsTogether"] = keepDeviceConnectorsTogether,
            ["connectorRules"] = parsedRules,
            ["skipAlreadyCircuitedConnectors"] = skipAlreadyCircuitedConnectors,
            ["atomic"] = atomic,
            ["dryRun"] = dryRun
        };
        var result = await pipeClient.SendAsync("revit_assign_data_devices_to_patch_panels", args, cancellationToken);
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

    [McpServerTool(Name = "revit_select_elements_by_panel"),
     Description("Selects every element connected to any circuit assigned to a panel, in one operation — the fast path for \"select all elements on panel X\" instead of calling revit_select_circuit_elements once per circuit. Requires panelName or panelElementId.")]
    public async Task<string> SelectElementsByPanel(
        [Description("Panel name (partial match; errors if ambiguous). Provide this or panelElementId.")] string? panelName = null,
        [Description("Panel element ID. Provide this or panelName.")] long panelElementId = 0,
        [Description("Optional system type filter, e.g. 'PowerCircuit'")] string? systemType = null,
        [Description("Replace current selection (default true)")] bool replaceSelection = true,
        [Description("Zoom to selection after selecting (default false)")] bool zoomToSelection = false,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["panelName"] = panelName ?? string.Empty,
            ["panelElementId"] = panelElementId,
            ["systemType"] = systemType ?? string.Empty,
            ["replaceSelection"] = replaceSelection,
            ["zoomToSelection"] = zoomToSelection
        };
        var result = await pipeClient.SendAsync("revit_select_elements_by_panel", args, cancellationToken);
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
     Description("Estimates cable length(s) by computing panel-to-element distances. Results are PRELIMINARY. Single circuit: pass circuitId (>0) — returns detail, supports includeElementBreakdown. Batch: omit circuitId and filter by panelName/systemType/circuitIds — returns one row per circuit. method: StraightLineMax, StraightLineSum, ManhattanMax (default), ManhattanSum, NearestNeighborPath.")]
    public async Task<string> EstimateCircuitLength(
        [Description("Single mode: circuit element ID (>0). Omit/0 for batch mode.")] long circuitId = 0,
        [Description("Length estimation method (default ManhattanMax)")] string method = "ManhattanMax",
        [Description("Multiplier to account for routing overhead (default 1.25)")] double routingMultiplier = 1.25,
        [Description("Single mode only: include per-element distance breakdown")] bool includeElementBreakdown = false,
        [Description("Batch mode: panel name filter")] string? panelName = null,
        [Description("Batch mode: system type filter")] string? systemType = null,
        [Description("Batch mode: explicit circuit element IDs")] long[]? circuitIds = null,
        [Description("Batch mode: max circuits (default 1000)")] int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        if (circuitId > 0)
        {
            var singleArgs = new Dictionary<string, object?>
            {
                ["circuitId"] = circuitId,
                ["method"] = method,
                ["routingMultiplier"] = routingMultiplier,
                ["includeElementBreakdown"] = includeElementBreakdown
            };
            return FormatResult(await pipeClient.SendAsync("revit_estimate_circuit_length", singleArgs, cancellationToken));
        }
        var batchArgs = new Dictionary<string, object?>
        {
            ["panelName"] = panelName ?? string.Empty,
            ["systemType"] = systemType ?? string.Empty,
            ["circuitIds"] = circuitIds ?? [],
            ["method"] = method,
            ["routingMultiplier"] = routingMultiplier,
            ["limit"] = limit
        };
        return FormatResult(await pipeClient.SendAsync("revit_estimate_circuit_lengths", batchArgs, cancellationToken));
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

    [McpServerTool(Name = "revit_preview_duplicate", ReadOnly = true),
     Description("Previews view/sheet duplication WITHOUT changes. entity=sheets: sourceSheetIds or sourceSheetNumbers; newNumberSuffix (default _COPY), newNameSuffix (default ' - Copy'), keepTitleBlock, copyParameters. entity=views: viewIds; duplicateOption (Duplicate|DuplicateWithDetailing|AsDependent), nameSuffix, namePrefix.")]
    public async Task<string> PreviewDuplicate(
        [Description("What to duplicate: views | sheets")] string entity,
        [Description("sheets only: source sheet element IDs")] long[]? sourceSheetIds = null,
        [Description("sheets only: source sheet numbers")] string[]? sourceSheetNumbers = null,
        [Description("sheets only: suffix for new sheet number (default _COPY)")] string newNumberSuffix = "_COPY",
        [Description("sheets only: suffix for new sheet name (default ' - Copy')")] string newNameSuffix = " - Copy",
        [Description("sheets only: keep same title block (default true)")] bool keepTitleBlock = true,
        [Description("sheets only: copy instance parameters (default true)")] bool copyParameters = true,
        [Description("views only: view element IDs to duplicate")] long[]? viewIds = null,
        [Description("views only: Duplicate|DuplicateWithDetailing|AsDependent")] string duplicateOption = "DuplicateWithDetailing",
        [Description("views only: suffix for new view name (default ' - Copy')")] string nameSuffix = " - Copy",
        [Description("views only: prefix for new view name")] string namePrefix = "",
        CancellationToken cancellationToken = default)
    {
        var e = entity?.Trim().ToLowerInvariant();
        if (e == "sheets")
        {
            var sheetArgs = new Dictionary<string, object?>
            {
                ["sourceSheetIds"] = sourceSheetIds ?? [],
                ["sourceSheetNumbers"] = sourceSheetNumbers ?? [],
                ["newNumberSuffix"] = newNumberSuffix,
                ["newNameSuffix"] = newNameSuffix,
                ["keepTitleBlock"] = keepTitleBlock,
                ["copyParameters"] = copyParameters
            };
            return FormatResult(await pipeClient.SendAsync("revit_preview_duplicate_sheets", sheetArgs, cancellationToken));
        }
        if (e == "views")
        {
            if (viewIds == null || viewIds.Length == 0)
                return FormatBridgeError("entity=views requires viewIds.");
            var viewArgs = new Dictionary<string, object?>
            {
                ["viewIds"] = viewIds,
                ["duplicateOption"] = duplicateOption,
                ["nameSuffix"] = nameSuffix,
                ["namePrefix"] = namePrefix
            };
            return FormatResult(await pipeClient.SendAsync("revit_preview_duplicate_views", viewArgs, cancellationToken));
        }
        return FormatBridgeError($"Invalid entity '{entity}'. Expected: views or sheets.");
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

    [McpServerTool(Name = "revit_preview_rename", ReadOnly = true),
     Description("Previews view/sheet renames WITHOUT changes. mode=FindReplace|PrefixSuffix|Template|RegexFindReplace (params: find/replace, prefix/suffix, template with {Name}). entity=views: viewIds/viewTypes/nameFilter. entity=sheets: sheetIds/nameFilter/numberFilter + target (Name|Number|Both).")]
    public async Task<string> PreviewRename(
        [Description("What to rename: views | sheets")] string entity,
        [Description("Rename mode: FindReplace|PrefixSuffix|Template|RegexFindReplace")] string mode,
        [Description("views only: view element IDs")] long[]? viewIds = null,
        [Description("views only: filter by view types")] string[]? viewTypes = null,
        [Description("sheets only: sheet element IDs")] long[]? sheetIds = null,
        [Description("sheets only: target field Name|Number|Both (default Name)")] string target = "Name",
        [Description("sheets only: filter by number substring")] string? numberFilter = null,
        [Description("Filter by name substring")] string? nameFilter = null,
        [Description("Text to find (FindReplace/Regex modes)")] string? find = null,
        [Description("Replacement text")] string? replace = null,
        [Description("Prefix to add (PrefixSuffix mode)")] string? prefix = null,
        [Description("Suffix to add (PrefixSuffix mode)")] string? suffix = null,
        [Description("Template pattern using {Name} (Template mode)")] string? template = null,
        CancellationToken cancellationToken = default)
    {
        var e = entity?.Trim().ToLowerInvariant();
        if (e == "views")
        {
            var viewArgs = new Dictionary<string, object?>
            {
                ["mode"] = mode, ["viewIds"] = viewIds ?? [], ["viewTypes"] = viewTypes ?? [],
                ["nameFilter"] = nameFilter ?? "", ["find"] = find ?? "", ["replace"] = replace ?? "",
                ["prefix"] = prefix ?? "", ["suffix"] = suffix ?? "", ["template"] = template ?? ""
            };
            return FormatResult(await pipeClient.SendAsync("revit_preview_rename_views", viewArgs, cancellationToken));
        }
        if (e == "sheets")
        {
            var sheetArgs = new Dictionary<string, object?>
            {
                ["mode"] = mode, ["target"] = target, ["sheetIds"] = sheetIds ?? [],
                ["nameFilter"] = nameFilter ?? "", ["numberFilter"] = numberFilter ?? "",
                ["find"] = find ?? "", ["replace"] = replace ?? "",
                ["prefix"] = prefix ?? "", ["suffix"] = suffix ?? "", ["template"] = template ?? ""
            };
            return FormatResult(await pipeClient.SendAsync("revit_preview_rename_sheets", sheetArgs, cancellationToken));
        }
        return FormatBridgeError($"Invalid entity '{entity}'. Expected: views or sheets.");
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

    [McpServerTool(Name = "revit_duplicate"),
     Description("Duplicates views/sheets. Requires approval. Run revit_preview_duplicate first. entity=sheets: sourceSheetIds or sourceSheetNumbers (empty shell, same titleblock + copied params). entity=views: viewIds; duplicateOption, nameSuffix, namePrefix.")]
    public async Task<string> Duplicate(
        [Description("What to duplicate: views | sheets")] string entity,
        [Description("sheets only: source sheet element IDs")] long[]? sourceSheetIds = null,
        [Description("sheets only: source sheet numbers")] string[]? sourceSheetNumbers = null,
        [Description("sheets only: suffix for new sheet number (default _COPY)")] string newNumberSuffix = "_COPY",
        [Description("sheets only: suffix for new sheet name (default ' - Copy')")] string newNameSuffix = " - Copy",
        [Description("sheets only: keep same title block (default true)")] bool keepTitleBlock = true,
        [Description("sheets only: copy instance parameters (default true)")] bool copyParameters = true,
        [Description("views only: view element IDs to duplicate")] long[]? viewIds = null,
        [Description("views only: Duplicate|DuplicateWithDetailing|AsDependent")] string duplicateOption = "DuplicateWithDetailing",
        [Description("views only: suffix for new view name (default ' - Copy')")] string nameSuffix = " - Copy",
        [Description("views only: prefix for new view name")] string namePrefix = "",
        CancellationToken cancellationToken = default)
    {
        var e = entity?.Trim().ToLowerInvariant();
        if (e == "sheets")
        {
            var sheetArgs = new Dictionary<string, object?>
            {
                ["sourceSheetIds"] = sourceSheetIds ?? [], ["sourceSheetNumbers"] = sourceSheetNumbers ?? [],
                ["newNumberSuffix"] = newNumberSuffix, ["newNameSuffix"] = newNameSuffix,
                ["keepTitleBlock"] = keepTitleBlock, ["copyParameters"] = copyParameters
            };
            return FormatResult(await pipeClient.SendAsync("revit_duplicate_sheets", sheetArgs, cancellationToken));
        }
        if (e == "views")
        {
            if (viewIds == null || viewIds.Length == 0)
                return FormatBridgeError("entity=views requires viewIds.");
            var viewArgs = new Dictionary<string, object?>
            {
                ["viewIds"] = viewIds, ["duplicateOption"] = duplicateOption,
                ["nameSuffix"] = nameSuffix, ["namePrefix"] = namePrefix
            };
            return FormatResult(await pipeClient.SendAsync("revit_duplicate_views", viewArgs, cancellationToken));
        }
        return FormatBridgeError($"Invalid entity '{entity}'. Expected: views or sheets.");
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

    [McpServerTool(Name = "revit_set_parameters_bulk"),
     Description("Sets parameters on multiple views/sheets in one transaction. Requires approval. parameters = name→value map (e.g. {\"Märkus\": \"Rev A\"}). entity=views: viewIds or viewTypes+nameFilter, includeTemplates, limit. entity=sheets: sheetIds or sheetNumbers, nameFilter.")]
    public async Task<string> SetParametersBulk(
        [Description("What to update: views | sheets")] string entity,
        [Description("Parameter name→value map")] object? parameters = null,
        [Description("views only: view element IDs")] long[]? viewIds = null,
        [Description("views only: filter by view types")] string[]? viewTypes = null,
        [Description("views only: include view templates (default false)")] bool includeTemplates = false,
        [Description("views only: max views to update (default 500)")] int limit = 500,
        [Description("sheets only: sheet element IDs")] long[]? sheetIds = null,
        [Description("sheets only: sheet numbers")] string[]? sheetNumbers = null,
        [Description("Filter by name substring")] string? nameFilter = null,
        CancellationToken cancellationToken = default)
    {
        var e = entity?.Trim().ToLowerInvariant();
        if (e == "views")
        {
            var viewArgs = new Dictionary<string, object?>
            {
                ["viewIds"] = viewIds ?? [], ["viewTypes"] = viewTypes ?? [],
                ["nameFilter"] = nameFilter ?? "", ["includeTemplates"] = includeTemplates,
                ["limit"] = limit, ["parameters"] = ToJToken(parameters)
            };
            return FormatResult(await pipeClient.SendAsync("revit_set_view_parameters_bulk", viewArgs, cancellationToken));
        }
        if (e == "sheets")
        {
            var sheetArgs = new Dictionary<string, object?>
            {
                ["sheetIds"] = sheetIds ?? [], ["sheetNumbers"] = sheetNumbers ?? [],
                ["nameFilter"] = nameFilter ?? "", ["parameters"] = ToJToken(parameters)
            };
            return FormatResult(await pipeClient.SendAsync("revit_set_sheet_parameters_bulk", sheetArgs, cancellationToken));
        }
        return FormatBridgeError($"Invalid entity '{entity}'. Expected: views or sheets.");
    }

    [McpServerTool(Name = "revit_rename"),
     Description("Renames views/sheets. Requires approval. Run revit_preview_rename first. mode=FindReplace|PrefixSuffix|Template|RegexFindReplace. entity=views: viewIds or viewTypes+nameFilter. entity=sheets: target (Name|Number|Both), sheetIds/nameFilter/numberFilter.")]
    public async Task<string> Rename(
        [Description("What to rename: views | sheets")] string entity,
        [Description("Rename mode")] string mode,
        [Description("views only: view element IDs")] long[]? viewIds = null,
        [Description("views only: filter by view types")] string[]? viewTypes = null,
        [Description("sheets only: sheet element IDs")] long[]? sheetIds = null,
        [Description("sheets only: target field Name|Number|Both (default Name)")] string target = "Name",
        [Description("sheets only: filter by number substring")] string? numberFilter = null,
        [Description("Filter by name substring")] string? nameFilter = null,
        [Description("Find text (FindReplace/Regex)")] string? find = null,
        [Description("Replace text")] string? replace = null,
        [Description("Prefix (PrefixSuffix)")] string? prefix = null,
        [Description("Suffix (PrefixSuffix)")] string? suffix = null,
        [Description("Template with {Name} (Template)")] string? template = null,
        CancellationToken cancellationToken = default)
    {
        var e = entity?.Trim().ToLowerInvariant();
        if (e == "views")
        {
            var viewArgs = new Dictionary<string, object?>
            {
                ["mode"] = mode, ["viewIds"] = viewIds ?? [], ["viewTypes"] = viewTypes ?? [],
                ["nameFilter"] = nameFilter ?? "", ["find"] = find ?? "", ["replace"] = replace ?? "",
                ["prefix"] = prefix ?? "", ["suffix"] = suffix ?? "", ["template"] = template ?? ""
            };
            return FormatResult(await pipeClient.SendAsync("revit_rename_views", viewArgs, cancellationToken));
        }
        if (e == "sheets")
        {
            var sheetArgs = new Dictionary<string, object?>
            {
                ["mode"] = mode, ["target"] = target, ["sheetIds"] = sheetIds ?? [],
                ["nameFilter"] = nameFilter ?? "", ["numberFilter"] = numberFilter ?? "",
                ["find"] = find ?? "", ["replace"] = replace ?? "",
                ["prefix"] = prefix ?? "", ["suffix"] = suffix ?? "", ["template"] = template ?? ""
            };
            return FormatResult(await pipeClient.SendAsync("revit_rename_sheets", sheetArgs, cancellationToken));
        }
        return FormatBridgeError($"Invalid entity '{entity}'. Expected: views or sheets.");
    }

    // ── View / Sheet / Documentation — Phase 4 Destructive ───────────────────

    [McpServerTool(Name = "revit_preview_delete", ReadOnly = true),
     Description("Previews which views/sheets/elements would be deleted WITHOUT changes. target=views: viewIds or viewTypes+nameFilter, skipPlacedOnSheets (default true). target=sheets: sheetIds/sheetNumbers/nameFilter, skipSheetsWithViews (default true). target=elements: elementIds, skipPinned (default true) — dependent elements are deleted too.")]
    public async Task<string> PreviewDelete(
        [Description("What to preview: views | sheets | elements")] string target,
        [Description("views only: view element IDs")] long[]? viewIds = null,
        [Description("views only: filter by view types")] string[]? viewTypes = null,
        [Description("sheets only: sheet element IDs")] long[]? sheetIds = null,
        [Description("sheets only: sheet numbers")] string[]? sheetNumbers = null,
        [Description("Filter by name substring")] string? nameFilter = null,
        [Description("views only: skip views placed on sheets (default true)")] bool skipPlacedOnSheets = true,
        [Description("sheets only: skip sheets with placed views (default true)")] bool skipSheetsWithViews = true,
        [Description("elements only: model element IDs")] long[]? elementIds = null,
        [Description("elements only: skip pinned elements (default true)")] bool skipPinned = true,
        CancellationToken cancellationToken = default)
    {
        var t = target?.Trim().ToLowerInvariant();
        if (t == "views")
        {
            var viewArgs = new Dictionary<string, object?>
            {
                ["viewIds"] = viewIds ?? [], ["viewTypes"] = viewTypes ?? [],
                ["nameFilter"] = nameFilter ?? "", ["skipPlacedOnSheets"] = skipPlacedOnSheets
            };
            return FormatResult(await pipeClient.SendAsync("revit_preview_delete_views", viewArgs, cancellationToken));
        }
        if (t == "sheets")
        {
            var sheetArgs = new Dictionary<string, object?>
            {
                ["sheetIds"] = sheetIds ?? [], ["sheetNumbers"] = sheetNumbers ?? [],
                ["nameFilter"] = nameFilter ?? "", ["skipSheetsWithViews"] = skipSheetsWithViews
            };
            return FormatResult(await pipeClient.SendAsync("revit_preview_delete_sheets", sheetArgs, cancellationToken));
        }
        if (t == "elements")
        {
            var elementArgs = new Dictionary<string, object?>
            {
                ["elementIds"] = elementIds ?? [], ["skipPinned"] = skipPinned
            };
            return FormatResult(await pipeClient.SendAsync("revit_preview_delete_elements", elementArgs, cancellationToken));
        }
        return FormatBridgeError($"Invalid target '{target}'. Expected: views, sheets, or elements.");
    }

    [McpServerTool(Name = "revit_delete"),
     Description("DESTRUCTIVE: permanently deletes views/sheets/elements. Always requires manual approval — cannot be bypassed by Direct Edit. Run revit_preview_delete first. target=views: viewIds required. target=sheets: sheetIds or sheetNumbers required. target=elements: elementIds required — dependent elements (tags, dimensions, hosted elements) are deleted too.")]
    public async Task<string> Delete(
        [Description("What to delete: views | sheets | elements")] string target,
        [Description("views only: view element IDs to delete")] long[]? viewIds = null,
        [Description("sheets only: sheet element IDs to delete")] long[]? sheetIds = null,
        [Description("sheets only: sheet numbers to delete")] string[]? sheetNumbers = null,
        [Description("views only: skip views placed on sheets (default true)")] bool skipPlacedOnSheets = true,
        [Description("sheets only: skip sheets with placed views (default true)")] bool skipSheetsWithViews = true,
        [Description("elements only: model element IDs to delete")] long[]? elementIds = null,
        [Description("elements only: skip pinned elements (default true)")] bool skipPinned = true,
        CancellationToken cancellationToken = default)
    {
        var t = target?.Trim().ToLowerInvariant();
        if (t == "views")
        {
            if (viewIds == null || viewIds.Length == 0)
                return FormatBridgeError("target=views requires viewIds.");
            var viewArgs = new Dictionary<string, object?>
            {
                ["viewIds"] = viewIds, ["skipPlacedOnSheets"] = skipPlacedOnSheets
            };
            return FormatResult(await pipeClient.SendAsync("revit_delete_views", viewArgs, cancellationToken));
        }
        if (t == "sheets")
        {
            var sheetArgs = new Dictionary<string, object?>
            {
                ["sheetIds"] = sheetIds ?? [], ["sheetNumbers"] = sheetNumbers ?? [],
                ["skipSheetsWithViews"] = skipSheetsWithViews
            };
            return FormatResult(await pipeClient.SendAsync("revit_delete_sheets", sheetArgs, cancellationToken));
        }
        if (t == "elements")
        {
            if (elementIds == null || elementIds.Length == 0)
                return FormatBridgeError("target=elements requires elementIds.");
            var elementArgs = new Dictionary<string, object?>
            {
                ["elementIds"] = elementIds, ["skipPinned"] = skipPinned
            };
            return FormatResult(await pipeClient.SendAsync("revit_delete_elements", elementArgs, cancellationToken));
        }
        return FormatBridgeError($"Invalid target '{target}'. Expected: views, sheets, or elements.");
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

    [McpServerTool(Name = "revit_detect_clashes", ReadOnly = true),
     Description("Detects clashes between two category sets. mode=hard: physical solid intersection (Confidence=High; set allowBoundingBoxFallback for low-confidence bbox results; toleranceMm). mode=clearance: expanded-bbox clearance violations (clearanceMm; conservative estimates, review visually). Saves as last run by default.")]
    public async Task<string> DetectClashes(
        [Description("Detection mode: hard | clearance")] string mode,
        [Description("Source element categories")] string[] sourceCategories,
        [Description("Target element categories")] string[] targetCategories,
        [Description("clearance only: required clearance in mm (default 50)")] double clearanceMm = 50,
        [Description("hard only: min intersection volume tolerance in mm³ (default 5)")] double toleranceMm = 5,
        [Description("hard only: allow low-confidence bbox fallback when solids fail. Default false.")] bool allowBoundingBoxFallback = false,
        [Description("Include linked models (default true)")] bool includeLinks = true,
        [Description("Include Generic Models (default true)")] bool includeGenericModels = true,
        [Description("Include imported geometry (default true)")] bool includeImportedGeometry = true,
        [Description("Filter by link name substrings")] string[]? linkNameFilters = null,
        [Description("Maximum clashes to return (default 1000)")] int limit = 1000,
        [Description("Stop after this many element pairs (default 100000)")] int maxPairs = 100000,
        [Description("Save as last run for navigation tools (default true)")] bool saveAsLastRun = true,
        [Description("Rule name label. Empty = default per mode.")] string? ruleName = null,
        [Description("Severity: Low | Medium | High | Critical (default Medium)")] string severity = "Medium",
        CancellationToken cancellationToken = default)
    {
        var m = mode?.Trim().ToLowerInvariant();
        if (m != "hard" && m != "clearance")
            return FormatBridgeError($"Invalid mode '{mode}'. Expected: hard or clearance.");

        var args = new Dictionary<string, object?>
        {
            ["sourceCategories"] = sourceCategories, ["targetCategories"] = targetCategories,
            ["includeLinks"] = includeLinks, ["includeGenericModels"] = includeGenericModels,
            ["includeImportedGeometry"] = includeImportedGeometry,
            ["linkNameFilters"] = linkNameFilters ?? [], ["limit"] = limit,
            ["maxPairs"] = maxPairs, ["saveAsLastRun"] = saveAsLastRun,
            ["severity"] = severity
        };

        string toolName;
        if (m == "hard")
        {
            toolName = "revit_detect_hard_clashes";
            args["toleranceMm"] = toleranceMm;
            args["allowBoundingBoxFallback"] = allowBoundingBoxFallback;
            args["ruleName"] = string.IsNullOrWhiteSpace(ruleName) ? "Ad-hoc Hard Clash" : ruleName;
        }
        else
        {
            toolName = "revit_detect_clearance_clashes";
            args["clearanceMm"] = clearanceMm;
            args["ruleName"] = string.IsNullOrWhiteSpace(ruleName) ? "Ad-hoc Clearance" : ruleName;
        }

        var result = await pipeClient.SendAsync(toolName, args, cancellationToken);
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

    [McpServerTool(Name = "revit_get_adjacent_clash", ReadOnly = true),
     Description("Navigates to the next/previous clash in the last run result. direction=next|previous. Returns clash details and position; wraps around.")]
    public async Task<string> GetAdjacentClash(
        [Description("Direction: next | previous")] string direction,
        CancellationToken cancellationToken = default)
    {
        var d = direction?.Trim().ToLowerInvariant();
        var toolName = d switch
        {
            "next" => "revit_get_next_clash",
            "previous" or "prev" => "revit_get_previous_clash",
            _ => null
        };
        if (toolName == null)
            return FormatBridgeError($"Invalid direction '{direction}'. Expected: next or previous.");

        var result = await pipeClient.SendAsync(toolName, [], cancellationToken);
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

    // ── Skill Builder — guided skill creation (issue #20) ────────────────────

    [McpServerTool(Name = "revit_skill_builder_guide", ReadOnly = true),
     Description("START HERE when the user wants to create (or design) a new Revit skill. " +
                 "Returns the guided interview workflow: how to question the user, compose the skill from task building blocks, create it, and explain activation. " +
                 "No Revit connection needed — this is static guidance.")]
    public string SkillBuilderGuide() => """
        # Skill Builder — guided workflow for creating a new skill

        A skill is a JSON recipe (.skill.json) that chains prebuilt task blocks — validations,
        scans, comparisons, report exports — into a repeatable workflow that runs inside Revit
        via revit_run_skill. Skills are composed ONLY from the existing task catalog; new task
        types require C# code and cannot be created here.

        Follow these steps with the user:

        1. UNDERSTAND THE GOAL. Ask the user to describe, at a high level, what they want the
           skill to do and what result they expect (a report? a validation? applied changes?).
           Restate the goal back in your own words.

        2. LEARN THE BUILDING BLOCKS. Call revit_list_skill_tasks (task catalog with example
           settings) and revit_list_skills (existing skills). If an existing skill already
           covers the goal, suggest running it — or a project override via
           revit_manage_project_skill_override — instead of creating a duplicate.

        3. INTERVIEW THE USER. Ask focused follow-up questions, a few at a time, and repeat
           until every required setting has a confirmed value. Typical topics: which checks or
           tasks to include and their order; file paths (delivery folder, Excel register);
           parameter names and allowed values; report outputs (Excel report, HTML dashboard)
           and their titles; whether the skill may modify the model
           (requiresUserConfirmationBeforeModelChanges). Do NOT guess values the user can
           answer — keep asking until nothing important is ambiguous.

        4. DRAFT AND CONFIRM. Present a readable summary of the proposed skill (name,
           description, ordered tasks with their settings) in the USER'S language and ask for
           confirmation before creating anything.

        5. CREATE. Call revit_create_skill. IMPORTANT: author ALL skill content — id, name,
           description, setting values — in ENGLISH, even when the conversation is in Estonian
           or any other language. Recommended id pattern: 'user.<area>.<short-name>'
           (e.g. 'user.delivery.pdf-check'). The call requires approval in the Revit MCP window.

        6. ANNOUNCE ACTIVATION. Tell the user (in their language) that the skill was created
           and how to activate it: ask "Run skill '<name>'" — which maps to revit_run_skill
           with skillId='<id>' — and recommend a dry run first via revit_preview_skill_run.

        7. EDITING LATER. The user can change the skill at any time: fetch the current
           definition with revit_get_skill_details, interview for the changes, then call
           revit_update_skill (tasks are a FULL replacement). Company master skills cannot be
           edited this way — use revit_manage_project_skill_override instead.
        """;

    [McpServerTool(Name = "revit_list_skill_tasks", ReadOnly = true),
     Description("Lists all available skill task building blocks for composing skills: id, name, changesModel, " +
                 "exampleSettings (from an existing skill that uses the task), usedBySkills. " +
                 "Use with revit_create_skill / revit_update_skill.")]
    public async Task<string> ListSkillTasks(CancellationToken cancellationToken = default)
    {
        var result = await pipeClient.SendAsync("revit_list_skill_tasks", new Dictionary<string, object?>(), cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_create_skill"),
     Description("Creates a new skill (.skill.json) in the skill library from task building blocks. Requires approval. " +
                 "Follow revit_skill_builder_guide first and author ALL content in ENGLISH regardless of conversation language. " +
                 "Required: skillId (lowercase, e.g. 'user.delivery.pdf-check'), name, description, tasks (array of {id, enabled, settings}). " +
                 "Task ids come from revit_list_skill_tasks.")]
    public async Task<string> CreateSkill(
        [Description("Unique skill id — lowercase letters/digits separated by '.', '-' or '_'; recommended prefix 'user.'")] string skillId,
        [Description("Human-readable skill name (English)")] string name,
        [Description("What the skill does and when to use it (English)")] string description,
        [Description("Ordered task array: objects with id (required), enabled (default true), settings (object)")] object[] tasks,
        [Description("Semantic version (default 1.0.0)")] string? version = null,
        [Description("Author name (default 'RevitMCP Skill Builder')")] string? author = null,
        [Description("Stop the run when a task fails critically (default false)")] bool stopOnCriticalFailure = false,
        [Description("Ask for user confirmation before model-changing tasks (default true)")] bool requiresUserConfirmationBeforeModelChanges = true,
        [Description("Replace an existing non-master skill with the same id (default false)")] bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["skillId"] = skillId,
            ["name"] = name,
            ["description"] = description,
            ["tasks"] = ToJToken(tasks),
            ["version"] = version ?? string.Empty,
            ["author"] = author ?? string.Empty,
            ["stopOnCriticalFailure"] = stopOnCriticalFailure,
            ["requiresUserConfirmationBeforeModelChanges"] = requiresUserConfirmationBeforeModelChanges,
            ["overwrite"] = overwrite
        };
        var result = await pipeClient.SendAsync("revit_create_skill", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "revit_update_skill"),
     Description("Updates an existing user-created skill. Requires approval. Only provided fields change; tasks is a FULL replacement. " +
                 "Author ALL content in ENGLISH. Company master skills are protected — use revit_manage_project_skill_override instead. " +
                 "Fetch the current definition with revit_get_skill_details first.")]
    public async Task<string> UpdateSkill(
        [Description("ID of the skill to update")] string skillId,
        [Description("New name (omit to keep)")] string? name = null,
        [Description("New description (omit to keep)")] string? description = null,
        [Description("New version (omit to keep)")] string? version = null,
        [Description("New author (omit to keep)")] string? author = null,
        [Description("FULL replacement task array: objects with id, enabled, settings (omit to keep)")] object[]? tasks = null,
        [Description("Stop the run when a task fails critically (omit to keep)")] bool? stopOnCriticalFailure = null,
        [Description("Ask for user confirmation before model-changing tasks (omit to keep)")] bool? requiresUserConfirmationBeforeModelChanges = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["skillId"] = skillId };
        if (!string.IsNullOrWhiteSpace(name)) args["name"] = name;
        if (!string.IsNullOrWhiteSpace(description)) args["description"] = description;
        if (!string.IsNullOrWhiteSpace(version)) args["version"] = version;
        if (!string.IsNullOrWhiteSpace(author)) args["author"] = author;
        if (tasks is not null) args["tasks"] = ToJToken(tasks);
        if (stopOnCriticalFailure is not null) args["stopOnCriticalFailure"] = stopOnCriticalFailure.Value;
        if (requiresUserConfirmationBeforeModelChanges is not null) args["requiresUserConfirmationBeforeModelChanges"] = requiresUserConfirmationBeforeModelChanges.Value;
        var result = await pipeClient.SendAsync("revit_update_skill", args, cancellationToken);
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

    [McpServerTool(Name = "revit_manage_project_skill_override", ReadOnly = true),
     Description("Manages a project-specific override for a company skill. action=create (new override; supports projectName, changesJson, note), update (merge changesJson into existing; supports note), reset (delete override, revert to master). changesJson structure: {\"tasks\":{\"<taskId>\":{\"enabled\":true,\"settings\":{\"clearanceMm\":100}}}}.")]
    public async Task<string> ManageProjectSkillOverride(
        [Description("Action: create | update | reset")] string action,
        [Description("ID of the skill to override")] string skillId,
        [Description("Project identifier (e.g. job number)")] string projectId,
        [Description("create only: human-readable project name")] string? projectName = null,
        [Description("create/update only: JSON string of override data (tasks + settings)")] string? changesJson = null,
        [Description("create/update only: optional note describing the override")] string? note = null,
        CancellationToken cancellationToken = default)
    {
        var a = action?.Trim().ToLowerInvariant();
        string toolName;
        Dictionary<string, object?> args;
        switch (a)
        {
            case "create":
                toolName = "revit_create_project_skill_override";
                args = new Dictionary<string, object?>
                {
                    ["skillId"] = skillId,
                    ["projectId"] = projectId,
                    ["projectName"] = projectName ?? string.Empty,
                    ["changesJson"] = changesJson ?? "{}",
                    ["note"] = note ?? string.Empty
                };
                break;
            case "update":
                toolName = "revit_update_project_skill_override";
                args = new Dictionary<string, object?>
                {
                    ["skillId"] = skillId,
                    ["projectId"] = projectId,
                    ["changesJson"] = changesJson ?? "{}",
                    ["note"] = note ?? string.Empty
                };
                break;
            case "reset":
                toolName = "revit_reset_project_skill_override";
                args = new Dictionary<string, object?> { ["skillId"] = skillId, ["projectId"] = projectId };
                break;
            default:
                return FormatBridgeError($"Invalid action '{action}'. Expected: create, update, or reset.");
        }

        var result = await pipeClient.SendAsync(toolName, args, cancellationToken);
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

    // ── IFC Space to Room — Phase 1 (read-only) ───────────────────────────────

    [McpServerTool(Name = "ifc_list_links", ReadOnly = true),
     Description(
         "Lists all Revit link instances in the active document and identifies which are likely " +
         "derived from an IFC model (based on name/path heuristics). " +
         "Returns linkInstanceId values that can be passed to ifc_preview_spaces. " +
         "Phase 1: read-only.")]
    public async Task<string> IfcListLinks(
        [Description("If true, include all Revit links regardless of IFC heuristics. Default false.")]
        bool includeAllRevitLinks = false,
        [Description("If true, include links that are currently unloaded. Default false.")]
        bool includeUnloaded = false,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["includeAllRevitLinks"] = includeAllRevitLinks,
            ["includeUnloaded"]      = includeUnloaded
        };
        var result = await pipeClient.SendAsync("ifc_list_links", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "ifc_preview_spaces", ReadOnly = true),
     Description(
         "Inspects a linked IFC model and returns all detected IFC Space candidates with metadata " +
         "(GUID, number, name, building storey), the nearest host Level match by elevation, an optional " +
         "comparison against existing Rooms in the host document (using Level + Number + Name only — " +
         "no shared parameters), and a per-space conversion readiness status. " +
         "By default only elements with a confirmed IfcSpace type parameter are returned; " +
         "set includeProbable=true to also include elements with only generic IFC-origin markers " +
         "(IfcGUID, IfcName, etc.) — these are flagged detectionConfidence='Probable' and " +
         "canConvertLater=false. " +
         "Call ifc_list_links first to get a valid linkInstanceId. " +
         "Phase 1: strictly read-only — does not create rooms, room separation lines, shared parameters, " +
         "or any other model elements.")]
    public async Task<string> IfcPreviewSpaces(
        [Description("Element id of the RevitLinkInstance to inspect (integer). Obtain from ifc_list_links.")]
        long linkInstanceId,
        [Description("If true, compare each candidate against existing Rooms using Level + Number + Name. Default true.")]
        bool includeExistingRoomCheck = true,
        [Description("Maximum acceptable vertical offset between a space bottom elevation and the matched Level, in millimetres. Default 300.")]
        double levelMatchToleranceMm = 300.0,
        [Description("Maximum number of space candidates to return. Default 1000.")]
        int maxResults = 1000,
        [Description("If true, also include elements with only generic IFC-origin markers as probable IFC Space candidates. They are flagged detectionConfidence='Probable' and canConvertLater=false. Default false.")]
        bool includeProbable = false,
        [Description("Highest-priority Room Name parameter. Effective project default: AR_Ruum.100_Nimi.")] string? roomNameParameter = null,
        [Description("Highest-priority Room Number parameter. Effective project default: AR_Ruum.105_Number.")] string? roomNumberParameter = null,
        [Description("Highest-priority storey parameter. Effective project default: IfcDecomposes.")] string? storeyParameter = null,
        [Description("Highest-priority validation area parameter. Effective project default: AR_Ruum.120_Pindala.")] string? areaParameter = null,
        [Description("Enable AR_Ruum project defaults before legacy fallbacks. Default true.")] bool enableArRuumDefaults = true,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["linkInstanceId"]           = linkInstanceId,
            ["includeExistingRoomCheck"] = includeExistingRoomCheck,
            ["levelMatchToleranceMm"]    = levelMatchToleranceMm,
            ["maxResults"]               = maxResults,
            ["includeProbable"]          = includeProbable,
            ["roomNameParameter"]        = roomNameParameter,
            ["roomNumberParameter"]      = roomNumberParameter,
            ["storeyParameter"]          = storeyParameter,
            ["areaParameter"]            = areaParameter,
            ["enableArRuumDefaults"]     = enableArRuumDefaults
        };
        var result = await pipeClient.SendAsync("ifc_preview_spaces", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "ifc_preview_space_geometry", ReadOnly = true),
     Description(
         "Extracts geometry from linked IFC Space elements (Generic Models / DirectShapes) and " +
         "returns per-space geometry readiness results: solid detection, bottom horizontal face " +
         "selection, footprint CurveLoop extraction, approximate area in m², and an interior " +
         "placement point for later Room placement. " +
         "If linkedElementIds is omitted, all IFC Space candidates in the link are processed. " +
         "Set includeLoopCoordinates=true to receive tessellated XY coordinates for each loop " +
         "(increases response size). " +
         "Only items with status=GeometryReady are eligible for Phase 3 room creation. " +
         "Phase 2: strictly read-only — does not create rooms, room separation lines, " +
         "shared parameters, or any other model elements.")]
    public async Task<string> IfcPreviewSpaceGeometry(
        [Description("Element id of the RevitLinkInstance to inspect (integer). Obtain from ifc_list_links.")]
        long linkInstanceId,
        [Description("Optional list of linked element IDs to process. If empty, all IFC Space candidates in the link are processed.")]
        long[]? linkedElementIds = null,
        [Description("If true, include tessellated loop coordinates in the response. Increases response size. Default false.")]
        bool includeLoopCoordinates = false,
        [Description("Maximum number of XY coordinate points returned per loop when includeLoopCoordinates is true. Default 250.")]
        int maxCoordinatePoints = 250,
        [Description("Endpoint gap below this threshold (mm) is snapped during loop cleanup. Default 3 mm.")]
        double endpointSnapToleranceMm = 3.0,
        [Description("Segments shorter than this threshold (mm) are removed during loop cleanup. Default 1 mm.")]
        double tinySegmentToleranceMm = 1.0,
        [Description("Maximum angle in degrees from vertical for a face normal to be classified as horizontal. Default 2 degrees.")]
        double horizontalFaceToleranceDegrees = 2.0,
        [Description("Maximum vertical offset (mm) between a space bottom elevation and the matched Level. Default 300 mm.")]
        double levelMatchToleranceMm = 300.0,
        [Description("Maximum number of space candidates to return. Default 1000.")]
        int maxResults = 1000,
        [Description("Highest-priority Room Name parameter.")] string? roomNameParameter = null,
        [Description("Highest-priority Room Number parameter.")] string? roomNumberParameter = null,
        [Description("Highest-priority storey parameter.")] string? storeyParameter = null,
        [Description("Highest-priority validation area parameter.")] string? areaParameter = null,
        [Description("Enable AR_Ruum project defaults. Default true.")] bool enableArRuumDefaults = true,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["linkInstanceId"]                = linkInstanceId,
            ["linkedElementIds"]              = linkedElementIds ?? [],
            ["includeLoopCoordinates"]        = includeLoopCoordinates,
            ["maxCoordinatePoints"]           = maxCoordinatePoints,
            ["endpointSnapToleranceMm"]       = endpointSnapToleranceMm,
            ["tinySegmentToleranceMm"]        = tinySegmentToleranceMm,
            ["horizontalFaceToleranceDegrees"] = horizontalFaceToleranceDegrees,
            ["levelMatchToleranceMm"]         = levelMatchToleranceMm,
            ["maxResults"]                    = maxResults,
            ["roomNameParameter"]             = roomNameParameter,
            ["roomNumberParameter"]           = roomNumberParameter,
            ["storeyParameter"]               = storeyParameter,
            ["areaParameter"]                 = areaParameter,
            ["enableArRuumDefaults"]          = enableArRuumDefaults
        };
        var result = await pipeClient.SendAsync("ifc_preview_space_geometry", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "convert_ifc_spaces_to_rooms"),
     Description(
         "Phase 3 — Converts linked IFC Space elements into native Revit Rooms. " +
         "For each IFC Space: extracts the floor-plan footprint (solid → bottom face → CurveLoop), " +
         "matches to a host Level by elevation, creates Room Separation Lines from the boundary, " +
         "and places a Room at an interior placement point. " +
         "Number and Name are written from IFC metadata using built-in Room fields only " +
         "(no shared parameters, no IFC GUIDs, no Comments, no Extensible Storage). " +
         "Existing Rooms (matched by Number + Name + Level) are never overwritten. " +
         "One failed space never aborts the whole batch — each space has its own transaction. " +
         "Set dryRun=true to validate all spaces without making any model changes. " +
         "duplicateMode: 'skip_existing' (default) skips exact Number+Name+Level matches AND " +
         "Number+Level conflicts (different Name); 'skip_conflicts' is an alias for skip_existing; " +
         "'allow_conflicts' only skips exact matches, permitting creation when Number+Level conflict but Name differs. " +
         "By default, auto-collected elements must have a confirmed IfcSpace type; set " +
         "allowProbableConversion=true to also process probable candidates (with advisory warnings). " +
         "By default, floor-plan views are NOT created automatically; set " +
         "allowCreateMissingBoundaryViews=true to create minimal views when none exist for a Level. " +
         "Recommended workflow: ifc_list_links → ifc_preview_spaces → ifc_preview_space_geometry " +
         "(to identify GeometryReady spaces) → convert_ifc_spaces_to_rooms with dryRun=true " +
         "to confirm → convert_ifc_spaces_to_rooms with dryRun=false to commit.")]
    public async Task<string> ConvertIfcSpacesToRooms(
        [Description("Element id of the RevitLinkInstance to convert from (integer). Obtain from ifc_list_links.")]
        long linkInstanceId,
        [Description("Optional list of linked element IDs to convert. If empty, all IFC Space candidates in the link are processed. Obtain from ifc_preview_space_geometry (status=GeometryReady).")]
        long[]? linkedElementIds = null,
        [Description("Conflict handling: 'skip_existing' (default) skips exact Number+Name+Level matches AND Number+Level conflicts; 'skip_conflicts' is an alias; 'allow_conflicts' only skips exact matches.")]
        string duplicateMode = "skip_existing",
        [Description("Maximum vertical offset (mm) between a space bottom elevation and the matched Level. Default 300 mm.")]
        double levelMatchToleranceMm = 300.0,
        [Description("Endpoint gap below this threshold (mm) is snapped during loop cleanup. Default 3 mm.")]
        double endpointSnapToleranceMm = 3.0,
        [Description("Segments shorter than this threshold (mm) are removed during loop cleanup. Default 1 mm.")]
        double tinySegmentToleranceMm = 1.0,
        [Description("If true, write Room Number and Name from IFC metadata using built-in Room fields only. Default true.")]
        bool setRoomNameAndNumber = true,
        [Description("If true, create Room Separation Lines from the IFC Space footprint before placing the Room. Recommended. Default true.")]
        bool createRoomSeparationLines = true,
        [Description("If true, create Rooms for IFC Spaces with no Name. Default false.")]
        bool allowCreateWithoutName = false,
        [Description("If true, create Rooms for IFC Spaces with no Number. Default false.")]
        bool allowCreateWithoutNumber = false,
        [Description("If true, automatically create a minimal floor-plan view for Levels that have none. Default false — spaces on levels without a view are skipped with status SkippedNoView.")]
        bool allowCreateMissingBoundaryViews = false,
        [Description("If true, also process auto-collected elements that are probable (not confirmed) IFC Spaces. Applies only when linkedElementIds is empty. Default false.")]
        bool allowProbableConversion = false,
        [Description("If true, perform full validation but make no model modifications. Items that pass all checks are returned with status DryRunReady. Default false.")]
        bool dryRun = false,
        [Description("Highest-priority Room Name parameter.")] string? roomNameParameter = null,
        [Description("Highest-priority Room Number parameter.")] string? roomNumberParameter = null,
        [Description("Highest-priority storey parameter.")] string? storeyParameter = null,
        [Description("Highest-priority validation area parameter.")] string? areaParameter = null,
        [Description("Enable AR_Ruum project defaults. Default true.")] bool enableArRuumDefaults = true,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["linkInstanceId"]                  = linkInstanceId,
            ["linkedElementIds"]                = linkedElementIds ?? [],
            ["duplicateMode"]                   = duplicateMode,
            ["levelMatchToleranceMm"]           = levelMatchToleranceMm,
            ["endpointSnapToleranceMm"]         = endpointSnapToleranceMm,
            ["tinySegmentToleranceMm"]          = tinySegmentToleranceMm,
            ["setRoomNameAndNumber"]            = setRoomNameAndNumber,
            ["createRoomSeparationLines"]       = createRoomSeparationLines,
            ["allowCreateWithoutName"]          = allowCreateWithoutName,
            ["allowCreateWithoutNumber"]        = allowCreateWithoutNumber,
            ["allowCreateMissingBoundaryViews"] = allowCreateMissingBoundaryViews,
            ["allowProbableConversion"]         = allowProbableConversion,
            ["dryRun"]                          = dryRun,
            ["roomNameParameter"]               = roomNameParameter,
            ["roomNumberParameter"]             = roomNumberParameter,
            ["storeyParameter"]                 = storeyParameter,
            ["areaParameter"]                   = areaParameter,
            ["enableArRuumDefaults"]            = enableArRuumDefaults
        };
        var result = await pipeClient.SendAsync("convert_ifc_spaces_to_rooms", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "validate_ifc_space_room_conversion", ReadOnly = true),
     Description(
         "Phase 4 — Compares linked IFC Space elements against existing native Revit Rooms " +
         "using built-in fields only (Level, Number, Name, Location, Area). " +
         "Returns per-space match confidence (High/Medium/Low/Ambiguous/None), " +
         "location and area deltas (mm / %), detected possible renames, geometry changes, " +
         "and missing Rooms. " +
         "Read-only — makes no model changes. " +
         "Confidence levels: High = same Level+Number+Name; " +
         "Medium = same Level + one of Number/Name + location/area within tolerance; " +
         "Low = same Level + location/area only; " +
         "Ambiguous = multiple plausible matches; None = MissingRoom. " +
         "Recommended workflow: run this after convert_ifc_spaces_to_rooms to verify results, " +
         "or to identify stale data after IFC model updates. " +
         "Then use sync_ifc_space_room_data to apply controlled updates.")]
    public async Task<string> ValidateIfcSpaceRoomConversion(
        [Description("Element id of the RevitLinkInstance to validate against (integer). Obtain from ifc_list_links.")]
        long linkInstanceId,
        [Description("Optional list of linked element IDs to validate. If empty, all IFC Space candidates in the link are processed.")]
        long[]? linkedElementIds = null,
        [Description("Maximum vertical offset (mm) for Level matching. Default 300 mm.")]
        double levelMatchToleranceMm = 300.0,
        [Description("Maximum 2D distance (mm) between IFC placement point and Room location for co-location scoring. Default 1000 mm.")]
        double locationToleranceMm = 1000.0,
        [Description("Maximum relative area difference (%) between IFC Space area and Room area for area scoring. Default 10%.")]
        double areaTolerancePercent = 10.0,
        [Description("If true (default), run Phase 2 geometry extraction for precise location/area comparison. If false, use bounding-box approximations.")]
        bool includeGeometryComparison = true,
        [Description("Endpoint snap tolerance for geometry extraction (mm). Default 3 mm.")]
        double endpointSnapToleranceMm = 3.0,
        [Description("Tiny segment removal threshold for geometry extraction (mm). Default 1 mm.")]
        double tinySegmentToleranceMm = 1.0,
        [Description("Maximum number of validation items to return. Default 1000.")]
        int maxResults = 1000,
        [Description("Highest-priority Room Name parameter.")] string? roomNameParameter = null,
        [Description("Highest-priority Room Number parameter.")] string? roomNumberParameter = null,
        [Description("Highest-priority storey parameter.")] string? storeyParameter = null,
        [Description("Highest-priority validation area parameter.")] string? areaParameter = null,
        [Description("Enable AR_Ruum project defaults. Default true.")] bool enableArRuumDefaults = true,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["linkInstanceId"]            = linkInstanceId,
            ["linkedElementIds"]          = linkedElementIds ?? [],
            ["levelMatchToleranceMm"]     = levelMatchToleranceMm,
            ["locationToleranceMm"]       = locationToleranceMm,
            ["areaTolerancePercent"]      = areaTolerancePercent,
            ["includeGeometryComparison"] = includeGeometryComparison,
            ["endpointSnapToleranceMm"]   = endpointSnapToleranceMm,
            ["tinySegmentToleranceMm"]    = tinySegmentToleranceMm,
            ["maxResults"]                = maxResults,
            ["roomNameParameter"]         = roomNameParameter,
            ["roomNumberParameter"]       = roomNumberParameter,
            ["storeyParameter"]           = storeyParameter,
            ["areaParameter"]             = areaParameter,
            ["enableArRuumDefaults"]      = enableArRuumDefaults
        };
        var result = await pipeClient.SendAsync("validate_ifc_space_room_conversion", args, cancellationToken);
        return FormatResult(result);
    }

    [McpServerTool(Name = "sync_ifc_space_room_data"),
     Description(
         "Phase 4 — Updates built-in Room Number and/or Name for selected Rooms to match " +
         "their corresponding IFC Space's current metadata. " +
         "Only Room.Number and Room.Name (built-in fields) are written — " +
         "no shared parameters, no IFC GUIDs, no Comments, no Extensible Storage. " +
         "Defaults to dryRun=true. Set dryRun=false only after previewing planned changes. " +
         "Ambiguous matches are always blocked. " +
         "Medium-confidence updates require allowMediumConfidenceUpdates=true. " +
         "Low-confidence updates require allowLowConfidenceUpdates=true (strongly discouraged). " +
         "Each item is updated in its own transaction — one failure never aborts the batch. " +
         "items format: [{linkedElementId, roomId, updateName, updateNumber}]. " +
         "Recommended workflow: validate_ifc_space_room_conversion → " +
         "sync_ifc_space_room_data dryRun=true → sync_ifc_space_room_data dryRun=false.")]
    public async Task<string> SyncIfcSpaceRoomData(
        [Description("Element id of the RevitLinkInstance (integer). Obtain from ifc_list_links.")]
        long linkInstanceId,
        [Description("Array of sync items. Each item: {linkedElementId: long, roomId: long, updateName: bool, updateNumber: bool}. Obtain linkedElementId and roomId from validate_ifc_space_room_conversion results.")]
        object[] items,
        [Description("If true (default), report planned changes without modifying the model. Set false to commit updates.")]
        bool dryRun = true,
        [Description("If true, allow updates for Medium-confidence matches. Default false.")]
        bool allowMediumConfidenceUpdates = false,
        [Description("If true, allow updates for Low-confidence matches. Default false. Strongly discouraged.")]
        bool allowLowConfidenceUpdates = false,
        [Description("Highest-priority Room Name parameter.")] string? roomNameParameter = null,
        [Description("Highest-priority Room Number parameter.")] string? roomNumberParameter = null,
        [Description("Highest-priority storey parameter.")] string? storeyParameter = null,
        [Description("Highest-priority validation area parameter.")] string? areaParameter = null,
        [Description("Enable AR_Ruum project defaults. Default true.")] bool enableArRuumDefaults = true,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["linkInstanceId"]               = linkInstanceId,
            ["items"]                        = items,
            ["dryRun"]                       = dryRun,
            ["allowMediumConfidenceUpdates"] = allowMediumConfidenceUpdates,
            ["allowLowConfidenceUpdates"]    = allowLowConfidenceUpdates,
            ["roomNameParameter"]            = roomNameParameter,
            ["roomNumberParameter"]          = roomNumberParameter,
            ["storeyParameter"]              = storeyParameter,
            ["areaParameter"]                = areaParameter,
            ["enableArRuumDefaults"]         = enableArRuumDefaults
        };
        var result = await pipeClient.SendAsync("sync_ifc_space_room_data", args, cancellationToken);
        return FormatResult(result);
    }
}

