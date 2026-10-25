using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>Schedules (#103, docs/schedules.md): read definitions and rows, create, edit, place on sheets, export.</summary>
internal sealed partial class RevitMcpTools
{
    private const string ScheduleArgDescription = "Schedule name or element id (revit_list_schedules lists them).";

    private const string ScheduleFieldsDescription =
        "Columns in order. Each is a field name as revit_list_schedulable_fields lists it (\"Mark\", \"Room: Name\") or an object " +
        "{parameter, heading?, width? (mm), hidden?, totals?, format? {units: mm|m|m2|W|kW|VA|A|V|…, accuracy: 0.1, suppressTrailingZeros}, fieldType? (instance|type|room|…)}; " +
        "{count: true} or \"Count\" for the count column; {percentage: {of: \"<numeric field>\"}, heading}. Formula fields cannot be created through the Revit API.";

    private const string ScheduleFiltersDescription =
        "Filters [{field, type, value}] — type: equals, notEquals, greaterThan, greaterThanOrEqual, lessThan, lessThanOrEqual, contains, notContains, " +
        "beginsWith, notBeginsWith, endsWith, notEndsWith, hasValue, hasNoValue, hasParameter. Values: text, numbers (lengths in mm, other units in the " +
        "project's display unit), yes/no, or a level/phase/type name for fields that refer to an element. At most 8. A field that is not a column is added hidden.";

    private const string ScheduleSortDescription =
        "Sorting/grouping levels in order: field names, or [{field, descending?, header?, footer?, blankLine?}].";

    [McpServerTool(Name = "revit_get_schedule", ReadOnly = true),
     Description("Reads a schedule. include=definition (default): category, fields in order (name, column heading, parameter id, field type, hidden, calculated, totals, width mm, number format), filters, sorting/grouping (order, header, footer, blank line), itemize, grand total, view template, phase and the sheets it is on. include=rows: the table as Revit displays it (units and formatting included) — title, column headings and data rows, paged (page, pageSize up to 500); element ids only when the schedule is itemized and its rows are exactly its elements. include=all: both. Panel schedules are refused (schedule_kind_unsupported).")]
    public async Task<string> GetSchedule(
        [Description(ScheduleArgDescription)] string schedule,
        [Description("definition (default) | rows | all")] string include = "definition",
        [Description("1-based page of data rows (include=rows/all). Default 1.")] int page = 1,
        [Description("Data rows per page, default 100, max 500.")] int pageSize = 100,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["schedule"] = schedule ?? string.Empty,
            ["include"] = include ?? "definition",
            ["page"] = page,
            ["pageSize"] = pageSize
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_get_schedule", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_list_schedulable_fields", ReadOnly = true),
     Description("Lists the fields a schedule of a category can show — the names revit_create_schedule and revit_edit_schedule accept: name, qualifiedName (\"Room: Name\" for fields of related elements), parameterId, fieldType (Instance, ElementType, Count, Room, FromRoom, …), isShared, dataType. category (e.g. \"Electrical Fixtures\"), multiCategory=true, or schedule (an existing schedule's catalogue). nameFilter and fieldType narrow the list. When no schedule of the category exists, a temporary one is made in a transaction that is rolled back; the model is unchanged.")]
    public async Task<string> ListSchedulableFields(
        [Description("Category name, as Revit shows it.")] string? category = null,
        [Description("The fields of a multi-category schedule.")] bool multiCategory = false,
        [Description("An existing schedule (name or id) whose catalogue to list instead of a category's.")] string? schedule = null,
        [Description("Only fields whose name contains this (spaces, '-' and '_' ignored).")] string? nameFilter = null,
        [Description("Only fields of this type: instance, type, count, room, fromRoom, toRoom, space, projectInfo, material, …")] string? fieldType = null,
        [Description("Maximum fields returned (default 150, max 1000). A category can offer several hundred; narrow with nameFilter or fieldType.")] int limit = 150,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["category"] = category ?? string.Empty,
            ["multiCategory"] = multiCategory,
            ["schedule"] = schedule ?? string.Empty,
            ["nameFilter"] = nameFilter ?? string.Empty,
            ["fieldType"] = fieldType ?? string.Empty,
            ["limit"] = limit
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_list_schedulable_fields", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_preview_create_schedule", ReadOnly = true),
     Description("Previews revit_create_schedule WITHOUT changing the model: same arguments. Resolves the category and every field (missingFields with the nearest names), the filter types and how each value is read, and the sorting; then builds the schedule in a transaction that is rolled back and returns the fields as created, the row count and the first rows. Fails with field_not_found when a name does not resolve, unless allowMissingFields=true.")]
    public Task<string> PreviewCreateSchedule(
        [Description("Schedule name; made unique. Default: Revit's default name for the category.")] string? name = null,
        [Description("Category name (e.g. \"Electrical Fixtures\"). Not needed with multiCategory=true.")] string? category = null,
        [Description(ScheduleFieldsDescription)] object? fields = null,
        [Description(ScheduleFiltersDescription)] object? filters = null,
        [Description(ScheduleSortDescription)] object? sort = null,
        [Description("One row per instance (default true); false groups rows by the sort fields.")] bool itemize = true,
        [Description("Grand total row: none | title (title and totals) | count (count and totals) | totals (totals only) | titleCountTotals. Default: none.")] string? grandTotal = null,
        [Description("Schedule view template name to apply.")] string? template = null,
        [Description("Phase name for the schedule.")] string? phase = null,
        [Description("A multi-category schedule (no category).")] bool multiCategory = false,
        [Description("A key schedule of the category (ViewSchedule.CreateKeySchedule).")] bool keySchedule = false,
        [Description("Key schedules: the key parameter's name.")] string? keyName = null,
        [Description("Build without the fields, filters or steps that do not resolve instead of refusing.")] bool allowMissingFields = false,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendCreateSchedule("revit_preview_create_schedule", name, category, fields, filters, sort, itemize, grandTotal, template, phase,
            multiCategory, keySchedule, keyName, allowMissingFields, document, cancellationToken);

    [McpServerTool(Name = "revit_create_schedule"),
     Description("Creates a schedule that updates with the model. Requires approval. Run revit_preview_create_schedule first (same arguments). name, category (or multiCategory / keySchedule + keyName), fields in order (names from revit_list_schedulable_fields, with headings, widths in mm, hidden, totals, number format; Count; percentages), filters, sort with group headers/footers/blank lines, itemize, grandTotal, template, phase. One transaction: a name that does not resolve or a step Revit refuses rolls everything back — no half-finished schedule — unless allowMissingFields=true. Returns scheduleId, the fields as created and rowCount.")]
    public Task<string> CreateSchedule(
        [Description("Schedule name; made unique. Default: Revit's default name for the category.")] string? name = null,
        [Description("Category name (e.g. \"Electrical Fixtures\"). Not needed with multiCategory=true.")] string? category = null,
        [Description(ScheduleFieldsDescription)] object? fields = null,
        [Description(ScheduleFiltersDescription)] object? filters = null,
        [Description(ScheduleSortDescription)] object? sort = null,
        [Description("One row per instance (default true); false groups rows by the sort fields.")] bool itemize = true,
        [Description("Grand total row: none | title (title and totals) | count (count and totals) | totals (totals only) | titleCountTotals. Default: none.")] string? grandTotal = null,
        [Description("Schedule view template name to apply.")] string? template = null,
        [Description("Phase name for the schedule.")] string? phase = null,
        [Description("A multi-category schedule (no category).")] bool multiCategory = false,
        [Description("A key schedule of the category (ViewSchedule.CreateKeySchedule).")] bool keySchedule = false,
        [Description("Key schedules: the key parameter's name.")] string? keyName = null,
        [Description("Build without the fields, filters or steps that do not resolve instead of refusing.")] bool allowMissingFields = false,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendCreateSchedule("revit_create_schedule", name, category, fields, filters, sort, itemize, grandTotal, template, phase,
            multiCategory, keySchedule, keyName, allowMissingFields, document, cancellationToken);

    private async Task<string> SendCreateSchedule(
        string toolName, string? name, string? category, object? fields, object? filters, object? sort, bool itemize,
        string? grandTotal, string? template, string? phase, bool multiCategory, bool keySchedule, string? keyName,
        bool allowMissingFields, string? document, CancellationToken cancellationToken)
    {
        var args = new Dictionary<string, object?>
        {
            ["name"] = name ?? string.Empty,
            ["category"] = category ?? string.Empty,
            ["fields"] = ToJToken(fields),
            ["filters"] = ToJToken(filters),
            ["sort"] = ToJToken(sort),
            ["itemize"] = itemize,
            ["grandTotal"] = grandTotal ?? string.Empty,
            ["template"] = template ?? string.Empty,
            ["phase"] = phase ?? string.Empty,
            ["multiCategory"] = multiCategory,
            ["keySchedule"] = keySchedule,
            ["keyName"] = keyName ?? string.Empty,
            ["allowMissingFields"] = allowMissingFields
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync(toolName, args, cancellationToken));
    }

    [McpServerTool(Name = "revit_preview_edit_schedule", ReadOnly = true),
     Description("Previews revit_edit_schedule WITHOUT changing the model: same arguments. Returns the field order before and after, every step, names that do not resolve (with the nearest names) and — from a trial run that is rolled back — the steps Revit would refuse and the row count after the edit.")]
    public Task<string> PreviewEditSchedule(
        [Description(ScheduleArgDescription)] string schedule,
        [Description("Fields to add, same shapes as revit_create_schedule's fields, each with an optional insertAt (0-based position).")] object? addFields = null,
        [Description("Fields to take out, by name or 0-based index.")] string[]? removeFields = null,
        [Description("New field order by name; fields not listed follow in their current order.")] string[]? order = null,
        [Description("New column headings: {\"field\": \"heading\"}.")] object? rename = null,
        [Description("Fields to hide.")] string[]? hide = null,
        [Description("Hidden fields to show.")] string[]? show = null,
        [Description("Number formats: {\"field\": {units, accuracy, suppressTrailingZeros}}.")] object? format = null,
        [Description("Column widths in mm: {\"field\": 30}.")] object? widths = null,
        [Description("Replaces every filter (same shape as revit_create_schedule's filters); [] removes them all.")] object? setFilters = null,
        [Description("Replaces the sorting/grouping (same shape as revit_create_schedule's sort); [] removes it.")] object? setSort = null,
        [Description("Itemize every instance (true) or group rows (false). Omit to keep.")] bool? itemize = null,
        [Description("Grand total: none | title | count | totals | titleCountTotals. Omit to keep.")] string? grandTotal = null,
        [Description("Apply the rest when some names do not resolve or steps are refused.")] bool allowMissingFields = false,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendEditSchedule("revit_preview_edit_schedule", schedule, addFields, removeFields, order, rename, hide, show, format, widths,
            setFilters, setSort, itemize, grandTotal, allowMissingFields, document, cancellationToken);

    [McpServerTool(Name = "revit_edit_schedule"),
     Description("Edits a schedule. Requires approval. Run revit_preview_edit_schedule first (same arguments). addFields (with insertAt), removeFields, order, rename {field: heading}, hide, show, format {field: {units, accuracy, suppressTrailingZeros}}, widths {field: mm}, setFilters and setSort (each replaces all; [] clears), itemize, grandTotal. Key schedules: fields only. Refuses view templates, title-block revision schedules and panel schedules (schedule_kind_unsupported). One transaction: a name that does not resolve or a refused step rolls everything back unless allowMissingFields=true.")]
    public Task<string> EditSchedule(
        [Description(ScheduleArgDescription)] string schedule,
        [Description("Fields to add, same shapes as revit_create_schedule's fields, each with an optional insertAt (0-based position).")] object? addFields = null,
        [Description("Fields to take out, by name or 0-based index.")] string[]? removeFields = null,
        [Description("New field order by name; fields not listed follow in their current order.")] string[]? order = null,
        [Description("New column headings: {\"field\": \"heading\"}.")] object? rename = null,
        [Description("Fields to hide.")] string[]? hide = null,
        [Description("Hidden fields to show.")] string[]? show = null,
        [Description("Number formats: {\"field\": {units, accuracy, suppressTrailingZeros}}.")] object? format = null,
        [Description("Column widths in mm: {\"field\": 30}.")] object? widths = null,
        [Description("Replaces every filter (same shape as revit_create_schedule's filters); [] removes them all.")] object? setFilters = null,
        [Description("Replaces the sorting/grouping (same shape as revit_create_schedule's sort); [] removes it.")] object? setSort = null,
        [Description("Itemize every instance (true) or group rows (false). Omit to keep.")] bool? itemize = null,
        [Description("Grand total: none | title | count | totals | titleCountTotals. Omit to keep.")] string? grandTotal = null,
        [Description("Apply the rest when some names do not resolve or steps are refused.")] bool allowMissingFields = false,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendEditSchedule("revit_edit_schedule", schedule, addFields, removeFields, order, rename, hide, show, format, widths,
            setFilters, setSort, itemize, grandTotal, allowMissingFields, document, cancellationToken);

    private async Task<string> SendEditSchedule(
        string toolName, string schedule, object? addFields, string[]? removeFields, string[]? order, object? rename,
        string[]? hide, string[]? show, object? format, object? widths, object? setFilters, object? setSort, bool? itemize,
        string? grandTotal, bool allowMissingFields, string? document, CancellationToken cancellationToken)
    {
        var args = new Dictionary<string, object?>
        {
            ["schedule"] = schedule ?? string.Empty,
            ["addFields"] = ToJToken(addFields),
            ["removeFields"] = removeFields ?? [],
            ["order"] = order ?? [],
            ["rename"] = ToJToken(rename),
            ["hide"] = hide ?? [],
            ["show"] = show ?? [],
            ["format"] = ToJToken(format),
            ["widths"] = ToJToken(widths),
            ["grandTotal"] = grandTotal ?? string.Empty,
            ["allowMissingFields"] = allowMissingFields
        };
        // Absent and empty differ here: an empty list clears the filters or sorting, an absent one keeps them.
        if (setFilters != null) args["setFilters"] = ToJToken(setFilters);
        if (setSort != null) args["setSort"] = ToJToken(setSort);
        if (itemize.HasValue) args["itemize"] = itemize.Value;
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync(toolName, args, cancellationToken));
    }

    [McpServerTool(Name = "revit_place_schedule_on_sheet"),
     Description("Places a schedule on a sheet (ScheduleSheetInstance — revit_place_views_on_sheets' viewports cannot hold schedules). Requires approval. schedule (name or id) or scheduleId; sheet (number or id). Position: xMm,yMm = the table's top-left corner in sheet mm; or nextTo {viewportId, side: right|below|left|above (default right), gapMm (default 10)} — viewportId may be a viewport, a view placed on the sheet (revit_get_sheet_viewports) or a placed schedule; tops are aligned for right/left, left edges for below/above. Neither: centred on the title block. The table is measured after placing and moved so its corner lands exactly there; overlaps with other viewports and leaving the title block come back as warnings. A schedule already on that sheet is refused unless allowDuplicate=true.")]
    public async Task<string> PlaceScheduleOnSheet(
        [Description("Sheet number or sheet element id.")] string sheet,
        [Description(ScheduleArgDescription)] string? schedule = null,
        [Description("Schedule element id (instead of schedule).")] long scheduleId = 0,
        [Description("X of the table's top-left corner on the sheet, mm.")] double? xMm = null,
        [Description("Y of the table's top-left corner on the sheet, mm.")] double? yMm = null,
        [Description("Place next to a viewport: {viewportId, side: right|below|left|above, gapMm}.")] object? nextTo = null,
        [Description("Place it even when the schedule is already on this sheet.")] bool allowDuplicate = false,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["sheet"] = sheet ?? string.Empty,
            ["schedule"] = schedule ?? string.Empty,
            ["scheduleId"] = scheduleId,
            ["nextTo"] = ToJToken(nextTo),
            ["allowDuplicate"] = allowDuplicate
        };
        if (xMm.HasValue) args["xMm"] = xMm.Value;
        if (yMm.HasValue) args["yMm"] = yMm.Value;
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_place_schedule_on_sheet", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_export_schedule"),
     Description("Exports a schedule to a file. Requires approval (writes a file). format = csv (default) or txt (tab-separated) through Revit's own ViewSchedule.Export, or xlsx — the table as displayed, title and headings bold, headings frozen. folder (default Documents\\RKTools\\RevitMCP\\Exports; must be allowed by the file access policy), fileName (default the schedule name; an existing file is never overwritten). delimiter for csv: \",\" (default) or \";\". title and headersFootersBlanks (default true). Returns filePath, rows and columns.")]
    public async Task<string> ExportSchedule(
        [Description(ScheduleArgDescription)] string schedule,
        [Description("csv (default) | txt | xlsx")] string format = "csv",
        [Description("Target folder; default Documents\\RKTools\\RevitMCP\\Exports.")] string? folder = null,
        [Description("File name; default the schedule name. The extension follows format.")] string? fileName = null,
        [Description("csv field delimiter: \",\" (default) or \";\".")] string delimiter = ",",
        [Description("Include the schedule title (default true).")] bool title = true,
        [Description("Include group headers, footers and blank lines (default true).")] bool headersFootersBlanks = true,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["schedule"] = schedule ?? string.Empty,
            ["format"] = format ?? "csv",
            ["folder"] = folder ?? string.Empty,
            ["fileName"] = fileName ?? string.Empty,
            ["delimiter"] = delimiter ?? ",",
            ["title"] = title,
            ["headersFootersBlanks"] = headersFootersBlanks
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_export_schedule", args, cancellationToken));
    }
}
