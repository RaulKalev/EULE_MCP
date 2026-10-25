using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Schedules;

/// <summary>A schedule's table as the user sees it: title rows, column heading rows and the data rows (text as displayed).</summary>
internal sealed class ScheduleTable
{
    public List<string[]> TitleRows { get; } = new();
    public List<string[]> HeadingRows { get; } = new();
    public List<string[]> DataRows { get; } = new();
    public List<string> Columns { get; } = new();
    public int BodyRows { get; set; }
    public int SkippedSeparatorRows { get; set; }
}

/// <summary>
/// Reads schedule table data with ViewSchedule.GetCellText (#103), which returns each cell exactly as Revit displays
/// it — units, formatting, group headers and footers, the grand total. The column headings sit at the top of the body
/// section; they are split off with <see cref="ScheduleTableMath.DataStartRow"/>.
/// </summary>
internal static class ScheduleTableReader
{
    /// <summary>Reads the whole table (title, headings, every data row). Callers page <see cref="ScheduleTable.DataRows"/>.</summary>
    public static ScheduleTable Read(ViewSchedule schedule)
    {
        var table = new ScheduleTable();
        var data = schedule.GetTableData();

        var definition = schedule.Definition;
        for (var i = 0; i < definition.GetFieldCount(); i++)
        {
            var field = definition.GetField(i);
            if (!field.IsHidden) table.Columns.Add(field.ColumnHeading ?? string.Empty);
        }

        var header = data.GetSectionData(SectionType.Header);
        if (header != null)
            table.TitleRows.AddRange(ReadRows(schedule, SectionType.Header, header));

        var body = data.GetSectionData(SectionType.Body);
        if (body == null) return table;
        var rows = ReadRows(schedule, SectionType.Body, body);
        table.BodyRows = rows.Count;

        var leading = rows.Take(8).Select(r => (IReadOnlyList<string>)r).ToList();
        var start = ScheduleTableMath.DataStartRow(leading, table.Columns);
        if (start > 0)
        {
            // The heading row is the last non-blank row before the data; a blank separator after it is dropped.
            var separator = start >= 2 && ScheduleTableMath.IsBlank(rows[start - 1]) ? 1 : 0;
            table.SkippedSeparatorRows = separator;
            table.HeadingRows.AddRange(rows.Take(start - separator));
        }
        table.DataRows.AddRange(rows.Skip(start));
        return table;
    }

    private static List<string[]> ReadRows(ViewSchedule schedule, SectionType section, TableSectionData data)
    {
        var rows = new List<string[]>();
        if (data.NumberOfRows <= 0 || data.NumberOfColumns <= 0) return rows;
        for (var r = data.FirstRowNumber; r <= data.LastRowNumber; r++)
        {
            var cells = new string[data.LastColumnNumber - data.FirstColumnNumber + 1];
            for (var c = data.FirstColumnNumber; c <= data.LastColumnNumber; c++)
            {
                string text;
                try { text = schedule.GetCellText(section, r, c) ?? string.Empty; }
                catch { text = string.Empty; }
                cells[c - data.FirstColumnNumber] = text;
            }
            rows.Add(cells);
        }
        return rows;
    }

    /// <summary>
    /// The elements the schedule lists, when that set can be stated: Revit does not expose which element a row is, and
    /// the view's element collector is not in table order. So ids come only for an itemized schedule whose collector
    /// count equals its data rows (no group headers, footers or totals in between); otherwise null and the reason.
    /// </summary>
    public static (List<long>? Ids, string Note) ElementIds(Document doc, ViewSchedule schedule, ScheduleTable table)
    {
        var definition = schedule.Definition;
        if (!definition.IsItemized)
            return (null, "Not itemized: a row stands for several elements, so no element ids are given.");
        if (definition.IsKeySchedule)
            return (null, "A key schedule lists key rows, not model elements.");

        List<ElementId> ids;
        try { ids = new FilteredElementCollector(doc, schedule.Id).WhereElementIsNotElementType().ToElementIds().ToList(); }
        catch (Exception ex) { return (null, $"Revit could not list the schedule's elements ({ex.Message})."); }

        if (ids.Count != table.DataRows.Count)
            return (null, $"The schedule shows {table.DataRows.Count} data row(s) but lists {ids.Count} element(s) (group headers, footers, blank lines or the grand total are rows too), so ids cannot be given per row.");
        return (ids.Select(id => id.Value).ToList(),
            "The elements the schedule lists — the same set as its rows, but Revit gives no row order, so do not pair them with rows by position.");
    }
}
