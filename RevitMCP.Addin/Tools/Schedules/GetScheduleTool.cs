using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Schedules;
using RevitMCP.Core.Models;
using RevitMCP.Core.Safety;

namespace RevitMCP.Addin.Tools.Schedules;

/// <summary>revit_get_schedule (#103): a schedule's definition and, on request, its rows as the user sees them.</summary>
public sealed class GetScheduleTool : IRevitMcpTool
{
    public string Name => "revit_get_schedule";

    public string Description =>
        "Reads a schedule: schedule (name or element id). include=definition (default): category, fields in order " +
        "(name, column heading, parameter id, field type, hidden, calculated, totals, width mm, number format), filters " +
        "(field, type, value), sorting/grouping (order, header, footer, blank line), itemize, grand total, template, phase " +
        "and the sheets it is placed on. include=rows: the table as Revit displays it (GetCellText: units and formatting " +
        "included), title, column headings and data rows, paged with page (1-based) and pageSize (default 100, max 500); " +
        "elementIds only when the schedule is itemized and its rows are exactly its elements. include=all: both.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Schedules;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return Task.FromResult(documentFailure!);
        var doc = target.Document;
        var args = request.Arguments;

        var schedule = ScheduleLookup.Resolve(doc, request, ToolArguments.GetString(args, "schedule"), 0, forEdit: false, out var failure);
        if (schedule == null) return Task.FromResult(failure!);

        var include = ToolArguments.GetString(args, "include", "definition").Trim().ToLowerInvariant();
        if (include.Length == 0) include = "definition";
        if (include is not ("definition" or "rows" or "all"))
            return Task.FromResult(ScheduleToolSupport.Fail(request, "validation_failed", $"include '{include}' is not one of definition, rows, all."));

        var warnings = new List<string>();
        object? definition = include is "definition" or "all" ? ScheduleDefinitionReader.Describe(doc, schedule) : null;

        object? rows = null;
        var message = $"Schedule '{schedule.Name}'";
        if (include is "rows" or "all")
        {
            ScheduleTable table;
            try { table = ScheduleTableReader.Read(schedule); }
            catch (Exception ex)
            {
                return Task.FromResult(ScheduleToolSupport.Fail(request, "tool_execution_failed", $"Revit could not read the table of '{schedule.Name}': {ex.Message}"));
            }

            var requestedSize = ToolArguments.GetInt(args, "pageSize", 0);
            var pageSize = QueryGuard.NormalizePageSize(requestedSize > 0 ? requestedSize : null);
            if (requestedSize > pageSize) warnings.Add($"pageSize {requestedSize} was clamped to {pageSize}.");
            var page = Math.Max(1, ToolArguments.GetInt(args, "page", 1));
            var (start, count, totalPages) = ScheduleTableMath.Page(table.DataRows.Count, page, pageSize);
            if (count == 0 && table.DataRows.Count > 0)
                warnings.Add($"Page {page} is past the end: the schedule has {totalPages} page(s) of {pageSize} rows.");

            var (ids, idsNote) = ScheduleTableReader.ElementIds(doc, schedule, table);
            if (ids != null && ids.Count > pageSize * 4)
            {
                idsNote += $" Only the first {pageSize * 4} ids are listed.";
                ids = ids.Take(pageSize * 4).ToList();
            }
            if (schedule.IsDataOutOfDate())
                warnings.Add("Revit reports the schedule's data as out of date; open the schedule in Revit or regenerate to refresh it.");

            rows = new
            {
                title = string.Join(" ", table.TitleRows.SelectMany(r => r).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct()),
                columns = table.Columns,
                headingRows = table.HeadingRows,
                totalRows = table.DataRows.Count,
                page,
                pageSize,
                totalPages,
                hasMore = start + count < table.DataRows.Count,
                firstRow = start,
                rows = table.DataRows.Skip(start).Take(count).ToList(),
                elementIds = ids,
                elementIdsNote = idsNote
            };
            message += $": {table.DataRows.Count} data row(s), page {page} of {Math.Max(1, totalPages)}";
        }
        message += ".";

        return Task.FromResult(ScheduleToolSupport.Ok(request, sw, message, new
        {
            scheduleId = schedule.Id.Value,
            name = schedule.Name,
            include,
            definition,
            table = rows
        }, warnings));
    }
}
