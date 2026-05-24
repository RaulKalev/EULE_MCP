using ClosedXML.Excel;

namespace RevitMCP.Addin.Excel;

/// <summary>
/// Copies row height and per-cell styles from a source row to a target row within the used range.
/// </summary>
public static class ExcelStyleCopyService
{
    public static void CopyRowStyle(IXLWorksheet ws, int sourceRowNumber, int targetRowNumber)
    {
        var sourceRow = ws.Row(sourceRowNumber);
        var targetRow = ws.Row(targetRowNumber);

        targetRow.Height = sourceRow.Height;

        var used = ws.RangeUsed();
        if (used == null) return;

        int firstCol = used.FirstColumn().ColumnNumber();
        int lastCol = used.LastColumn().ColumnNumber();

        for (int col = firstCol; col <= lastCol; col++)
        {
            var srcCell = sourceRow.Cell(col);
            var tgtCell = targetRow.Cell(col);
            tgtCell.Style = srcCell.Style;
        }
    }
}
