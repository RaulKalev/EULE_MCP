using System.IO;
using ClosedXML.Excel;
using RevitMCP.Core.Models.Excel;

namespace RevitMCP.Addin.Excel;

/// <summary>
/// Reads workbook structure and cell ranges without modifying the file.
/// </summary>
public static class ExcelWorkbookInspector
{
    private static readonly string[] SupportedExtensions = [".xlsx", ".xlsm"];

    public static (ExcelWorkbookInspectionDto? Result, string? Error) Inspect(
        string filePath, bool includePreviewRows, int previewRowCount)
    {
        if (!File.Exists(filePath))
            return (null, $"File not found: {filePath}");

        var ext = Path.GetExtension(filePath);
        if (!SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            return (null, $"Unsupported file type '{ext}'. Only .xlsx and .xlsm are supported.");

        try
        {
            using var wb = new XLWorkbook(filePath);
            var dto = new ExcelWorkbookInspectionDto { FilePath = filePath };

            foreach (var ws in wb.Worksheets)
            {
                if (ws.Visibility != XLWorksheetVisibility.Visible) continue;

                var used = ws.RangeUsed();
                var wsInfo = new ExcelWorksheetInfoDto
                {
                    Name = ws.Name,
                    UsedRange = used?.RangeAddress.ToString() ?? "empty",
                    RowCount = used?.RowCount() ?? 0,
                    ColumnCount = used?.ColumnCount() ?? 0
                };

                var (headerRow, headers) = ExcelHeaderDetector.DetectHeaders(ws);
                wsInfo.Headers = headers;

                if (includePreviewRows && used != null && headerRow > 0)
                {
                    int dataStart = headerRow + 1;
                    int dataEnd = Math.Min(dataStart + previewRowCount - 1, used.LastRow().RowNumber());

                    for (int r = dataStart; r <= dataEnd; r++)
                    {
                        var rowValues = new List<string>();
                        for (int c = used.FirstColumn().ColumnNumber(); c <= used.LastColumn().ColumnNumber(); c++)
                            rowValues.Add(ws.Cell(r, c).GetString());
                        wsInfo.PreviewRows.Add(rowValues);
                    }
                }

                dto.Worksheets.Add(wsInfo);
            }

            return (dto, null);
        }
        catch (Exception ex)
        {
            return (null, $"Failed to open workbook: {ex.Message}");
        }
    }

    public static (object? Result, string? Error) ReadRange(
        string filePath, string worksheetName, string rangeAddress, bool includeFormulas)
    {
        if (!File.Exists(filePath))
            return (null, $"File not found: {filePath}");

        try
        {
            using var wb = new XLWorkbook(filePath);

            if (!wb.TryGetWorksheet(worksheetName, out var ws))
                return (null, $"Worksheet '{worksheetName}' not found.");

            IXLRange range;
            try { range = ws.Range(rangeAddress); }
            catch { return (null, $"Invalid range address '{rangeAddress}'."); }

            var rows = new List<object>();
            foreach (var row in range.Rows())
            {
                var cells = row.Cells().Select(cell => (object)new
                {
                    address = cell.Address.ToString(),
                    value = cell.GetString(),
                    formula = includeFormulas && cell.HasFormula ? cell.FormulaA1 : null,
                    dataType = cell.DataType.ToString()
                }).ToList();

                rows.Add(cells);
            }

            return (new { worksheetName, rangeAddress, rowCount = rows.Count, rows }, null);
        }
        catch (Exception ex)
        {
            return (null, $"Failed to read range: {ex.Message}");
        }
    }
}
