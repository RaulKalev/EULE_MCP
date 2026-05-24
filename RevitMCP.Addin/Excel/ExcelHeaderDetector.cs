using ClosedXML.Excel;

namespace RevitMCP.Addin.Excel;

/// <summary>
/// Detects the header row and maps header names to column indices in ClosedXML worksheets.
/// </summary>
public static class ExcelHeaderDetector
{
    /// <summary>
    /// Detects the likely header row: the first non-empty row in the used range.
    /// Returns (rowNumber 1-based, header values). Returns (0, empty list) when no data found.
    /// </summary>
    public static (int Row, List<string> Headers) DetectHeaders(IXLWorksheet ws)
    {
        var used = ws.RangeUsed();
        if (used == null) return (0, []);

        int firstCol = used.FirstColumn().ColumnNumber();
        int lastCol = used.LastColumn().ColumnNumber();

        for (int row = used.FirstRow().RowNumber(); row <= used.LastRow().RowNumber(); row++)
        {
            var headers = new List<string>();
            bool hasContent = false;

            for (int col = firstCol; col <= lastCol; col++)
            {
                var cellValue = ws.Cell(row, col).GetString();
                headers.Add(cellValue);
                if (!string.IsNullOrWhiteSpace(cellValue)) hasContent = true;
            }

            if (hasContent) return (row, headers);
        }

        return (0, []);
    }

    /// <summary>
    /// Finds the 1-based column index for a header name (case-insensitive exact match).
    /// Returns 0 if not found.
    /// </summary>
    public static int FindColumnByHeader(IXLWorksheet ws, int headerRow, string headerName)
    {
        var used = ws.RangeUsed();
        if (used == null) return 0;

        int firstCol = used.FirstColumn().ColumnNumber();
        int lastCol = used.LastColumn().ColumnNumber();

        for (int col = firstCol; col <= lastCol; col++)
        {
            var cellValue = ws.Cell(headerRow, col).GetString();
            if (string.Equals(cellValue, headerName, StringComparison.OrdinalIgnoreCase))
                return col;
        }

        return 0;
    }
}
