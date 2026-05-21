using ClosedXML.Excel;
using RevitMCP.Addin.Query;

namespace RevitMCP.Addin.Export;

public class ExcelExportRequest
{
    public string FilePath { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string CentralPath { get; set; } = string.Empty;
    public string RevitUser { get; set; } = string.Empty;
    public string CategoryFilter { get; set; } = string.Empty;
    public List<ParameterFilterDto> ParameterFilters { get; set; } = new();
    public List<GroupKeyOptions> GroupBy { get; set; } = new();
    public List<string> ParameterColumns { get; set; } = new();
    public string OutputMode { get; set; } = "Both"; // Elements, Groups, Both
    public ElementQueryResult? QueryResult { get; set; }
    public GroupingResult? GroupingResult { get; set; }
}

public class ExcelExportService
{
    public void Export(ExcelExportRequest req)
    {
        using var wb = new XLWorkbook();

        AddSummarySheet(wb, req);

        if ((req.OutputMode == "Both" || req.OutputMode == "Groups") && req.GroupingResult != null)
            AddGroupsSheet(wb, req);

        if (req.OutputMode == "Both" || req.OutputMode == "Elements")
            AddElementsSheet(wb, req);

        AddParametersSheet(wb, req);

        wb.SaveAs(req.FilePath);
    }

    private static void AddSummarySheet(XLWorkbook wb, ExcelExportRequest req)
    {
        var ws = wb.Worksheets.Add("Summary");
        var row = 1;

        void AddRow(string label, string value)
        {
            ws.Cell(row, 1).Value = label;
            ws.Cell(row, 2).Value = value;
            ws.Cell(row, 1).Style.Font.Bold = true;
            row++;
        }

        AddRow("Model", req.ModelName);
        AddRow("Central Path", req.CentralPath);
        AddRow("Export Time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        AddRow("Revit User", req.RevitUser);
        AddRow("Category Filter", req.CategoryFilter);
        if (req.ParameterFilters.Count > 0)
            AddRow("Parameter Filters", string.Join("; ", req.ParameterFilters.Select(f => $"{f.ParameterName} {f.Operator} '{f.Value}'")));
        if (req.QueryResult != null)
        {
            AddRow("Total Matched Elements", req.QueryResult.TotalMatched.ToString());
            AddRow("Returned Elements", req.QueryResult.Elements.Count.ToString());
        }
        if (req.GroupingResult != null)
            AddRow("Group Rows", req.GroupingResult.TotalGroups.ToString());

        ws.Column(1).Width = 22;
        ws.Column(2).AdjustToContents();
    }

    private static void AddGroupsSheet(XLWorkbook wb, ExcelExportRequest req)
    {
        var gr = req.GroupingResult!;
        if (gr.GroupsFlat.Count == 0) return;

        var ws = wb.Worksheets.Add("Groups");
        var keyNames = gr.GroupsFlat[0].Keys.Keys.ToList();
        var headers = keyNames.Concat(new[] { "Count" }).ToList();

        // Header row
        for (var c = 0; c < headers.Count; c++)
        {
            ws.Cell(1, c + 1).Value = headers[c];
            ws.Cell(1, c + 1).Style.Font.Bold = true;
        }

        // Data rows
        for (var r = 0; r < gr.GroupsFlat.Count; r++)
        {
            var row = gr.GroupsFlat[r];
            for (var c = 0; c < keyNames.Count; c++)
                ws.Cell(r + 2, c + 1).Value = row.Keys.TryGetValue(keyNames[c], out var v) ? v : string.Empty;
            ws.Cell(r + 2, keyNames.Count + 1).Value = row.Count;
        }

        FormatAsTable(ws, 1, 1, gr.GroupsFlat.Count + 1, headers.Count, "GroupsTable");
    }

    private static void AddElementsSheet(XLWorkbook wb, ExcelExportRequest req)
    {
        var qr = req.QueryResult;
        if (qr == null || qr.Elements.Count == 0) return;

        var ws = wb.Worksheets.Add("Elements");

        // Determine columns: fixed fields + selected parameters
        var paramCols = req.ParameterColumns.Count > 0
            ? req.ParameterColumns
            : qr.Elements.SelectMany(e => e.Parameters.Keys).Distinct().OrderBy(k => k).ToList();

        var fixedHeaders = new[] { "ElementId", "UniqueId", "Category", "Family", "Type", "Level" };
        var allHeaders = fixedHeaders.Concat(paramCols).ToList();

        // Header
        for (var c = 0; c < allHeaders.Count; c++)
        {
            ws.Cell(1, c + 1).Value = allHeaders[c];
            ws.Cell(1, c + 1).Style.Font.Bold = true;
        }

        // Data
        for (var r = 0; r < qr.Elements.Count; r++)
        {
            var elem = qr.Elements[r];
            ws.Cell(r + 2, 1).Value = elem.ElementId;
            ws.Cell(r + 2, 2).Value = elem.UniqueId;
            ws.Cell(r + 2, 3).Value = elem.Category;
            ws.Cell(r + 2, 4).Value = elem.Family;
            ws.Cell(r + 2, 5).Value = elem.Type;
            ws.Cell(r + 2, 6).Value = elem.Level;

            for (var c = 0; c < paramCols.Count; c++)
            {
                var val = elem.Parameters.TryGetValue(paramCols[c], out var p) ? p.Value : string.Empty;
                ws.Cell(r + 2, fixedHeaders.Length + c + 1).Value = val;
            }
        }

        FormatAsTable(ws, 1, 1, qr.Elements.Count + 1, allHeaders.Count, "ElementsTable");
    }

    private static void AddParametersSheet(XLWorkbook wb, ExcelExportRequest req)
    {
        var qr = req.QueryResult;
        if (qr == null || qr.Elements.Count == 0) return;

        // Skip the parameters detail sheet when all parameters were returned (no specific columns
        // requested) because the cross-product of elements × params can reach hundreds of thousands
        // of rows and cause timeouts or OOM.  When the caller has filtered to specific columns the
        // sheet is small and genuinely useful.
        if (req.ParameterColumns.Count == 0) return;

        const int maxRows = 10_000;

        var ws = wb.Worksheets.Add("Parameters");
        var headers = new[] { "ElementId", "ParameterName", "Scope", "Value", "IsShared", "Guid", "StorageType", "IsReadOnly" };

        for (var c = 0; c < headers.Length; c++)
        {
            ws.Cell(1, c + 1).Value = headers[c];
            ws.Cell(1, c + 1).Style.Font.Bold = true;
        }

        var row = 2;
        var capped = false;
        foreach (var elem in qr.Elements)
        {
            foreach (var kv in elem.Parameters)
            {
                if (row - 1 > maxRows)
                {
                    capped = true;
                    break;
                }
                var p = kv.Value;
                ws.Cell(row, 1).Value = elem.ElementId;
                ws.Cell(row, 2).Value = p.Name;
                ws.Cell(row, 3).Value = p.Scope;
                ws.Cell(row, 4).Value = p.Value;
                ws.Cell(row, 5).Value = p.IsShared;
                ws.Cell(row, 6).Value = p.Guid ?? string.Empty;
                ws.Cell(row, 7).Value = p.StorageType;
                ws.Cell(row, 8).Value = p.IsReadOnly;
                row++;
            }
            if (capped) break;
        }

        if (capped)
        {
            var noteRow = row;
            ws.Cell(noteRow, 1).Value = "(truncated)";
            ws.Cell(noteRow, 2).Value = $"Parameters sheet capped at {maxRows} rows. Request fewer elements or specific parameter columns.";
        }

        if (row > 2)
            FormatAsTable(ws, 1, 1, row - 1, headers.Length, "ParametersTable");
    }

    private static void FormatAsTable(IXLWorksheet ws, int firstRow, int firstCol, int lastRow, int lastCol, string name)
    {
        if (lastRow <= firstRow) return;
        var range = ws.Range(firstRow, firstCol, lastRow, lastCol);
        var table = range.CreateTable(name);
        table.ShowAutoFilter = true;
        ws.Row(firstRow).Style.Font.Bold = true;
        ws.SheetView.FreezeRows(1);
        for (var c = firstCol; c <= lastCol; c++)
            ws.Column(c).AdjustToContents(1, Math.Min(lastRow, 200));
    }
}
